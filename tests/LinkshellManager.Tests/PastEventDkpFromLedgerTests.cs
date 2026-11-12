using System.Security.Claims;
using System.Text.Json;
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

namespace LinkshellManager.Tests;

// P3.7 (db-design-10): Past Events show each attendee's DKP from the ledger -- their earned row for
// the event plus every DKP audit of it -- not the EventDkp copy stored at close. That copy went
// stale whenever money moved without patching it: an audit written before audits moved it, an
// audit of a camp payout, an officer's "Add" on a camp.
//
// These go through the real routes: the DKP audit an officer submits, then the Past Events list.
public class PastEventDkpFromLedgerTests
{
    private const int Ls = 7;
    private const int TimedHistoryId = 50;
    private const int CampHistoryId = 51;
    private const string Officer = "officer";

    private static ApplicationDbContext NewDb() =>
        new(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options);

    private static DkpPoolResolver NewPools(ApplicationDbContext db) => new(db, new DkpPoolProvisioner(db));

    private static DkpLedgerWriter NewWriter(ApplicationDbContext db) =>
        new(db, NewPools(db), NullLogger<DkpLedgerWriter>.Instance);

    // The app's controller with only what these routes touch, and a signed-in Leader.
    private static ActivityDataController NewController(ApplicationDbContext db)
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
        var pools = NewPools(db);
        var http = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
                new[] { new Claim(ClaimTypes.NameIdentifier, Officer) }, authenticationType: "Test")),
        };
        return new ActivityDataController(
            db, null!, null!, null!, null!, userManager, null!, null!, null!, null!, null!, null!,
            NullLogger<ActivityDataController>.Instance, null!, null!, new MemberActivityService(db), null!,
            new DkpLedgerWriter(db, pools, NullLogger<DkpLedgerWriter>.Instance), pools,
            null!, null!, null!, null!, null!, null!, null!, null!, null!, null!, null!, null!, null!, null!,
            TestPermissions.For(db))
        {
            ControllerContext = new ControllerContext { HttpContext = http },
        };
    }

    // Alice and Bob each earned 10 at a closed timed event (ledger + stored figure agree, as at close).
    private static async Task<(DkpLedgerEntry AliceEarned, DkpLedgerEntry BobEarned)> SeedTimedEventAsync(ApplicationDbContext db)
    {
        db.Linkshells.Add(new Linkshell { Id = Ls, LinkshellName = "LS" });
        db.Users.AddRange(
            new AppUser { Id = Officer, UserName = Officer, CharacterName = "Boss" },
            new AppUser { Id = "alice", UserName = "alice", CharacterName = "Alice" },
            new AppUser { Id = "bob", UserName = "bob", CharacterName = "Bob" });
        db.AppUserLinkshells.AddRange(
            new AppUserLinkshell { Id = 1, LinkshellId = Ls, AppUserId = Officer, CharacterName = "Boss", Rank = LinkshellRanks.Leader, LinkshellDkp = 0 },
            new AppUserLinkshell { Id = 2, LinkshellId = Ls, AppUserId = "alice", CharacterName = "Alice", Rank = LinkshellRanks.Member, LinkshellDkp = 0 },
            new AppUserLinkshell { Id = 3, LinkshellId = Ls, AppUserId = "bob", CharacterName = "Bob", Rank = LinkshellRanks.Member, LinkshellDkp = 0 });
        db.EventHistories.Add(new EventHistory
        {
            Id = TimedHistoryId, LinkshellId = Ls, EventName = "Sky", EventType = "Sky", DkpPerHour = 10,
            EndTime = DateTime.UtcNow.AddHours(-1),
            AppUserEventHistories =
            {
                new AppUserEventHistory { Id = 70, AppUserId = "alice", CharacterName = "Alice", EventDkp = 10, Duration = 1 },
                new AppUserEventHistory { Id = 71, AppUserId = "bob", CharacterName = "Bob", EventDkp = 10, Duration = 1 },
            },
        });
        await db.SaveChangesAsync();

        var writer = NewWriter(db);
        var alice = db.AppUserLinkshells.Single(m => m.AppUserId == "alice");
        var bob = db.AppUserLinkshells.Single(m => m.AppUserId == "bob");
        var aliceEarned = await writer.AppendAsync(alice, "EventEarned", 10, DateTime.UtcNow, DkpPoolRef.Derived("Sky"),
            new DkpEntryContext(CharacterName: "Alice", EventType: "Sky", EventHistoryId: TimedHistoryId), CancellationToken.None);
        var bobEarned = await writer.AppendAsync(bob, "EventEarned", 10, DateTime.UtcNow, DkpPoolRef.Derived("Sky"),
            new DkpEntryContext(CharacterName: "Bob", EventType: "Sky", EventHistoryId: TimedHistoryId), CancellationToken.None);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return (aliceEarned!, bobEarned!);
    }

    private static JsonElement Body(IActionResult result) =>
        JsonSerializer.SerializeToElement(Assert.IsType<OkObjectResult>(result).Value);

    // Past Events list: attendee figures by character name for one event, plus the event total.
    private static async Task<(Dictionary<string, double?> ByName, double Total)> ListShowsAsync(
        ApplicationDbContext db, int historyId)
    {
        var body = Body(await NewController(db).GetEventHistoryAsync(Ls, CancellationToken.None));
        var history = body.GetProperty("histories").EnumerateArray().Single(h => h.GetProperty("id").GetInt32() == historyId);
        var byName = history.GetProperty("participants").EnumerateArray().ToDictionary(
            p => p.GetProperty("characterName").GetString()!,
            p => p.GetProperty("eventDkp").ValueKind == JsonValueKind.Null ? (double?)null : p.GetProperty("eventDkp").GetDouble());
        return (byName, history.GetProperty("eventDkp").GetDouble());
    }

    private static double Balance(ApplicationDbContext db, string appUserId) =>
        db.AppUserLinkshells.AsNoTracking().Single(m => m.AppUserId == appUserId).LinkshellDkp ?? 0;

    // Characterization: with no audit, the ledger and the stored copy agree, so nothing shown changes.
    [Fact]
    public async Task AnUnauditedEvent_ShowsTheSameFiguresAsBefore()
    {
        await using var db = NewDb();
        await SeedTimedEventAsync(db);

        var (byName, total) = await ListShowsAsync(db, TimedHistoryId);

        Assert.Equal(10, byName["Alice"]);
        Assert.Equal(10, byName["Bob"]);
        Assert.Equal(20, total);
    }

    [Fact]
    public async Task AnAuditAfterClose_ShowsTheCorrectedFigure_AndNoLongerPatchesTheStoredCopy()
    {
        await using var db = NewDb();
        var (_, bobEarned) = await SeedTimedEventAsync(db);

        var audit = await NewController(db).CreateDkpAuditAsync(
            new ActivityDkpAuditRequest(Ls, "bob", "Adjust", bobEarned.Id, null, 8, "Left early"),
            CancellationToken.None);
        Assert.IsType<OkObjectResult>(audit);
        db.ChangeTracker.Clear();

        Assert.Equal(8, Balance(db, "bob"));
        var (byName, total) = await ListShowsAsync(db, TimedHistoryId);
        Assert.Equal(8, byName["Bob"]);
        Assert.Equal(10, byName["Alice"]);
        Assert.Equal(18, total);

        // The audit path wrote only the ledger row; the stored copy is no longer what anyone reads.
        Assert.Equal(10, db.AppUserEventHistories.AsNoTracking().Single(p => p.AppUserId == "bob").EventDkp);
    }

    // An audit written before audits carried the event id (and before they moved the figure) left
    // Past Events quoting 10 while Bob had 8. The ledger read finds it through the row it corrects.
    [Fact]
    public async Task AnOldAuditFiledUnderNoEvent_StillShows()
    {
        await using var db = NewDb();
        var (_, bobEarned) = await SeedTimedEventAsync(db);
        var bob = db.AppUserLinkshells.Single(m => m.AppUserId == "bob");
        await NewWriter(db).AppendAsync(bob, "AuditAdjustment", -2, DateTime.UtcNow, DkpPoolRef.Derived("Sky"),
            new DkpEntryContext(CharacterName: "Bob", AuditRelatedLedgerEntryId: bobEarned.Id), CancellationToken.None);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        Assert.Equal(8, (await ListShowsAsync(db, TimedHistoryId)).ByName["Bob"]);
    }

    // Loot charged at the event is filed under it too, but it is spending, not what the member earned
    // there -- and neither is an audit of that charge.
    [Fact]
    public async Task LootFiledUnderTheEvent_IsNotTakenOffTheEarnedFigure()
    {
        await using var db = NewDb();
        await SeedTimedEventAsync(db);
        var writer = NewWriter(db);
        var alice = db.AppUserLinkshells.Single(m => m.AppUserId == "alice");
        var spent = await writer.AppendAsync(alice, "LootSpent", -5, DateTime.UtcNow, DkpPoolRef.Derived("Sky"),
            new DkpEntryContext(CharacterName: "Alice", ItemName: "Ridill", EventHistoryId: TimedHistoryId), CancellationToken.None);
        await db.SaveChangesAsync();
        await writer.AppendAsync(alice, "AuditAdjustment", 1, DateTime.UtcNow, DkpPoolRef.Derived("Sky"),
            new DkpEntryContext(CharacterName: "Alice", EventHistoryId: TimedHistoryId, AuditRelatedLedgerEntryId: spent!.Id),
            CancellationToken.None);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        Assert.Equal(10, (await ListShowsAsync(db, TimedHistoryId)).ByName["Alice"]);
    }

    // A camp archive is paid as SnapshotEarned rows. Here the stored copy is stale three ways the old
    // display could not see: an account posted before one-row-per-account (5 + 0 after folding), an
    // officer's audit of a camp payout, and an officer's audit "Add" (a second card's payout filed
    // under the same camp). A guest with no account keeps the stored figure: there is no ledger row
    // to read for them.
    [Fact]
    public async Task ACampArchive_ShowsTheSnapshotEarnedTotal()
    {
        await using var db = NewDb();
        await SeedTimedEventAsync(db);
        db.EventHistories.Add(new EventHistory
        {
            Id = CampHistoryId, LinkshellId = Ls, EventName = "Fafnir", EventType = "HNM", EndTime = DateTime.UtcNow,
            AppUserEventHistories =
            {
                new AppUserEventHistory { Id = 80, AppUserId = "alice", CharacterName = "Alice", EventDkp = 4 },
                new AppUserEventHistory { Id = 81, AppUserId = "bob", CharacterName = "Bob", EventDkp = 6 },
                new AppUserEventHistory { Id = 82, CharacterName = "Guest", EventDkp = 3 },
            },
        });
        db.WindowEvents.Add(new WindowEvent
        {
            Id = 90, LinkshellId = Ls, Name = "Fafnir", Status = WindowEventStatuses.Open, CreatedAtUtc = DateTime.UtcNow,
            FirstCapturedAtUtc = DateTime.UtcNow, LastCapturedAtUtc = DateTime.UtcNow, DkpAmount = 5,
            EntryType = WindowEventEntryTypes.FromMonsterName("Fafnir"), PostedToSheetAt = DateTime.UtcNow,
            CampEventHistoryId = CampHistoryId,
        });
        await db.SaveChangesAsync();

        var writer = NewWriter(db);
        var alice = db.AppUserLinkshells.Single(m => m.AppUserId == "alice");
        var bob = db.AppUserLinkshells.Single(m => m.AppUserId == "bob");
        async Task<DkpLedgerEntry> PayAsync(AppUserLinkshell member, double amount)
        {
            var row = await writer.AppendAsync(member, "SnapshotEarned", amount, DateTime.UtcNow, DkpPoolRef.Derived("HNM"),
                new DkpEntryContext(CharacterName: member.CharacterName, EventType: "HNM",
                    EventHistoryId: CampHistoryId, SourceWindowEventId: 90), CancellationToken.None);
            await db.SaveChangesAsync();
            return row!;
        }
        await PayAsync(alice, 5);
        await PayAsync(alice, 0);
        var bobPaid = await PayAsync(bob, 6);
        await writer.AppendAsync(alice, "SnapshotEarned", 2, DateTime.UtcNow, DkpPoolRef.Derived("HNM"),
            new DkpEntryContext(CharacterName: "Alice", EventType: "HNM", EventHistoryId: CampHistoryId), CancellationToken.None);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        // An officer audits Bob's camp payout from 6 down to 4, through the real route.
        Assert.IsType<OkObjectResult>(await NewController(db).CreateDkpAuditAsync(
            new ActivityDkpAuditRequest(Ls, "bob", "Adjust", bobPaid.Id, null, 4, "Arrived late"),
            CancellationToken.None));
        db.ChangeTracker.Clear();

        var (byName, total) = await ListShowsAsync(db, CampHistoryId);
        Assert.Equal(7, byName["Alice"]);
        Assert.Equal(4, byName["Bob"]);
        Assert.Equal(3, byName["Guest"]);
        Assert.Equal(14, total);
        // And it is what their balances actually got (10 from the timed event each, plus the camp).
        Assert.Equal(17, Balance(db, "alice"));
        Assert.Equal(14, Balance(db, "bob"));
    }

    // Typed decimals added in floating point leave leftovers (10.3 - 2.1 = 8.200000000000001), and
    // the Past Events table prints the number as it comes. The shown figure and total are clean.
    [Fact]
    public async Task ATypedDecimalAudit_ShowsACleanFigure()
    {
        await using var db = NewDb();
        await SeedTimedEventAsync(db);
        db.EventHistories.Add(new EventHistory
        {
            Id = CampHistoryId, LinkshellId = Ls, EventName = "Fafnir", EventType = "HNM", EndTime = DateTime.UtcNow,
            AppUserEventHistories = { new AppUserEventHistory { Id = 80, AppUserId = "alice", CharacterName = "Alice", EventDkp = 10.3 } },
        });
        await db.SaveChangesAsync();
        var alice = db.AppUserLinkshells.Single(m => m.AppUserId == "alice");
        var writer = NewWriter(db);
        var paid = await writer.AppendAsync(alice, "SnapshotEarned", 10.3, DateTime.UtcNow, DkpPoolRef.Derived("HNM"),
            new DkpEntryContext(CharacterName: "Alice", EventType: "HNM", EventHistoryId: CampHistoryId), CancellationToken.None);
        await db.SaveChangesAsync();
        await writer.AppendAsync(alice, "AuditAdjustment", -2.1, DateTime.UtcNow, DkpPoolRef.Derived("HNM"),
            new DkpEntryContext(CharacterName: "Alice", EventHistoryId: CampHistoryId, AuditRelatedLedgerEntryId: paid!.Id),
            CancellationToken.None);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var (byName, total) = await ListShowsAsync(db, CampHistoryId);
        Assert.Equal(8.2, byName["Alice"]);
        Assert.Equal(8.2, total);
    }
}
