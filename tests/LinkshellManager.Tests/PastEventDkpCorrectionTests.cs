using System.Security.Claims;
using LinkshellManagerDiscordApp.Controllers;
using LinkshellManagerDiscordApp.Data;
using LinkshellManagerDiscordApp.Models;
using LinkshellManagerDiscordApp.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;
using LinkshellManager.TestSupport;

namespace LinkshellManager.Tests;

// R2.1 (dead-dkp-03): a DKP change on a PAST event no longer edits or deletes the rows the event
// paid. It appends one "Correction" row for the difference, pointing at the row it corrects, with
// who made it and the officer's optional reason. Member history now shows what really happened.
//
// The money must not change: each test runs the same edit the OLD way (DkpLedgerWriter.Amend /
// Remove on the earned row, as EventHistoryEditService did before) on a twin database, and checks
// the balance, the Past Event figure and the lifetime Total / Spent all come out the same.
public class PastEventDkpCorrectionTests
{
    private const int Ls = 7;
    private const int HistoryId = 50;
    private const int BobRow = 70;
    private const int AliceRow = 71;
    private const string Officer = "officer";
    private static readonly DkpCorrectionNote Note = new("Boss", "wrong rate");

    private static ApplicationDbContext NewDb(params IInterceptor[] interceptors) =>
        new(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .AddInterceptors(interceptors)
            .Options);

    private static DkpPoolResolver Pools(ApplicationDbContext db) => new(db, new DkpPoolProvisioner(db));

    private static DkpLedgerWriter Writer(ApplicationDbContext db) =>
        new(db, Pools(db), NullLogger<DkpLedgerWriter>.Instance);

    private static EventHistoryEditService Edits(ApplicationDbContext db)
    {
        var pools = Pools(db);
        return new EventHistoryEditService(db, new DkpLedgerWriter(db, pools, NullLogger<DkpLedgerWriter>.Instance), pools);
    }

    // A 1-hour Sky event at 10 DKP/hour: Bob and Alice each earned 10. The officer "Boss" leads.
    private static async Task SeedAsync(ApplicationDbContext db)
    {
        db.Linkshells.Add(new Linkshell { Id = Ls, LinkshellName = "LS", DkpRoundingIncrement = "Quarter" });
        db.Users.AddRange(
            new AppUser { Id = Officer, UserName = Officer, CharacterName = "Boss" },
            new AppUser { Id = "bob", UserName = "bob", CharacterName = "Bob" },
            new AppUser { Id = "alice", UserName = "alice", CharacterName = "Alice" });
        db.AppUserLinkshells.AddRange(
            new AppUserLinkshell { Id = 1, LinkshellId = Ls, AppUserId = Officer, Rank = LinkshellRanks.Leader, LinkshellDkp = 0 },
            new AppUserLinkshell { Id = 2, LinkshellId = Ls, AppUserId = "bob", Rank = LinkshellRanks.Member, LinkshellDkp = 0 },
            new AppUserLinkshell { Id = 3, LinkshellId = Ls, AppUserId = "alice", Rank = LinkshellRanks.Member, LinkshellDkp = 0 });
        db.EventHistories.Add(new EventHistory
        {
            Id = HistoryId, LinkshellId = Ls, EventName = "Sky", EventType = "Sky", DkpPerHour = 10, Duration = 1,
            EndTime = new DateTime(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc),
        });
        db.AppUserEventHistories.AddRange(
            new AppUserEventHistory { Id = BobRow, EventHistoryId = HistoryId, AppUserId = "bob", CharacterName = "Bob", EventDkp = 10, Duration = 1 },
            new AppUserEventHistory { Id = AliceRow, EventHistoryId = HistoryId, AppUserId = "alice", CharacterName = "Alice", EventDkp = 10, Duration = 1 });
        await db.SaveChangesAsync();

        var writer = Writer(db);
        foreach (var member in db.AppUserLinkshells.Where(m => m.AppUserId != Officer).ToList())
        {
            await writer.AppendAsync(member, "EventEarned", 10, DateTime.UtcNow, DkpPoolRef.Derived("Sky"),
                new DkpEntryContext(CharacterName: db.Users.Find(member.AppUserId)!.CharacterName, EventName: "Sky", EventType: "Sky",
                    Details: "DKP earned from completed event.", EventHistoryId: HistoryId),
                CancellationToken.None);
        }
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
    }

    // ---- the old in-place edit, kept here as the yardstick -------------------------------------

    private static async Task OldSetAsync(ApplicationDbContext db, string appUserId, double amount)
    {
        var member = await db.AppUserLinkshells.SingleAsync(m => m.AppUserId == appUserId);
        var earned = await db.DkpLedgerEntries.SingleAsync(e => e.AppUserId == appUserId && e.EntryType == "EventEarned");
        var audited = await db.DkpLedgerEntries.Where(e => e.AuditRelatedLedgerEntryId == earned.Id).SumAsync(e => e.Amount);
        Writer(db).Amend(earned, amount - audited, "DKP earned from completed event (edited).", member);
        (await db.AppUserEventHistories.SingleAsync(p => p.AppUserId == appUserId)).EventDkp = amount;
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
    }

    private static async Task OldRemoveAsync(ApplicationDbContext db, string appUserId)
    {
        var member = await db.AppUserLinkshells.SingleAsync(m => m.AppUserId == appUserId);
        var writer = Writer(db);
        var earned = await db.DkpLedgerEntries.SingleAsync(e => e.AppUserId == appUserId && e.EntryType == "EventEarned");
        foreach (var audit in await db.DkpLedgerEntries.Where(e => e.AuditRelatedLedgerEntryId == earned.Id).ToListAsync())
        {
            writer.Remove(audit, member);
        }
        writer.Remove(earned, member);
        db.AppUserEventHistories.Remove(await db.AppUserEventHistories.SingleAsync(p => p.AppUserId == appUserId));
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
    }

    // ---- what the officer and the member see ----------------------------------------------------

    private sealed record Outcome(double Balance, double? Shown, double Total, double Spent);

    private static async Task<Outcome> OutcomeAsync(ApplicationDbContext db, string appUserId)
    {
        var balance = (await db.AppUserLinkshells.AsNoTracking().SingleAsync(m => m.AppUserId == appUserId)).LinkshellDkp ?? 0;
        var attendee = await db.AppUserEventHistories.AsNoTracking().SingleOrDefaultAsync(p => p.AppUserId == appUserId);
        double? shown = null;
        if (attendee is not null)
        {
            shown = (await PastEventDkp.LoadAsync(db, new[] { HistoryId }, CancellationToken.None)).For(attendee);
        }
        var sheet = await new DkpSheetService(db, Pools(db)).BuildAsync(Ls, CancellationToken.None);
        var name = appUserId == "bob" ? "Bob" : "Alice";
        var row = sheet.Members.Single(m => m.Name == name);
        return new Outcome(balance, shown, row.Total, row.Spent);
    }

    private static List<DkpLedgerEntry> Rows(ApplicationDbContext db, string appUserId) =>
        db.DkpLedgerEntries.AsNoTracking().Where(e => e.AppUserId == appUserId).OrderBy(e => e.Id).ToList();

    private static async Task<(ApplicationDbContext Old, ApplicationDbContext New)> TwinsAsync(params IInterceptor[] interceptors)
    {
        var old = NewDb();
        var current = NewDb(interceptors);
        await SeedAsync(old);
        await SeedAsync(current);
        return (old, current);
    }

    // ---- set one attendee's DKP -----------------------------------------------------------------

    [Theory]
    [InlineData(6)]
    [InlineData(14.5)]
    [InlineData(0)]
    public async Task SetDkp_AppendsOneCorrection_AndEndsWhereTheOldEditDid(double amount)
    {
        var (old, db) = await TwinsAsync();
        await OldSetAsync(old, "bob", amount);

        Assert.Equal(EventHistoryEditOutcome.Done,
            await Edits(db).SetParticipantDkpAsync(HistoryId, BobRow, amount, Note, CancellationToken.None));

        Assert.Equal(await OutcomeAsync(old, "bob"), await OutcomeAsync(db, "bob"));
        Assert.Equal(new Outcome(amount, amount, amount, 0), await OutcomeAsync(db, "bob"));

        var rows = Rows(db, "bob");
        Assert.Equal(2, rows.Count);
        // The payout is exactly as the event wrote it.
        Assert.Equal(("EventEarned", 10d, "DKP earned from completed event."), (rows[0].EntryType, rows[0].Amount, rows[0].Details));
        var correction = rows[1];
        Assert.Equal(DkpCorrections.EntryType, correction.EntryType);
        Assert.Equal(amount - 10, correction.Amount);
        Assert.Equal(rows[0].Id, correction.AuditRelatedLedgerEntryId);
        Assert.Equal(HistoryId, correction.EventHistoryId);
        Assert.Equal(("Sky", "Sky", rows[0].DkpPoolId), (correction.EventName, correction.EventType, correction.DkpPoolId));
        Assert.Equal("wrong rate", correction.EditReason);
        Assert.StartsWith("Correction by Boss: wrong rate.", correction.Details);
        Assert.Contains($"10 -> {DkpCorrections.Amount(amount)}", correction.Details);
        Assert.Equal(amount, db.AppUserEventHistories.AsNoTracking().Single(p => p.Id == BobRow).EventDkp);
        // Alice is untouched.
        Assert.Single(Rows(db, "alice"));
    }

    [Fact]
    public async Task SetDkp_WithNoReason_SaysWhoOnly()
    {
        await using var db = NewDb();
        await SeedAsync(db);

        await Edits(db).SetParticipantDkpAsync(HistoryId, BobRow, 8, new DkpCorrectionNote("Boss", "  "), CancellationToken.None);

        var correction = Rows(db, "bob")[1];
        Assert.Null(correction.EditReason);
        Assert.Equal("Correction by Boss. Set by hand: 10 -> 8.", correction.Details);
    }

    [Fact]
    public async Task SetDkp_ToWhatTheyAlreadyHave_WritesNothing()
    {
        await using var db = NewDb();
        await SeedAsync(db);

        await Edits(db).SetParticipantDkpAsync(HistoryId, BobRow, 10, Note, CancellationToken.None);

        Assert.Single(Rows(db, "bob"));
        Assert.Equal(10, (await OutcomeAsync(db, "bob")).Balance);
    }

    [Fact]
    public async Task RepeatEdits_EachAppendOnlyTheDifference()
    {
        var (old, db) = await TwinsAsync();
        foreach (var amount in new[] { 6d, 9d, 9d, 2.5d })
        {
            await OldSetAsync(old, "bob", amount);
            await Edits(db).SetParticipantDkpAsync(HistoryId, BobRow, amount, Note, CancellationToken.None);
            Assert.Equal(await OutcomeAsync(old, "bob"), await OutcomeAsync(db, "bob"));
        }

        var rows = Rows(db, "bob");
        Assert.Equal(new[] { 10d, -4d, 3d, -6.5d }, rows.Select(r => r.Amount));
        Assert.All(rows.Skip(1), r => Assert.Equal(rows[0].Id, r.AuditRelatedLedgerEntryId));
        Assert.Equal(new Outcome(2.5, 2.5, 2.5, 0), await OutcomeAsync(db, "bob"));
    }

    // ---- remove an attendee ---------------------------------------------------------------------

    [Fact]
    public async Task Remove_TakesBackTheirTotalInOneCorrection_AndEndsWhereTheOldRemoveDid()
    {
        var (old, db) = await TwinsAsync();
        await OldRemoveAsync(old, "bob");

        Assert.Equal(EventHistoryEditOutcome.Done,
            await Edits(db).RemoveParticipantAsync(HistoryId, BobRow, Note, CancellationToken.None));

        Assert.Equal(await OutcomeAsync(old, "bob"), await OutcomeAsync(db, "bob"));
        Assert.Equal(new Outcome(0, null, 0, 0), await OutcomeAsync(db, "bob"));
        var rows = Rows(db, "bob");
        Assert.Equal(new[] { 10d, -10d }, rows.Select(r => r.Amount));
        Assert.StartsWith("Correction by Boss: wrong rate. Removed from the event: 10 -> 0.", rows[1].Details);
        Assert.False(db.AppUserEventHistories.Any(p => p.Id == BobRow));
    }

    [Fact]
    public async Task EditThenRemove_TakesBackWhatIsLeft()
    {
        var (old, db) = await TwinsAsync();
        await OldSetAsync(old, "bob", 6);
        await OldRemoveAsync(old, "bob");

        await Edits(db).SetParticipantDkpAsync(HistoryId, BobRow, 6, Note, CancellationToken.None);
        await Edits(db).RemoveParticipantAsync(HistoryId, BobRow, Note, CancellationToken.None);

        Assert.Equal(await OutcomeAsync(old, "bob"), await OutcomeAsync(db, "bob"));
        Assert.Equal(new[] { 10d, -4d, -6d }, Rows(db, "bob").Select(r => r.Amount));
        Assert.Equal(0, (await OutcomeAsync(db, "bob")).Balance);
    }

    // Remove keeps the first payout (corrected to 0), so adding Bob back gives him a second earned
    // row. A later edit corrects the total of both and files under the newer one.
    [Fact]
    public async Task RemoveThenAddBack_ThenEdit_CountsBothEarnedRowsOnce()
    {
        await using var db = NewDb();
        await SeedAsync(db);
        await Edits(db).RemoveParticipantAsync(HistoryId, BobRow, Note, CancellationToken.None);

        Assert.Equal(EventHistoryEditOutcome.Done, await Edits(db).AddParticipantAsync(
            HistoryId, "bob", 5, null, null, null, activeCredit: true, CancellationToken.None));
        var newRowId = db.AppUserEventHistories.AsNoTracking().Single(p => p.AppUserId == "bob").Id;
        Assert.Equal(5, (await OutcomeAsync(db, "bob")).Shown);

        await Edits(db).SetParticipantDkpAsync(HistoryId, newRowId, 7, Note, CancellationToken.None);

        var rows = Rows(db, "bob");
        Assert.Equal(new[] { 10d, -10d, 5d, 2d }, rows.Select(r => r.Amount));
        Assert.Equal(rows[2].Id, rows[3].AuditRelatedLedgerEntryId);
        Assert.Equal(new Outcome(7, 7, 7, 0), await OutcomeAsync(db, "bob"));
    }

    // ---- the event's rate -----------------------------------------------------------------------

    [Fact]
    public async Task RateChange_AppendsACorrectionPerAttendee_WithTheNote()
    {
        var (old, db) = await TwinsAsync();
        await OldSetAsync(old, "bob", 20);
        await OldSetAsync(old, "alice", 20);

        Assert.Equal(EventHistoryEditOutcome.Done, await Edits(db).EditEventAsync(HistoryId,
            new EventHistoryEditInput("Sky", "Sky", null, null, 20, Note), CancellationToken.None));

        foreach (var who in new[] { "bob", "alice" })
        {
            Assert.Equal(await OutcomeAsync(old, who), await OutcomeAsync(db, who));
            var rows = Rows(db, who);
            Assert.Equal(new[] { 10d, 10d }, rows.Select(r => r.Amount));
            Assert.Equal(DkpCorrections.EntryType, rows[1].EntryType);
            Assert.Equal("Correction by Boss: wrong rate. Rate 10 -> 20 DKP per hour: 10 -> 20.", rows[1].Details);
        }
    }

    // ---- nothing rewrites history ---------------------------------------------------------------

    // Every save on these paths only ever ADDS ledger rows.
    [Fact]
    public async Task NoEditPath_UpdatesOrDeletesALedgerRow()
    {
        var watcher = new LedgerRowWatcher();
        await using var db = NewDb(watcher);
        await SeedAsync(db);
        watcher.Start();

        await Edits(db).SetParticipantDkpAsync(HistoryId, BobRow, 6, Note, CancellationToken.None);
        await Edits(db).SetParticipantDkpAsync(HistoryId, BobRow, 8, Note, CancellationToken.None);
        await Edits(db).EditEventAsync(HistoryId, new EventHistoryEditInput("Sky", "Sky", null, null, 15, Note), CancellationToken.None);
        await Edits(db).RemoveParticipantAsync(HistoryId, AliceRow, Note, CancellationToken.None);
        await Edits(db).RemoveParticipantAsync(HistoryId, BobRow, Note, CancellationToken.None);

        Assert.Empty(watcher.Changed);
        Assert.True(watcher.Added > 0);
        Assert.Equal(0, (await OutcomeAsync(db, "bob")).Balance);
        Assert.Equal(0, (await OutcomeAsync(db, "alice")).Balance);
    }

    // Someone whose membership has since been removed: the row is still written so the Past Event
    // shows the new figure, and there is no balance to move (the old edit moved none either).
    [Fact]
    public async Task AMemberWhoLeft_GetsTheCorrectionRow_AndNoBalance()
    {
        await using var db = NewDb();
        await SeedAsync(db);
        db.AppUserLinkshells.Remove(db.AppUserLinkshells.Single(m => m.AppUserId == "bob"));
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        Assert.Equal(EventHistoryEditOutcome.Done,
            await Edits(db).SetParticipantDkpAsync(HistoryId, BobRow, 4, Note, CancellationToken.None));

        Assert.Equal(new[] { 10d, -6d }, Rows(db, "bob").Select(r => r.Amount));
        var attendee = db.AppUserEventHistories.AsNoTracking().Single(p => p.Id == BobRow);
        Assert.Equal(4, (await PastEventDkp.LoadAsync(db, new[] { HistoryId }, CancellationToken.None)).For(attendee));
    }

    // Deleting the whole event still takes everything back, corrections included.
    [Fact]
    public async Task DeletingTheEvent_AfterCorrections_LeavesNothing()
    {
        await using var db = NewDb();
        await SeedAsync(db);
        await Edits(db).SetParticipantDkpAsync(HistoryId, BobRow, 6, Note, CancellationToken.None);
        await Edits(db).RemoveParticipantAsync(HistoryId, AliceRow, Note, CancellationToken.None);

        Assert.True(await Edits(db).DeleteEventAsync(HistoryId, CancellationToken.None));

        Assert.Empty(db.DkpLedgerEntries);
        Assert.Equal(0, db.AppUserLinkshells.AsNoTracking().Single(m => m.AppUserId == "bob").LinkshellDkp);
        Assert.Equal(0, db.AppUserLinkshells.AsNoTracking().Single(m => m.AppUserId == "alice").LinkshellDkp);
    }

    // ---- lifetime totals --------------------------------------------------------------------------

    [Fact]
    public void LifetimeTotals_FoldACorrectionIntoTheRowItCorrects()
    {
        var rows = new List<DkpLifetimeTotals.Row>
        {
            new(1, 10, "EventEarned", null),
            new(2, -4, DkpCorrections.EntryType, 1),
            new(3, -3, "LootSpent", null),
            // An audit is its own line, as before.
            new(4, -2, "AuditAdjustment", 1),
            // A correction taking a row below zero counts as spending, as the edited row did.
            new(5, 2, "EventEarned", null),
            new(6, -5, DkpCorrections.EntryType, 5),
        };

        Assert.Equal((6d, 8d), DkpLifetimeTotals.Sum(rows, _ => true));
        // The correction follows its row: before the seed watermark, neither counts.
        Assert.Equal((0d, 8d), DkpLifetimeTotals.Sum(rows, root => root.Id != 1));
    }

    // ---- through the controller: officers only --------------------------------------------------

    private static ActivityDkpController Controller(ApplicationDbContext db, string appUserId)
    {
        var userManager = new UserManager<AppUser>(
            new UserStore<AppUser>(db),
            Options.Create(new IdentityOptions()),
            new PasswordHasher<AppUser>(),
            Array.Empty<IUserValidator<AppUser>>(),
            Array.Empty<IPasswordValidator<AppUser>>(),
            new UpperInvariantLookupNormalizer(),
            new IdentityErrorDescriber(),
            null!,
            NullLogger<UserManager<AppUser>>.Instance);
        var pools = Pools(db);
        var http = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
                new[] { new Claim(ClaimTypes.NameIdentifier, appUserId) }, authenticationType: "Test")),
        };
        return ActivityControllerFactory.Create<ActivityDkpController>(
            http, db, userManager, new DkpLedgerWriter(db, pools, NullLogger<DkpLedgerWriter>.Instance), pools,
            TestPermissions.For(db));
    }

    [Fact]
    public async Task APlainMember_Gets403_AndNothingChanges()
    {
        await using var db = NewDb();
        await SeedAsync(db);
        var member = Controller(db, "alice");

        Assert.IsType<ForbidResult>(await member.SetEventHistoryParticipantDkpAsync(
            HistoryId, BobRow, new ActivitySetParticipantDkpRequest(50, "mine now"), CancellationToken.None));
        Assert.IsType<ForbidResult>(await member.RemoveEventHistoryParticipantAsync(
            HistoryId, BobRow, new ActivityRemoveEventHistoryParticipantRequest("bye"), CancellationToken.None));
        Assert.IsType<ForbidResult>(await member.EditEventHistoryAsync(
            HistoryId, new ActivityEditEventHistoryRequest("Sky", "Sky", null, null, 99, "more"), CancellationToken.None));

        Assert.Single(Rows(db, "bob"));
        Assert.Equal(10, (await OutcomeAsync(db, "bob")).Balance);
    }

    [Fact]
    public async Task TheLeader_WritesTheCorrection_WithTheirNameAndReason()
    {
        await using var db = NewDb();
        await SeedAsync(db);

        Assert.IsType<OkObjectResult>(await Controller(db, Officer).SetEventHistoryParticipantDkpAsync(
            HistoryId, BobRow, new ActivitySetParticipantDkpRequest(7, " left early "), CancellationToken.None));
        db.ChangeTracker.Clear();
        // No body, as an app loaded before the Reason box sends.
        Assert.IsType<OkObjectResult>(await Controller(db, Officer).RemoveEventHistoryParticipantAsync(
            HistoryId, AliceRow, null, CancellationToken.None));

        var bob = Rows(db, "bob")[1];
        Assert.Equal("Correction by Boss: left early. Set by hand: 10 -> 7.", bob.Details);
        Assert.Equal("left early", bob.EditReason);
        Assert.Equal("Correction by Boss. Removed from the event: 10 -> 0.", Rows(db, "alice")[1].Details);
    }

    [Fact]
    public async Task AReasonOverTheLimit_IsA400()
    {
        await using var db = NewDb();
        await SeedAsync(db);

        var result = await Controller(db, Officer).SetEventHistoryParticipantDkpAsync(
            HistoryId, BobRow, new ActivitySetParticipantDkpRequest(7, new string('x', DkpCorrections.MaxReasonLength + 1)),
            CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result);
        Assert.Single(Rows(db, "bob"));
    }

    // Records every ledger row a save would change or delete, once started.
    private sealed class LedgerRowWatcher : SaveChangesInterceptor
    {
        private bool _on;
        public List<string> Changed { get; } = new();
        public int Added { get; private set; }

        public void Start() => _on = true;

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (_on && eventData.Context is not null)
            {
                foreach (var entry in eventData.Context.ChangeTracker.Entries<DkpLedgerEntry>())
                {
                    if (entry.State is EntityState.Modified or EntityState.Deleted)
                    {
                        Changed.Add($"{entry.State} #{entry.Entity.Id} {entry.Entity.EntryType}");
                    }
                    else if (entry.State == EntityState.Added)
                    {
                        Added++;
                    }
                }
            }
            return base.SavingChangesAsync(eventData, result, cancellationToken);
        }
    }
}
