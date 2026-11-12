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

namespace LinkshellManager.Tests;

// P3.5 (backend-02 / logic-13 / db-design-09): the app's End Event button and the addon's End Event
// were two hand-written copies of one close, and they drifted (alt-won loot, Loot Council pay,
// outside sign-ups, the pending gate). Both now run EventEndService.EndEventCoreAsync, with the
// app's own rules as explicit EndEventOptions.
//
// These close the SAME seeded event twice -- once through the app's End Event action, once through
// the core with EndEventOptions.Activity -- and compare every ledger row, balance, history row,
// loot row and archived window. Written while the action still had its own copy of the close, so
// they proved the core reproduces it exactly before the copy was deleted; the pinned figures keep
// the app's End Event paying exactly that from now on.
public class EndEventOnePathTests
{
    private const int Ls = 7;
    private const int EventId = 100;
    private const string Officer = "officer";

    private static ApplicationDbContext NewDb(string name) =>
        new(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(name)
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options);

    private static DkpPoolResolver NewPools(ApplicationDbContext db) => new(db, new DkpPoolProvisioner(db));

    private static DkpLedgerWriter NewWriter(ApplicationDbContext db) =>
        new(db, NewPools(db), NullLogger<DkpLedgerWriter>.Instance);

    // The app's controller with only what End Event touches: the database, the ledger, the pools,
    // the permission check, the activity recompute, and a signed-in officer.
    private static ActivityDataController NewActivityController(ApplicationDbContext db)
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

    // ------------------------------------------------------------------ seeds ---

    private sealed record Seed(string LootStructure = "Dkp", bool Windowed = false, bool AlicePending = false);

    // A 2-hour event with every case the two copies could disagree on:
    //   alice   present, wins one item under her main name and one under her alt
    //   bob     marked Absent
    //   carol   on break after 1h, wins an item recorded by account
    //   dave    signed up on the party board but never joined -- brought in at close
    //   Guest   an outside sign-up with no account
    //   + loot already charged, loot at 0 DKP, and loot to a name nobody has.
    private static void SeedEvent(ApplicationDbContext db, Seed seed)
    {
        db.Linkshells.Add(new Linkshell
        {
            Id = Ls, LinkshellName = "LS", LootStructure = seed.LootStructure, DkpRoundingIncrement = "Quarter",
        });
        db.Users.AddRange(
            new AppUser { Id = Officer, UserName = Officer, CharacterName = "Boss" },
            new AppUser { Id = "alice", UserName = "alice", CharacterName = "Alice", AltCharacterName1 = "Alicealt" },
            new AppUser { Id = "bob", UserName = "bob", CharacterName = "Bob" },
            new AppUser { Id = "carol", UserName = "carol", CharacterName = "Carol" },
            new AppUser { Id = "dave", UserName = "dave", CharacterName = "Dave" });
        db.AppUserLinkshells.AddRange(
            new AppUserLinkshell { Id = 1, LinkshellId = Ls, AppUserId = Officer, CharacterName = "Boss", Rank = LinkshellRanks.Leader, LinkshellDkp = 0 },
            new AppUserLinkshell { Id = 2, LinkshellId = Ls, AppUserId = "alice", CharacterName = "Alice", Rank = LinkshellRanks.Member, LinkshellDkp = 100 },
            new AppUserLinkshell { Id = 3, LinkshellId = Ls, AppUserId = "bob", CharacterName = "Bob", Rank = LinkshellRanks.Member, LinkshellDkp = 100 },
            new AppUserLinkshell { Id = 4, LinkshellId = Ls, AppUserId = "carol", CharacterName = "Carol", Rank = LinkshellRanks.Member, LinkshellDkp = 100 },
            new AppUserLinkshell { Id = 5, LinkshellId = Ls, AppUserId = "dave", CharacterName = "Dave", Rank = LinkshellRanks.Member, LinkshellDkp = 100 });
        // Opening balances go through the ledger, as in the app, so Hybrid reads them from it.
        foreach (var id in new[] { "alice", "bob", "carol", "dave" })
        {
            db.DkpLedgerEntries.Add(new DkpLedgerEntry
            {
                AppUserId = id, LinkshellId = Ls, EntryType = "ManualAdjustment", Amount = 100, OccurredAt = DateTime.UtcNow.AddDays(-1),
            });
        }

        var start = DateTime.UtcNow.AddHours(-2);
        db.Events.Add(new Event
        {
            Id = EventId,
            LinkshellId = Ls,
            EventName = "Dynamis - Xarcabard",
            EventType = "Dynamis",
            EventLocation = "Xarcabard",
            StartTime = start,
            CommencementStartTime = start,
            DkpPerHour = 10, // per window when windowed
            WindowCountOverride = seed.Windowed ? 3 : 1,
            CountsTowardActive = true,
        });
        db.AppUserEvents.AddRange(
            new AppUserEvent { Id = 10, EventId = EventId, AppUserId = "alice", CharacterName = "Alice", JobName = "WAR", IsVerified = seed.AlicePending ? null : true, StartTime = start },
            new AppUserEvent { Id = 11, EventId = EventId, AppUserId = "bob", CharacterName = "Bob", JobName = "WHM", IsVerified = false, StartTime = start },
            new AppUserEvent { Id = 12, EventId = EventId, AppUserId = "carol", CharacterName = "Carol", JobName = "BLM", IsVerified = true, StartTime = start, IsOnBreak = true, Duration = 1 },
            new AppUserEvent { Id = 13, EventId = EventId, DiscordUserId = "555", CharacterName = "Guest", JobName = "THF", StartTime = start });
        db.EventPartySlotSignups.Add(new EventPartySlotSignup
        {
            Id = 1, EventId = EventId, PartySetupSlotId = 1, AppUserId = "dave", CharacterName = "Dave", MainJob = "RDM",
            SignedUpAtUtc = start.AddMinutes(30),
        });
        db.EventLootDetails.AddRange(
            new EventLootDetail { Id = 60, EventId = EventId, LinkshellId = Ls, ItemName = "Ridill", ItemWinner = "Alice", WinningDkpSpent = 30 },
            new EventLootDetail { Id = 61, EventId = EventId, LinkshellId = Ls, ItemName = "Kraken Club", ItemWinner = "Alicealt", WinningDkpSpent = 20 },
            new EventLootDetail { Id = 62, EventId = EventId, LinkshellId = Ls, ItemName = "Relic", ItemWinner = "Carol", ItemWinnerAppUserId = "carol", WinningDkpSpent = 15 },
            new EventLootDetail { Id = 63, EventId = EventId, LinkshellId = Ls, ItemName = "Charged", ItemWinner = "Bob", WinningDkpSpent = 10, ActualDeductedDkp = 10, DkpDebitedAt = start },
            new EventLootDetail { Id = 64, EventId = EventId, LinkshellId = Ls, ItemName = "Free", ItemWinner = "Bob", WinningDkpSpent = 0 },
            new EventLootDetail { Id = 65, EventId = EventId, LinkshellId = Ls, ItemName = "Lost", ItemWinner = "Nobody", WinningDkpSpent = 5 });

        if (seed.Windowed)
        {
            db.EventAttendanceWindows.AddRange(
                new EventAttendanceWindow { Id = 1, EventId = EventId, SequenceNumber = 1, PostedAt = start },
                new EventAttendanceWindow { Id = 2, EventId = EventId, SequenceNumber = 2, PostedAt = start.AddMinutes(30) },
                new EventAttendanceWindow { Id = 3, EventId = EventId, SequenceNumber = 3, PostedAt = start.AddMinutes(60) });
            db.AppUserEventWindows.AddRange(
                new AppUserEventWindow { EventAttendanceWindowId = 1, AppUserEventId = 10, AppUserId = "alice" },
                new AppUserEventWindow { EventAttendanceWindowId = 2, AppUserEventId = 10, AppUserId = "alice" },
                new AppUserEventWindow { EventAttendanceWindowId = 3, AppUserEventId = 10, AppUserId = "alice" },
                new AppUserEventWindow { EventAttendanceWindowId = 1, AppUserEventId = 12, AppUserId = "carol" },
                new AppUserEventWindow { EventAttendanceWindowId = 2, AppUserEventId = 13 });
        }
        db.SaveChanges();
    }

    // ---------------------------------------------------------------- runners ---

    private sealed record Run(string DbName, IActionResult? Action, EventEndService.EndEventResult? Core);

    private static async Task<Run> EndThroughTheApp(Seed seed)
    {
        var name = Guid.NewGuid().ToString();
        using (var seedDb = NewDb(name)) SeedEvent(seedDb, seed);

        using var db = NewDb(name);
        var action = await NewActivityController(db).EndEventAsync(EventId, CancellationToken.None);
        return new Run(name, action, null);
    }

    private static async Task<Run> EndThroughTheCore(Seed seed, EventEndService.EndEventOptions options)
    {
        var name = Guid.NewGuid().ToString();
        using (var seedDb = NewDb(name)) SeedEvent(seedDb, seed);

        using var db = NewDb(name);
        // Loaded exactly as the app's End Event action loads it.
        var ev = await db.Events
            .Include(e => e.AppUserEvents)
            .Include(e => e.EventLootDetails)
            .Include(e => e.Linkshell)
            .SingleAsync(e => e.Id == EventId);
        var result = await EventEndService.EndEventCoreAsync(db, NewWriter(db), NewPools(db), ev, options);
        return new Run(name, null, result);
    }

    // Everything a close writes, as text so two runs compare line by line. Times are left out
    // (each run has its own "now"); durations are rounded far below anything that moves DKP.
    private static List<string> Snapshot(string dbName)
    {
        using var db = NewDb(dbName);
        var lines = new List<string>();

        lines.AddRange(db.DkpLedgerEntries.AsNoTracking().ToList()
            .Select(e => $"ledger {e.AppUserId}|{e.EntryType}|{e.Amount}|pool={e.DkpPoolId}|pinned={e.DkpPoolPinned}"
                + $"|{e.CharacterName}|{e.EventName}|{e.EventType}|{e.EventLocation}|{e.ItemName}|{e.Details}"
                + $"|loot={e.SourceEventLootDetailId}|history={(e.EventHistoryId is null ? "none" : "set")}")
            .OrderBy(s => s, StringComparer.Ordinal));
        lines.AddRange(db.AppUserLinkshells.AsNoTracking().ToList()
            .Select(m => $"balance {m.AppUserId}|{m.LinkshellDkp}")
            .OrderBy(s => s, StringComparer.Ordinal));
        lines.AddRange(db.EventHistories.AsNoTracking().ToList()
            .Select(h => $"history {h.LinkshellId}|{h.EventName}|{h.EventType}|{h.EventLocation}|{h.DkpPerHour}"
                + $"|{h.EventDkp}|{h.CountsTowardActive}|{Math.Round(h.Duration ?? -1, 3)}|setup={h.PartySetupId}"));
        lines.AddRange(db.AppUserEventHistories.AsNoTracking().ToList()
            .Select(h => $"row {h.AppUserId}|{h.CharacterName}|{h.JobName}|{h.SubJobName}|{h.JobType}"
                + $"|{Math.Round(h.Duration ?? -1, 3)}|{h.EventDkp}|verified={h.IsVerified}|active={h.ActiveCredit}"
                + $"|windows={h.WindowsAttended}|quick={h.IsQuickJoin}|{h.Proctor}")
            .OrderBy(s => s, StringComparer.Ordinal));
        lines.AddRange(db.EventLootDetails.AsNoTracking().ToList()
            .Select(l => $"loot {l.Id}|event={l.EventId}|history={(l.EventHistoryId is null ? "none" : "set")}"
                + $"|{l.ActualDeductedDkp}|debited={(l.DkpDebitedAt is null ? "no" : "yes")}")
            .OrderBy(s => s, StringComparer.Ordinal));
        lines.AddRange(db.EventAttendanceWindows.AsNoTracking().ToList()
            .Select(w => $"window {w.Id}|event={w.EventId}|history={(w.EventHistoryId is null ? "none" : "set")}")
            .OrderBy(s => s, StringComparer.Ordinal));
        lines.Add($"events={db.Events.Count()} participations={db.AppUserEvents.Count()} windowRows={db.AppUserEventWindows.Count()}");
        return lines;
    }

    private static async Task<List<string>> AssertBothPathsMatch(Seed seed)
    {
        var app = await EndThroughTheApp(seed);
        var core = await EndThroughTheCore(seed, EventEndService.EndEventOptions.Activity);

        var appRows = Snapshot(app.DbName);
        Assert.Equal(appRows, Snapshot(core.DbName));
        return appRows;
    }

    private static double Balance(List<string> rows, string appUserId) =>
        double.Parse(rows.Single(r => r.StartsWith($"balance {appUserId}|", StringComparison.Ordinal)).Split('|')[1]);

    // ------------------------------------------------------------------ tests ---

    [Fact]
    public async Task DkpLinkshell_BothPathsWriteTheSameClose()
    {
        var rows = await AssertBothPathsMatch(new Seed());

        // Alice: +20 for 2h, -30 Ridill (main name), -20 Kraken Club (won on her alt).
        Assert.Equal(70, Balance(rows, "alice"));
        // Bob was marked Absent: 0 DKP; his already-charged item is not charged again.
        Assert.Equal(100, Balance(rows, "bob"));
        // Carol went on break after 1h: +10, -15 for the item recorded by account.
        Assert.Equal(95, Balance(rows, "carol"));
        // Dave was brought in from the party board at close and paid from his sign-up time.
        Assert.Equal(115, Balance(rows, "dave"));
        // The outside sign-up is board-only: no history row.
        Assert.DoesNotContain(rows, r => r.Contains("|Guest|", StringComparison.Ordinal));
        // Every loot row moved onto the past event; nothing still points at the deleted event.
        Assert.Equal(6, rows.Count(r => r.StartsWith("loot ", StringComparison.Ordinal) && r.Contains("history=set")));
        // Loot to a name nobody in the linkshell has is not charged to anyone (today's rule).
        Assert.Contains("loot 65|event=|history=set||debited=no", rows);
        Assert.Contains("events=0 participations=0 windowRows=0", rows);
    }

    [Fact]
    public async Task LootCouncilLinkshell_BothPathsPayNothingAndChargeNothing()
    {
        var rows = await AssertBothPathsMatch(new Seed(LootStructure: "LootCouncil"));

        Assert.DoesNotContain(rows, r => r.Contains("|EventEarned|", StringComparison.Ordinal));
        Assert.DoesNotContain(rows, r => r.Contains("|LootSpent|", StringComparison.Ordinal));
        Assert.All(rows.Where(r => r.StartsWith("row ", StringComparison.Ordinal)),
            r => Assert.Contains("|0|verified=", r));
        Assert.Equal(100, Balance(rows, "alice"));
    }

    [Fact]
    public async Task HybridLinkshell_BothPathsChargeTheSamePercentages()
    {
        var rows = await AssertBothPathsMatch(new Seed(LootStructure: "Hybrid"));

        // Alice: 100 + 20 earned = 120; 30% of that is 36, then 20% of the 84 left is 16.75 (the
        // second item sees the first item's charge), rounded to the quarter.
        Assert.Equal(67.25, Balance(rows, "alice"));
        Assert.Equal(93.5, Balance(rows, "carol"));
        Assert.Contains(rows, r => r.Contains("|LootSpent|-36|", StringComparison.Ordinal)
            && r.Contains("Hybrid DKP spent (30%) on loot: Ridill."));
    }

    [Fact]
    public async Task WindowedEvent_BothPathsPayPerWindowAndArchiveTheWindows()
    {
        var rows = await AssertBothPathsMatch(new Seed(Windowed: true));

        // Alice: 3 windows x 10, minus 30 + 20 of loot.
        Assert.Equal(80, Balance(rows, "alice"));
        // Carol: 1 window, break state does not move windowed DKP; minus 15.
        Assert.Equal(95, Balance(rows, "carol"));
        // Dave joined at close and was in no window: 0.
        Assert.Equal(100, Balance(rows, "dave"));
        Assert.Contains(rows, r => r.StartsWith("row alice|", StringComparison.Ordinal) && r.Contains("windows=3"));
        Assert.Equal(3, rows.Count(r => r.StartsWith("window ", StringComparison.Ordinal) && r.Contains("event=|history=set")));
        // The windows' roster rows survive with them.
        Assert.Contains("events=0 participations=0 windowRows=5", rows);
    }

    [Fact]
    public async Task PendingMember_BothPathsRefuseAndChangeNothing()
    {
        var seed = new Seed(AlicePending: true);
        var app = await EndThroughTheApp(seed);
        var core = await EndThroughTheCore(seed, EventEndService.EndEventOptions.Activity);

        var bad = Assert.IsType<BadRequestObjectResult>(app.Action);
        Assert.Contains("1 member(s) still pending", bad.Value!.ToString());
        Assert.True(core.Core!.Refused);
        Assert.Equal(1, core.Core.PendingAttendeeCount);

        var appRows = Snapshot(app.DbName);
        Assert.Equal(appRows, Snapshot(core.DbName));
        Assert.DoesNotContain(appRows, r => r.StartsWith("history ", StringComparison.Ordinal));
        Assert.Contains(appRows, r => r.StartsWith("events=1 ", StringComparison.Ordinal));
        // Dave's board sign-up was not brought in either: the refusal comes first.
        Assert.Contains("events=1 participations=4 windowRows=0", appRows);
    }

    // ---------------------------------------------- the addon's rules, pinned ---

    // The addon's End Event keeps the rules it has always had (EndEventOptions.Addon). Only a
    // non-HNM event reaches it today, and the addon is HNM-only, but the differences are pinned so
    // changing one is a decision, not an accident.
    [Fact]
    public async Task AddonRules_PayLootCouncil_ArchiveOutsideSignups_AndDoNotWaitForPending()
    {
        var lootCouncil = await EndThroughTheCore(new Seed(LootStructure: "LootCouncil"), EventEndService.EndEventOptions.Addon);
        var rows = Snapshot(lootCouncil.DbName);
        Assert.Equal(120, Balance(rows, "alice"));
        Assert.DoesNotContain(rows, r => r.Contains("|LootSpent|", StringComparison.Ordinal));
        Assert.Contains(rows, r => r.StartsWith("row |Guest|", StringComparison.Ordinal));

        var pending = await EndThroughTheCore(new Seed(AlicePending: true), EventEndService.EndEventOptions.Addon);
        Assert.False(pending.Core!.Refused);
        Assert.Contains("events=0 participations=0 windowRows=0", Snapshot(pending.DbName));
    }

    // The app's End Event runs the shared close with its own options, and keeps no copy of the
    // close beside it: a second copy is exactly what drifted before.
    [Fact]
    public void TheAppsEndEvent_HasNoCloseLoopOfItsOwn()
    {
        var source = File.ReadAllText(Path.Combine(FindRepoRoot(), "Controllers", "ActivityDataController.EventsLifecycle.cs"));

        Assert.Contains("EventEndService.EndEventCoreAsync(", source, StringComparison.Ordinal);
        Assert.Contains("EventEndService.EndEventOptions.Activity", source, StringComparison.Ordinal);
        Assert.DoesNotContain("\"EventEarned\"", source, StringComparison.Ordinal);
        Assert.DoesNotContain("\"LootSpent\"", source, StringComparison.Ordinal);
        Assert.DoesNotContain("new AppUserEventHistory", source, StringComparison.Ordinal);
        Assert.DoesNotContain("new EventHistory", source, StringComparison.Ordinal);
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

    // Leaving options out means the addon's rules, so existing callers keep their behaviour.
    [Fact]
    public void DefaultOptions_AreTheAddonRules()
    {
        Assert.Equal(EventEndService.EndEventOptions.Addon, new EventEndService.EndEventOptions());
        Assert.True(EventEndService.EndEventOptions.Activity.RequireNoPendingAttendees);
        Assert.True(EventEndService.EndEventOptions.Activity.LootCouncilPaysNoDkp);
        Assert.True(EventEndService.EndEventOptions.Activity.SkipAccountlessParticipants);
    }
}
