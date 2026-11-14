using LinkshellManagerDiscordApp.Data;
using LinkshellManagerDiscordApp.Models;
using LinkshellManagerDiscordApp.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LinkshellManager.Tests;

// Every way the app writes a Past Event (an EventHistory row, its AppUserEventHistory rows and the
// ledger rows that point at them), with EVERY field pinned:
//
//   1. a timed event's End Event          (EventEndService.EndEventCoreAsync)
//   2. an HNM camp's End Camp             (HnmCampReviewHandoffService, archive staged at End Camp)
//   3. that camp's review being Posted     (WindowEventDkpLedgerService re-syncs the archive roster)
//   4. a legacy review row with no archive (WindowEventDkpLedgerService builds one at Post)
//   5. a member added to a Past Event      (EventHistoryEditService.AddParticipantAsync)
//
// Written before those paths were moved onto one shared builder, so the move can be proven to
// write the same rows field for field. Where two paths differ on purpose (how StartTime and
// Duration are read, which time is the TimeStamp, whether a party setup or DkpPerHour is kept),
// the difference is pinned here so it can only change on purpose.
public class PastEventRowsPinnedTests
{
    private const int Ls = 7;
    private const int TemplateId = 30;
    private const int SnapshotSetupId = 31;

    private static ApplicationDbContext NewDb(string? name = null) =>
        new(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(name ?? Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options);

    private static DkpPoolResolver NewPools(ApplicationDbContext db) => new(db, new DkpPoolProvisioner(db));

    private static DkpLedgerWriter NewWriter(ApplicationDbContext db) =>
        new(db, NewPools(db), NullLogger<DkpLedgerWriter>.Instance);

    // A board template and a per-event copy of it: an event that ran on the copy must archive
    // the TEMPLATE, because the copy is deleted with the event.
    private static void SeedPartySetups(ApplicationDbContext db, int ownerEventId)
    {
        db.PartySetups.AddRange(
            new PartySetup { Id = TemplateId, LinkshellId = Ls, Name = "Template" },
            new PartySetup
            {
                Id = SnapshotSetupId, LinkshellId = Ls, Name = "Copy",
                OwnerEventId = ownerEventId, ClonedFromPartySetupId = TemplateId,
            });
    }

    // ===================================================== 1. timed End Event ===

    private const int TimedEventId = 100;

    private static async Task<(string DbName, DateTime Start, DateTime Commenced, EventEndService.EndEventResult Result)> EndTimedEventAsync(
        bool commenced, EventEndService.EndEventOptions options)
    {
        var name = Guid.NewGuid().ToString();
        var start = DateTime.UtcNow.AddHours(-3);
        var commencedAt = start.AddMinutes(40);
        using (var seed = NewDb(name))
        {
            seed.Linkshells.Add(new Linkshell { Id = Ls, LinkshellName = "LS", DkpRoundingIncrement = "Quarter" });
            seed.Users.Add(new AppUser { Id = "alice", UserName = "alice", CharacterName = "Alice" });
            seed.AppUserLinkshells.Add(new AppUserLinkshell
            {
                Id = 2, LinkshellId = Ls, AppUserId = "alice", CharacterName = "Alice", Rank = LinkshellRanks.Member, LinkshellDkp = 0,
            });
            SeedPartySetups(seed, TimedEventId);
            seed.Events.Add(new Event
            {
                Id = TimedEventId,
                LinkshellId = Ls,
                EventName = "Dynamis - Xarcabard",
                EventType = "Dynamis",
                EventLocation = "Xarcabard",
                PartySetupId = SnapshotSetupId,
                StartTime = start,
                CommencementStartTime = commenced ? commencedAt : null,
                Duration = 1.5,
                DkpPerHour = 10,
                WindowCountOverride = 1,
                CountsTowardActive = true,
            });
            // On break with 2 hours banked, so her duration (and DKP) does not depend on the clock.
            seed.AppUserEvents.AddRange(
                new AppUserEvent
                {
                    Id = 10, EventId = TimedEventId, AppUserId = "alice", CharacterName = "Alice",
                    JobName = "WAR", SubJobName = "NIN", JobType = "DD",
                    IsVerified = true, StartTime = start, IsOnBreak = true, Duration = 2,
                },
                new AppUserEvent
                {
                    Id = 11, EventId = TimedEventId, DiscordUserId = "555", CharacterName = "Guest",
                    JobName = "THF", IsVerified = true, StartTime = start, IsOnBreak = true, Duration = 1,
                });
            seed.SaveChanges();
        }

        using var db = NewDb(name);
        var ev = await db.Events
            .Include(e => e.AppUserEvents)
            .Include(e => e.EventLootDetails)
            .Include(e => e.Linkshell)
            .SingleAsync(e => e.Id == TimedEventId);
        var result = await EventEndService.EndEventCoreAsync(db, NewWriter(db), NewPools(db), ev, options);
        return (name, start, commencedAt, result);
    }

    [Fact]
    public async Task TimedEndEvent_WritesThisExactPastEvent()
    {
        var (name, start, commencedAt, result) = await EndTimedEventAsync(commenced: true, EventEndService.EndEventOptions.Activity);

        using var db = NewDb(name);
        var history = await db.EventHistories.Include(h => h.AppUserEventHistories).SingleAsync();
        Assert.Equal(result.EventHistoryId, history.Id);
        Assert.Equal(Ls, history.LinkshellId);
        Assert.Equal("Dynamis - Xarcabard", history.EventName);
        Assert.Equal("Dynamis", history.EventType);
        Assert.Equal("Xarcabard", history.EventLocation);
        Assert.Equal(TemplateId, history.PartySetupId);
        // The timed close keeps the event's own StartTime (not the commencement)...
        Assert.Equal(start, history.StartTime);
        Assert.Equal(result.EndTimeUtc, history.EndTime);
        Assert.Equal(commencedAt, history.CommencementStartTime);
        // ...and times the Duration from the commencement.
        Assert.Equal((result.EndTimeUtc - commencedAt).TotalHours, history.Duration);
        Assert.Equal(10, history.DkpPerHour);
        Assert.True(history.CountsTowardActive);
        Assert.Null(history.PopWindow);
        Assert.Null(history.PopWindowCount);
        // Its own clock read, a moment after the end time.
        Assert.NotNull(history.TimeStamp);
        Assert.True(history.TimeStamp >= result.EndTimeUtc);
        Assert.True(history.TimeStamp - result.EndTimeUtc < TimeSpan.FromMinutes(1));

        // The app's rules skip the outside sign-up: Alice only.
        var row = Assert.Single(history.AppUserEventHistories);
        Assert.Equal(history.Id, row.EventHistoryId);
        Assert.Equal("alice", row.AppUserId);
        Assert.Equal("Alice", row.CharacterName);
        Assert.Equal("WAR", row.JobName);
        Assert.Equal("NIN", row.SubJobName);
        Assert.Equal("DD", row.JobType);
        Assert.Equal(2, row.Duration);
        Assert.Equal(20, row.EventDkp);
        Assert.True(row.ActiveCredit);
        Assert.Null(row.WindowsAttended);

        var ledger = Assert.Single(await db.DkpLedgerEntries.ToListAsync());
        Assert.Equal("alice", ledger.AppUserId);
        Assert.Equal(Ls, ledger.LinkshellId);
        Assert.Equal(history.Id, ledger.EventHistoryId);
        Assert.Equal("EventEarned", ledger.EntryType);
        Assert.Equal(20, ledger.Amount);
        Assert.Equal(await NewPools(db).ResolveAsync(Ls, "Dynamis", CancellationToken.None), ledger.DkpPoolId);
        Assert.False(ledger.DkpPoolPinned);
        Assert.Equal(result.EndTimeUtc, ledger.OccurredAt);
        Assert.Equal("Alice", ledger.CharacterName);
        Assert.Equal("Dynamis - Xarcabard", ledger.EventName);
        Assert.Equal("Dynamis", ledger.EventType);
        Assert.Equal("Xarcabard", ledger.EventLocation);
        Assert.Null(ledger.ItemName);
        Assert.Equal("DKP earned from completed event.", ledger.Details);
        Assert.Null(ledger.EditReason);
        Assert.Null(ledger.SourceEventLootDetailId);
        Assert.Null(ledger.SourceWindowEventId);
        Assert.Null(ledger.AuditRelatedLedgerEntryId);
    }

    // Never commenced: Duration falls back to the event's own planned Duration. The addon's rules
    // also archive the outside sign-up, with no account and no ledger row.
    [Fact]
    public async Task TimedEndEvent_NeverCommenced_KeepsThePlannedDurationAndTheOutsideSignup()
    {
        var (name, start, _, result) = await EndTimedEventAsync(commenced: false, EventEndService.EndEventOptions.Addon);

        using var db = NewDb(name);
        var history = await db.EventHistories.Include(h => h.AppUserEventHistories).SingleAsync();
        Assert.Equal(start, history.StartTime);
        Assert.Equal(result.EndTimeUtc, history.EndTime);
        Assert.Null(history.CommencementStartTime);
        Assert.Equal(1.5, history.Duration);
        Assert.Equal(TemplateId, history.PartySetupId);

        var guest = history.AppUserEventHistories.Single(r => r.CharacterName == "Guest");
        Assert.Null(guest.AppUserId);
        Assert.Equal("THF", guest.JobName);
        Assert.Null(guest.SubJobName);
        Assert.Null(guest.JobType);
        Assert.Equal(1, guest.Duration);
        Assert.Equal(10, guest.EventDkp);
        Assert.True(guest.ActiveCredit);
        Assert.Null(guest.WindowsAttended);
        Assert.Equal(2, history.AppUserEventHistories.Count);

        // Only Alice has a balance to pay into.
        Assert.Equal("alice", Assert.Single(await db.DkpLedgerEntries.ToListAsync()).AppUserId);
    }

    // ======================================================== 2. HNM End Camp ===

    private const int CampEventId = 500;
    private const string AlphaId = "user-alpha";
    private const string BetaId = "user-beta";
    private static readonly DateTime CampStart = new(2026, 8, 20, 10, 0, 0, DateTimeKind.Utc);

    private static HnmCampReviewHandoffService NewHandoff(ApplicationDbContext db) =>
        new(db,
            new WdCampFinalizer(db, NullLogger<WdCampFinalizer>.Instance),
            new HnmStandardCampFinalizer(db, NullLogger<HnmStandardCampFinalizer>.Instance),
            NewWriter(db),
            NewPools(db),
            NullLogger<HnmCampReviewHandoffService>.Instance);

    private static WindowEventDkpLedgerService NewLedgerService(ApplicationDbContext db) =>
        new(db, NewWriter(db), NewPools(db), NullLogger<WindowEventDkpLedgerService>.Instance);

    // A live Standard camp, Alpha and Beta scanned in window 1. StartTime is the NEXT repop, as a
    // recycled board has it; the archive must not date itself from that when it has a commencement.
    private static async Task<ApplicationDbContext> SeedCampAsync(bool commenced)
    {
        var db = NewDb();
        db.Linkshells.Add(new Linkshell
        {
            Id = Ls,
            LinkshellName = "LS",
            EnableHnmSection = true,
            HnmStandardWindowBonus = 1,
            HnmStandardOpenBonus = 2,
            HnmStandardCloseBonus = 3,
        });
        SeedPartySetups(db, CampEventId);
        db.Events.Add(new Event
        {
            Id = CampEventId,
            LinkshellId = Ls,
            EventName = "Fafnir/Nidhogg Day 3",
            EventType = "HNM",
            EventLocation = "Dragon's Aery",
            AssignedMonsterName = "Fafnir/Nidhogg",
            PartySetupId = SnapshotSetupId,
            StartTime = commenced ? CampStart.AddDays(3) : CampStart,
            CommencementStartTime = commenced ? CampStart : null,
            Duration = 4,
            DkpPerHour = 0,
            HnmWindowNumber = 2,
            WindowCountOverride = 7,
            CountsTowardActive = true,
        });
        db.Users.AddRange(
            new AppUser { Id = AlphaId, UserName = "alpha", CharacterName = "Alpha" },
            new AppUser { Id = BetaId, UserName = "beta", CharacterName = "Beta" });
        db.AppUserLinkshells.AddRange(
            new AppUserLinkshell { Id = 1, LinkshellId = Ls, AppUserId = AlphaId, CharacterName = "Alpha" },
            new AppUserLinkshell { Id = 2, LinkshellId = Ls, AppUserId = BetaId, CharacterName = "Beta" });
        db.AppUserEvents.AddRange(
            new AppUserEvent
            {
                Id = 20, EventId = CampEventId, AppUserId = AlphaId, CharacterName = "Alpha",
                JobName = "WAR", SubJobName = "NIN", JobType = "DD", IsVerified = true, StartTime = CampStart,
            },
            new AppUserEvent
            {
                Id = 21, EventId = CampEventId, AppUserId = BetaId, CharacterName = "Beta",
                JobName = "WHM", JobType = "Healer", IsVerified = true, StartTime = CampStart,
            });
        db.EventAttendanceWindows.Add(new EventAttendanceWindow
        {
            Id = 1, EventId = CampEventId, SequenceNumber = 1, PostedAt = CampStart.AddMinutes(5),
        });
        db.AppUserEventWindows.AddRange(
            new AppUserEventWindow
            {
                Id = 1, EventAttendanceWindowId = 1, AppUserEventId = 20, AppUserId = AlphaId,
                CharacterName = "Alpha", VerifiedAt = CampStart.AddMinutes(5),
            },
            new AppUserEventWindow
            {
                Id = 2, EventAttendanceWindowId = 1, AppUserEventId = 21, AppUserId = BetaId,
                CharacterName = "Beta", VerifiedAt = CampStart.AddMinutes(5),
            });
        await db.SaveChangesAsync();
        return db;
    }

    private static async Task<WindowEvent> EndCampAsync(ApplicationDbContext db)
    {
        var camp = await db.Events.FirstAsync(e => e.Id == CampEventId);
        var windowEvent = await NewHandoff(db).StageHandoffAsync(
            camp, popWindow: 2, claimed: false, killed: true, CancellationToken.None);
        await db.SaveChangesAsync();
        return windowEvent!;
    }

    [Fact]
    public async Task HnmEndCamp_WritesThisExactPastEvent()
    {
        using var db = await SeedCampAsync(commenced: true);
        var windowEvent = await EndCampAsync(db);

        var history = await db.EventHistories.Include(h => h.AppUserEventHistories).SingleAsync();
        Assert.Equal(windowEvent.CampEventHistoryId, history.Id);
        Assert.Equal(Ls, history.LinkshellId);
        Assert.Equal("Fafnir/Nidhogg Day 3", history.EventName);
        Assert.Equal("HNM", history.EventType);
        Assert.Equal("Dragon's Aery", history.EventLocation);
        Assert.Equal(TemplateId, history.PartySetupId);
        // A camp dates itself from its commencement, never from the recycled board's StartTime.
        Assert.Equal(CampStart, history.StartTime);
        Assert.Equal(windowEvent.CampEndedAtUtc, history.EndTime);
        Assert.Equal(CampStart, history.CommencementStartTime);
        Assert.Equal((windowEvent.CampEndedAtUtc!.Value - CampStart).TotalHours, history.Duration);
        Assert.Equal(0, history.DkpPerHour);
        Assert.True(history.CountsTowardActive);
        Assert.Equal(2, history.PopWindow);
        Assert.Equal(7, history.PopWindowCount);
        // The end time IS the TimeStamp here (one clock read for the whole End Camp).
        Assert.Equal(windowEvent.CampEndedAtUtc, history.TimeStamp);

        var rows = history.AppUserEventHistories.OrderBy(r => r.CharacterName).ToList();
        Assert.Equal(2, rows.Count);
        AssertCampRow(rows[0], history.Id, AlphaId, "Alpha", "WAR", "NIN", dkp: rows[0].EventDkp);
        AssertCampRow(rows[1], history.Id, BetaId, "Beta", "WHM", null, dkp: rows[1].EventDkp);
        // The camp's proposal: window 1 of a camp that popped in window 2 pays the window bonus
        // plus the open bonus.
        Assert.Equal(3, rows[0].EventDkp);
        Assert.Equal(3, rows[1].EventDkp);

        // End Camp writes no earned ledger rows: Post does.
        Assert.Empty(await db.DkpLedgerEntries.ToListAsync());
    }

    // A camp row's fixed shape: no job type, no duration, no window count, always active.
    private static void AssertCampRow(
        AppUserEventHistory row, int historyId, string appUserId, string characterName,
        string? jobName, string? subJobName, double? dkp)
    {
        Assert.Equal(historyId, row.EventHistoryId);
        Assert.Equal(appUserId, row.AppUserId);
        Assert.Equal(characterName, row.CharacterName);
        Assert.Equal(jobName, row.JobName);
        Assert.Equal(subJobName, row.SubJobName);
        Assert.Null(row.JobType);
        Assert.Null(row.Duration);
        Assert.Equal(dkp, row.EventDkp);
        Assert.True(row.ActiveCredit);
        Assert.Null(row.WindowsAttended);
    }

    // Never commenced: the camp falls back to the board's StartTime, and still times the Duration
    // from it (unlike the timed close, which falls back to the planned Duration).
    [Fact]
    public async Task HnmEndCamp_NeverCommenced_DatesFromTheBoardsStartTime()
    {
        using var db = await SeedCampAsync(commenced: false);
        var windowEvent = await EndCampAsync(db);

        var history = await db.EventHistories.SingleAsync();
        Assert.Equal(CampStart, history.StartTime);
        Assert.Null(history.CommencementStartTime);
        Assert.Equal((windowEvent.CampEndedAtUtc!.Value - CampStart).TotalHours, history.Duration);
        Assert.Equal(windowEvent.CampEndedAtUtc, history.TimeStamp);
    }

    // ================================================= 3. the review is Posted ===

    private static async Task PostAsync(ApplicationDbContext db, WindowEvent windowEvent, DateTime postedAt)
    {
        windowEvent.PostedToSheetAt = postedAt;
        windowEvent.DkpAmount = 1d;
        windowEvent.EntryType = WindowEventEntryTypes.KingsCamp;
        await db.SaveChangesAsync();
        await NewLedgerService(db).EnsurePostedWindowEventLedgerEntriesAsync(windowEvent.Id, CancellationToken.None);
    }

    // Post re-syncs the archive's roster to the review: a reviewed amount lands on the existing
    // row, and someone added during review gets a fresh row of the camp shape.
    [Fact]
    public async Task HnmPost_ResyncsTheArchiveRowsToTheReview()
    {
        using var db = await SeedCampAsync(commenced: true);
        db.Users.Add(new AppUser { Id = "user-gamma", UserName = "gamma", CharacterName = "Gamma" });
        db.AppUserLinkshells.Add(new AppUserLinkshell { Id = 3, LinkshellId = Ls, AppUserId = "user-gamma", CharacterName = "Gamma" });
        await db.SaveChangesAsync();
        var windowEvent = await EndCampAsync(db);
        var endedHistory = await db.EventHistories.AsNoTracking().SingleAsync();

        var alpha = await db.AttendanceSnapshotEntries.FirstAsync(e => e.CharacterName == "Alpha");
        alpha.DkpAmount = 9d;
        db.AttendanceSnapshotEntries.Add(new AttendanceSnapshotEntry
        {
            SnapshotId = alpha.SnapshotId, CharacterName = "Gamma",
        });
        await db.SaveChangesAsync();

        var postedAt = CampStart.AddDays(1);
        await PostAsync(db, windowEvent, postedAt);

        var history = await db.EventHistories.Include(h => h.AppUserEventHistories).AsNoTracking().SingleAsync();
        // Post touches the roster only, never the header.
        Assert.Equal(endedHistory.StartTime, history.StartTime);
        Assert.Equal(endedHistory.EndTime, history.EndTime);
        Assert.Equal(endedHistory.TimeStamp, history.TimeStamp);
        Assert.Equal(endedHistory.Duration, history.Duration);

        var rows = history.AppUserEventHistories.OrderBy(r => r.CharacterName).ToList();
        Assert.Equal(new[] { "Alpha", "Beta", "Gamma" }, rows.Select(r => r.CharacterName).ToArray());
        AssertCampRow(rows[0], history.Id, AlphaId, "Alpha", "WAR", "NIN", dkp: 9);
        AssertCampRow(rows[1], history.Id, BetaId, "Beta", "WHM", null, dkp: rows[1].EventDkp);
        AssertCampRow(rows[2], history.Id, "user-gamma", "Gamma", null, null, dkp: rows[2].EventDkp);

        // Each paid member's ledger row points at this archive.
        var earned = await db.DkpLedgerEntries.Where(e => e.EntryType == "SnapshotEarned").ToListAsync();
        Assert.NotEmpty(earned);
        Assert.All(earned, e => Assert.Equal(history.Id, e.EventHistoryId));
        Assert.Equal(9, earned.Single(e => e.AppUserId == AlphaId).Amount);
    }

    // ============================================ 4. legacy review row at Post ===

    // A review row staged before End Camp wrote the archive gets one at Post, built entirely from
    // the review row's Camp* columns.
    [Fact]
    public async Task HnmPost_LegacyReviewRow_WritesThisExactPastEvent()
    {
        using var db = await SeedCampAsync(commenced: true);
        var windowEvent = await EndCampAsync(db);

        var staged = await db.EventHistories.Include(h => h.AppUserEventHistories).SingleAsync();
        db.AppUserEventHistories.RemoveRange(staged.AppUserEventHistories);
        db.EventHistories.Remove(staged);
        windowEvent.CampEventHistoryId = null;
        windowEvent.CampEventHistory = null;
        await db.SaveChangesAsync();

        var postedAt = CampStart.AddDays(1);
        await PostAsync(db, windowEvent, postedAt);

        var history = await db.EventHistories.Include(h => h.AppUserEventHistories).AsNoTracking().SingleAsync();
        Assert.Equal(windowEvent.CampEventHistoryId, history.Id);
        Assert.Equal(Ls, history.LinkshellId);
        Assert.Equal(windowEvent.Name, history.EventName);
        Assert.Equal("Fafnir/Nidhogg Day 3", history.EventName);
        Assert.Equal("HNM", history.EventType);
        Assert.Equal("Dragon's Aery", history.EventLocation);
        Assert.Null(history.PartySetupId);
        Assert.Equal(CampStart, history.StartTime);
        Assert.Equal(windowEvent.CampEndedAtUtc, history.EndTime);
        Assert.Equal(CampStart, history.CommencementStartTime);
        Assert.Null(history.Duration);
        Assert.Null(history.DkpPerHour);
        Assert.True(history.CountsTowardActive);
        Assert.Null(history.PopWindow);
        Assert.Null(history.PopWindowCount);
        Assert.Equal(postedAt, history.TimeStamp);

        var rows = history.AppUserEventHistories.OrderBy(r => r.CharacterName).ToList();
        Assert.Equal(2, rows.Count);
        // Jobs come off the review's captures.
        AssertCampRow(rows[0], history.Id, AlphaId, "Alpha", "WAR", "NIN", dkp: rows[0].EventDkp);
        AssertCampRow(rows[1], history.Id, BetaId, "Beta", "WHM", null, dkp: rows[1].EventDkp);
        Assert.Equal(3, rows[0].EventDkp);
        Assert.Equal(3, rows[1].EventDkp);
    }

    // =================================== 5. member added to a Past Event later ===

    [Fact]
    public async Task AddingAMemberToAPastEvent_WritesThisExactRow()
    {
        using var db = NewDb();
        var endedAt = new DateTime(2026, 9, 1, 22, 0, 0, DateTimeKind.Utc);
        db.Linkshells.Add(new Linkshell { Id = Ls, LinkshellName = "LS", DkpRoundingIncrement = "Quarter" });
        db.Users.Add(new AppUser { Id = "bob", UserName = "bob", CharacterName = "Bob" });
        db.AppUserLinkshells.Add(new AppUserLinkshell
        {
            Id = 2, LinkshellId = Ls, AppUserId = "bob", CharacterName = "Bobby", LinkshellDkp = 0,
        });
        db.EventHistories.Add(new EventHistory
        {
            Id = 50, LinkshellId = Ls, EventName = "Sky", EventType = "Sky", EventLocation = "Tu'Lia",
            StartTime = endedAt.AddHours(-3), EndTime = endedAt, Duration = 2.5, DkpPerHour = 4,
        });
        await db.SaveChangesAsync();

        var pools = NewPools(db);
        var edits = new EventHistoryEditService(db, NewWriter(db), pools);
        var outcome = await edits.AddParticipantAsync(
            50, "bob", 7.3, jobType: " DD ", jobName: " SAM ", subJobName: "  ", activeCredit: false, CancellationToken.None);
        Assert.Equal(EventHistoryEditOutcome.Done, outcome);

        var row = await db.AppUserEventHistories.AsNoTracking().SingleAsync();
        Assert.Equal(50, row.EventHistoryId);
        Assert.Equal("bob", row.AppUserId);
        Assert.Equal("Bobby", row.CharacterName);
        Assert.Equal("SAM", row.JobName);
        Assert.Null(row.SubJobName);
        Assert.Equal("DD", row.JobType);
        Assert.Equal(2.5, row.Duration);
        Assert.Equal(7.25, row.EventDkp);
        Assert.False(row.ActiveCredit);
        Assert.Null(row.WindowsAttended);

        var ledger = await db.DkpLedgerEntries.AsNoTracking().SingleAsync();
        Assert.Equal("bob", ledger.AppUserId);
        Assert.Equal(Ls, ledger.LinkshellId);
        Assert.Equal(50, ledger.EventHistoryId);
        Assert.Equal("EventEarned", ledger.EntryType);
        Assert.Equal(7.25, ledger.Amount);
        Assert.Equal(await pools.ResolveAsync(Ls, "Sky", CancellationToken.None), ledger.DkpPoolId);
        Assert.False(ledger.DkpPoolPinned);
        Assert.Equal(endedAt, ledger.OccurredAt);
        Assert.Equal("Bobby", ledger.CharacterName);
        Assert.Equal("Sky", ledger.EventName);
        Assert.Equal("Sky", ledger.EventType);
        Assert.Equal("Tu'Lia", ledger.EventLocation);
        Assert.Null(ledger.ItemName);
        Assert.Equal("DKP earned from completed event (member added afterward).", ledger.Details);
        Assert.Null(ledger.SourceEventLootDetailId);
        Assert.Null(ledger.SourceWindowEventId);
        Assert.Null(ledger.AuditRelatedLedgerEntryId);
    }

    // ====================================================== one place only ===

    // Past Event rows are built in EventHistoryBuilder and nowhere else in the app, so a column
    // added to (or dropped from) these tables is handled once. Tests may still build rows by hand.
    [Fact]
    public void PastEventRows_AreOnlyBuiltByTheBuilder()
    {
        var root = FindRepoRoot();
        var files = new[] { "Areas", "Authorization", "Controllers", "Data", "Services", "Utils", "ViewModels" }
            .Select(folder => Path.Combine(root, folder))
            .Where(Directory.Exists)
            .SelectMany(folder => Directory.EnumerateFiles(folder, "*.cs", SearchOption.AllDirectories))
            .Append(Path.Combine(root, "Program.cs"))
            .Where(path => !path.EndsWith(Path.Combine("Services", "EventHistoryBuilder.cs"), StringComparison.Ordinal))
            .ToList();
        Assert.Contains(files, path => path.EndsWith("EventEndService.cs", StringComparison.Ordinal));

        // "new EventHistory {" / "(" / end of line: code, not a comment that says "the new EventHistory".
        var pattern = new System.Text.RegularExpressions.Regex(
            @"\bnew\s+(EventHistory|AppUserEventHistory)\s*(\{|\(|$)",
            System.Text.RegularExpressions.RegexOptions.Multiline);
        var offenders = files
            .Where(path => pattern.IsMatch(File.ReadAllText(path)))
            .Select(path => Path.GetRelativePath(root, path))
            .ToList();
        Assert.True(offenders.Count == 0,
            "Build Past Event rows with EventHistoryBuilder instead: " + string.Join(", ", offenders));
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "LinkshellManagerDiscordApp.csproj")))
        {
            dir = dir.Parent;
        }
        return dir?.FullName ?? throw new InvalidOperationException("Repo root not found.");
    }
}
