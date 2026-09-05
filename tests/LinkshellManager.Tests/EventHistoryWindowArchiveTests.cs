using LinkshellManagerDiscordApp.Controllers;
using LinkshellManagerDiscordApp.Data;
using LinkshellManagerDiscordApp.Models;
using LinkshellManagerDiscordApp.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LinkshellManager.Tests;

// Closing a camp used to DESTROY its window record. EventAttendanceWindow cascades off Event, and
// EndEventCoreAsync deletes the Event — so the moment an HNM event became a past event, the
// windows it posted, the rosters scanned into them, and the per-member window tally its DKP was
// computed from all went with it. A closed camp could not explain its own payout, and the Past
// events UI had nothing HNM-shaped to show.
//
// The fix is a re-parent, not a copy: at close the windows move onto the new EventHistory and
// their Event FK is cleared, so the cascade finds nothing to take. These tests pin that, because
// the failure mode is silent — the close still succeeds, the DKP is still right, and the loss only
// shows up later as an empty archive.
public class EventHistoryWindowArchiveTests
{
    private const int Ls = 7;

    private static ApplicationDbContext NewDb() =>
        new(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options);

    private static DkpLedgerWriter NewWriter(ApplicationDbContext db) =>
        new(db, new DkpPoolResolver(db, new DkpPoolProvisioner(db)), NullLogger<DkpLedgerWriter>.Instance);

    private static DkpPoolResolver NewPools(ApplicationDbContext db) =>
        new(db, new DkpPoolProvisioner(db));

    // A 2-post king camp (Fafnir => HnmConfig.GetWindowCount 2) with an Open and a Close, one
    // member scanned into both. Deliberately the smallest thing that is still WINDOWED: the
    // isWindowed branch is what decides both the payout and the tally under test.
    private static Event SeedCamp(ApplicationDbContext db)
    {
        db.Linkshells.Add(new Linkshell { Id = Ls, LinkshellName = "LS", LootStructure = "Dkp" });
        db.Users.Add(new AppUser { Id = "u1", UserName = "edicius" });
        db.AppUserLinkshells.Add(new AppUserLinkshell
        {
            Id = 1, LinkshellId = Ls, AppUserId = "u1", CharacterName = "Edicius"
        });

        var camp = new Event
        {
            Id = 100,
            LinkshellId = Ls,
            EventName = "Fafnir",
            EventType = "HNM",
            EventLocation = "Dragon's Aery",
            StartTime = new DateTime(2026, 8, 18, 1, 0, 0, DateTimeKind.Utc),
            CommencementStartTime = new DateTime(2026, 8, 18, 1, 0, 0, DateTimeKind.Utc),
            DkpPerHour = 3,
            CountsTowardActive = true
        };
        db.Events.Add(camp);

        var participation = new AppUserEvent
        {
            Id = 10,
            EventId = camp.Id,
            AppUserId = "u1",
            CharacterName = "Edicius",
            JobName = "NIN",
            SubJobName = "WAR",
            IsVerified = true,
            StartTime = camp.CommencementStartTime
        };
        db.AppUserEvents.Add(participation);

        foreach (var (sequence, label, closing) in new[] { (1, "Open", false), (2, "Close", true) })
        {
            var window = new EventAttendanceWindow
            {
                Id = 200 + sequence,
                EventId = camp.Id,
                SequenceNumber = sequence,
                Label = label,
                PostedAt = new DateTime(2026, 8, 18, 1, sequence, 0, DateTimeKind.Utc),
                PostedBySource = "addon",
                IsClosingWindow = closing
            };
            db.EventAttendanceWindows.Add(window);
            db.AppUserEventWindows.Add(new AppUserEventWindow
            {
                Id = 300 + sequence,
                AppUserEventId = participation.Id,
                EventAttendanceWindowId = window.Id,
                AppUserId = "u1",
                CharacterName = "Athmilk",
                MainCharacterName = "Edicius",
                Zone = "Dragon's_Aery",
                VerifiedAt = window.PostedAt
            });
        }

        db.SaveChanges();

        // Reload the way the controller does, so the close operates on the same graph it would in
        // production (AppUserEvents / EventLootDetails included, windows NOT).
        return db.Events
            .Include(e => e.AppUserEvents)
            .Include(e => e.EventLootDetails)
            .Include(e => e.Linkshell)
            .First(e => e.Id == 100);
    }

    [Fact]
    public async Task EndingACamp_KeepsItsWindows_ReparentedToTheHistory()
    {
        using var db = NewDb();
        var camp = SeedCamp(db);

        await EventController.EndEventCoreAsync(db, NewWriter(db), NewPools(db), camp);

        var history = await db.EventHistories.SingleAsync();
        var windows = await db.EventAttendanceWindows.OrderBy(w => w.SequenceNumber).ToListAsync();

        Assert.Equal(2, windows.Count);
        Assert.All(windows, w => Assert.Equal(history.Id, w.EventHistoryId));
        // The Event FK has to be CLEARED, not just left dangling — it is what stops the cascade.
        Assert.All(windows, w => Assert.Null(w.EventId));
        Assert.Equal(new[] { "Open", "Close" }, windows.Select(w => w.Label));
        Assert.True(windows.Single(w => w.SequenceNumber == 2).IsClosingWindow);
    }

    [Fact]
    public async Task EndingACamp_KeepsTheRosterScannedIntoEachWindow()
    {
        using var db = NewDb();
        var camp = SeedCamp(db);

        await EventController.EndEventCoreAsync(db, NewWriter(db), NewPools(db), camp);

        // The participation is deleted with the event (AppUserEventId is SetNull), but the
        // denormalized name/zone on the snapshot row is exactly what keeps it readable afterwards.
        var scanned = await db.AppUserEventWindows.ToListAsync();
        Assert.Equal(2, scanned.Count);
        Assert.All(scanned, row => Assert.Equal("Athmilk", row.CharacterName));
        Assert.All(scanned, row => Assert.Equal("Edicius", row.MainCharacterName));
        Assert.All(scanned, row => Assert.Equal("Dragon's_Aery", row.Zone));
    }

    [Fact]
    public async Task EndingACamp_StampsTheWindowTallyOnTheHistoryRow()
    {
        using var db = NewDb();
        var camp = SeedCamp(db);

        await EventController.EndEventCoreAsync(db, NewWriter(db), NewPools(db), camp);

        var participant = await db.AppUserEventHistories.SingleAsync();
        // Both windows, which is also what the DKP was computed from (2 x DkpPerHour-as-per-window).
        Assert.Equal(2, participant.WindowsAttended);
        Assert.Equal(6, participant.EventDkp);
    }

    [Fact]
    public async Task TimedEvent_LeavesTheWindowTallyNull_RatherThanZero()
    {
        using var db = NewDb();
        db.Linkshells.Add(new Linkshell { Id = Ls, LinkshellName = "LS", LootStructure = "Dkp" });
        db.Users.Add(new AppUser { Id = "u1", UserName = "edicius" });
        db.AppUserLinkshells.Add(new AppUserLinkshell
        {
            Id = 1, LinkshellId = Ls, AppUserId = "u1", CharacterName = "Edicius"
        });
        var start = new DateTime(2026, 8, 18, 1, 0, 0, DateTimeKind.Utc);
        db.Events.Add(new Event
        {
            Id = 101, LinkshellId = Ls, EventName = "Sky Farm", EventType = "Sky",
            StartTime = start, CommencementStartTime = start, DkpPerHour = 2, CountsTowardActive = true
        });
        db.AppUserEvents.Add(new AppUserEvent
        {
            Id = 11, EventId = 101, AppUserId = "u1", CharacterName = "Edicius",
            IsVerified = true, StartTime = start
        });
        db.SaveChanges();

        var ev = db.Events
            .Include(e => e.AppUserEvents)
            .Include(e => e.EventLootDetails)
            .Include(e => e.Linkshell)
            .First(e => e.Id == 101);

        await EventController.EndEventCoreAsync(db, NewWriter(db), NewPools(db), ev);

        var participant = await db.AppUserEventHistories.SingleAsync();
        // Null, not 0: "this event doesn't count windows" and "attended none of them" are different
        // facts, and the UI hides the column on the first.
        Assert.Null(participant.WindowsAttended);
    }

    [Fact]
    public async Task ArchivedWindows_ReadBack_WithLabelsAndRoster()
    {
        using var db = NewDb();
        var camp = SeedCamp(db);
        await EventController.EndEventCoreAsync(db, NewWriter(db), NewPools(db), camp);

        var history = await db.EventHistories.SingleAsync();
        var archive = await EventHistoryWindowsReader.LoadAsync(db, history, CancellationToken.None);

        Assert.True(archive.HasWindows);
        Assert.Equal(2, archive.WindowCount);
        Assert.Equal(new[] { "Open", "Close" }, archive.Windows.Select(w => w.Label));
        // One person, scanned into both windows — the distinct count must not double them.
        Assert.Equal(1, archive.DistinctAttendeeCount);
        Assert.Equal("Athmilk", archive.Windows[0].Attendees.Single().CharacterName);
        Assert.Equal("Edicius", archive.Windows[0].Attendees.Single().MainCharacterName);
    }

    // No lotteries, no row — a camp nobody tagged must not render an empty Tag section.
    [Fact]
    public async Task ArchivedTags_AreAbsent_WhenNobodyTagged()
    {
        using var db = NewDb();
        var camp = SeedCamp(db);
        await EventController.EndEventCoreAsync(db, NewWriter(db), NewPools(db), camp);

        var history = await db.EventHistories.SingleAsync();
        var archive = await EventHistoryWindowsReader.LoadAsync(db, history, CancellationToken.None);

        Assert.Null(archive.TagRoster);
    }

    [Fact]
    public async Task WindowCounts_ForTheList_OnlyCoverEventsThatArchivedSome()
    {
        using var db = NewDb();
        var camp = SeedCamp(db);
        await EventController.EndEventCoreAsync(db, NewWriter(db), NewPools(db), camp);

        var history = await db.EventHistories.SingleAsync();
        var counts = await EventHistoryWindowsReader.CountsByHistoryAsync(
            db, new[] { history.Id, history.Id + 999 }, CancellationToken.None);

        Assert.Equal(2, counts[history.Id]);
        // An event with no archive is ABSENT, not zero — the caller defaults it, and a key here
        // would make "closed before the archive existed" indistinguishable from "posted none".
        Assert.False(counts.ContainsKey(history.Id + 999));
    }
}
