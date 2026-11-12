using System.Text.Json;
using LinkshellManagerDiscordApp.Authorization;
using LinkshellManagerDiscordApp.Controllers;
using LinkshellManagerDiscordApp.Data;
using LinkshellManagerDiscordApp.Models;
using LinkshellManagerDiscordApp.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LinkshellManager.Tests;

// Loot posted AFTER End Camp.
//
// End Camp keeps an HNM board's Events row and parks it for the next pop (WdFinalizedAt stamped,
// CommencementStartTime cleared). Every loot path used to treat any Events row as live, so a drop
// posted right after the camp ended was recorded uncharged on the parked board and waited for the
// NEXT camp's End Camp -- and was never charged at all if the board was cancelled or deleted.
//
// Now only a RUNNING event (EventRunState) holds loot for its close. A parked board's loot is filed
// under the camp it belongs to -- the board's newest archive -- and charged at once; with no
// archive it is refused.
public class LootAfterEndCampTests
{
    private const int Ls = 1;
    private const int BoardId = 10;
    private const int OlderArchiveId = 100;
    private const int NewestArchiveId = 101;
    private static readonly DateTime Now = DateTime.UtcNow;

    private static ApplicationDbContext NewDb() =>
        new(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static ManualLootService NewService(ApplicationDbContext db)
    {
        var pools = new DkpPoolResolver(db, new DkpPoolProvisioner(db));
        return new ManualLootService(
            db,
            new DkpLedgerWriter(db, pools, NullLogger<DkpLedgerWriter>.Instance),
            pools,
            new DkpPoolBalanceService(db, pools));
    }

    // A linkshell with Alice (100 DKP) and a Fafnir board in the given state.
    private static async Task<ApplicationDbContext> SeededAsync(Action<Event> boardState, bool withArchives = true)
    {
        var db = NewDb();
        db.Linkshells.Add(new Linkshell { Id = Ls, LinkshellName = "LS", LootStructure = "Dkp" });
        db.Users.Add(new AppUser { Id = "u1", UserName = "alice" });
        db.AppUserLinkshells.Add(new AppUserLinkshell
        {
            Id = 1, LinkshellId = Ls, AppUserId = "u1", CharacterName = "Alice", LinkshellDkp = 100,
            Rank = LinkshellRanks.Leader,
        });
        var board = new Event
        {
            Id = BoardId, LinkshellId = Ls, EventName = "Fafnir", EventType = "HNM",
            AssignedMonsterName = "Fafnir", StartTime = Now.AddHours(20),
        };
        boardState(board);
        db.Events.Add(board);

        if (withArchives)
        {
            // Two camps ended on this board; the newer one is where a late drop belongs.
            foreach (var (historyId, endedAt) in new[] { (OlderArchiveId, Now.AddDays(-3)), (NewestArchiveId, Now.AddMinutes(-10)) })
            {
                db.EventHistories.Add(new EventHistory
                {
                    Id = historyId, LinkshellId = Ls, EventName = "Fafnir", EventType = "HNM",
                    StartTime = endedAt.AddHours(-2), EndTime = endedAt, TimeStamp = endedAt,
                });
                db.WindowEvents.Add(new WindowEvent
                {
                    LinkshellId = Ls, Name = "Fafnir", SourceEventId = BoardId, CampEventHistoryId = historyId,
                    CampEndedAtUtc = endedAt, CreatedAtUtc = endedAt, FirstCapturedAtUtc = endedAt,
                    LastCapturedAtUtc = endedAt,
                });
            }
        }

        await db.SaveChangesAsync();
        return db;
    }

    // What End Camp leaves behind.
    private static void Parked(Event ev)
    {
        ev.CommencementStartTime = null;
        ev.WdFinalizedAt = Now.AddMinutes(-10);
        ev.HnmDefeatedAt = Now.AddMinutes(-10);
    }

    private static void Running(Event ev) => ev.CommencementStartTime = Now.AddHours(-1);

    // ---- The rule.

    [Fact]
    public void IsRunning_MeansStartedAndNotEndedByEndCamp()
    {
        Assert.True(EventRunState.IsRunning(new Event { CommencementStartTime = Now }));
        Assert.False(EventRunState.IsRunning(new Event()));                                   // queued
        Assert.False(EventRunState.IsRunning(new Event { WdFinalizedAt = Now }));             // parked by End Camp
        Assert.False(EventRunState.IsRunning(new Event { CommencementStartTime = Now, WdFinalizedAt = Now }));
        // A timed event's EndTime is the PLANNED end from the event form, set while it runs.
        Assert.True(EventRunState.IsRunning(new Event { CommencementStartTime = Now, EndTime = Now.AddHours(2) }));
    }

    [Fact]
    public async Task RunningExpression_FiltersAQuery_TheSameWay()
    {
        await using var db = NewDb();
        db.Events.AddRange(
            new Event { Id = 1, LinkshellId = Ls, CommencementStartTime = Now },
            new Event { Id = 2, LinkshellId = Ls },
            new Event { Id = 3, LinkshellId = Ls, WdFinalizedAt = Now },
            new Event { Id = 4, LinkshellId = Ls, CommencementStartTime = Now, EndTime = Now.AddHours(2) });
        await db.SaveChangesAsync();

        var running = await db.Events.Where(EventRunState.Running).Select(e => e.Id).OrderBy(id => id).ToListAsync();

        Assert.Equal([1, 4], running);
    }

    // ---- ManualLootService (Loot History's Add loot and both addon routes land here).

    [Fact]
    public async Task ParkedBoard_FilesUnderTheNewestCamp_AndChargesNow()
    {
        await using var db = await SeededAsync(Parked);

        var result = await NewService(db).AddAsync(
            Ls, ManualLootTarget.Parse("live", BoardId, null), "Ridill", "Alice", 30, null, null, CancellationToken.None);

        Assert.True(result.Success, result.Error);
        var loot = await db.EventLootDetails.SingleAsync();
        Assert.Null(loot.EventId);                       // not left on the parked board
        Assert.Equal(NewestArchiveId, loot.EventHistoryId);
        Assert.NotNull(loot.DkpDebitedAt);               // charged, so no close charges it again
        Assert.Equal(30, loot.ActualDeductedDkp);
        var debit = Assert.Single(await db.DkpLedgerEntries.ToListAsync());
        Assert.Equal(-30, debit.Amount);
        Assert.Equal(NewestArchiveId, debit.EventHistoryId);
    }

    [Fact]
    public async Task ParkedBoardWithNoArchive_IsRefused_AndNothingIsSaved()
    {
        await using var db = await SeededAsync(Parked, withArchives: false);

        var result = await NewService(db).AddAsync(
            Ls, ManualLootTarget.Parse("live", BoardId, null), "Ridill", "Alice", 30, null, null, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("isn't running", result.Error);
        Assert.Empty(await db.EventLootDetails.ToListAsync());
        Assert.Empty(await db.DkpLedgerEntries.ToListAsync());
    }

    [Fact]
    public async Task RunningCamp_StillHoldsTheLootForEndCamp()
    {
        await using var db = await SeededAsync(Running);

        var result = await NewService(db).AddAsync(
            Ls, ManualLootTarget.Parse("live", BoardId, null), "Ridill", "Alice", 30, null, null, CancellationToken.None);

        Assert.True(result.Success, result.Error);
        var loot = await db.EventLootDetails.SingleAsync();
        Assert.Equal(BoardId, loot.EventId);
        Assert.Null(loot.DkpDebitedAt);
        Assert.Empty(await db.DkpLedgerEntries.ToListAsync());
    }

    [Fact]
    public async Task TimedEventThatHasNotStarted_IsRefused()
    {
        await using var db = await SeededAsync(ev =>
        {
            ev.EventType = "Dynamis";
            ev.AssignedMonsterName = null;
            ev.EndTime = Now.AddHours(23);
        }, withArchives: false);

        var result = await NewService(db).AddAsync(
            Ls, ManualLootTarget.Parse("live", BoardId, null), "Ridill", "Alice", 30, null, null, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Empty(await db.EventLootDetails.ToListAsync());
    }

    [Fact]
    public async Task RunningTimedEvent_WithAPlannedEnd_HoldsTheLootForItsClose()
    {
        await using var db = await SeededAsync(ev =>
        {
            ev.EventType = "Dynamis";
            ev.CommencementStartTime = Now.AddHours(-1);
            ev.EndTime = Now.AddHours(2);
        }, withArchives: false);

        var result = await NewService(db).AddAsync(
            Ls, ManualLootTarget.Parse("live", BoardId, null), "Ridill", "Alice", 30, null, null, CancellationToken.None);

        Assert.True(result.Success, result.Error);
        Assert.Equal(BoardId, (await db.EventLootDetails.SingleAsync()).EventId);
        Assert.Empty(await db.DkpLedgerEntries.ToListAsync());
    }

    // ---- The addon routes.

    private static AddonApiController NewAddonController(ApplicationDbContext db)
    {
        var http = new DefaultHttpContext();
        http.Items[AddonApiAuthAttribute.TokenContextKey] = new AddonApiToken
        {
            Id = 1, LinkshellId = Ls, IssuedToAppUserId = "u1", TokenPrefix = "att_1", TokenHash = "hash-1",
            CreatedAt = Now,
        };
        http.Items[AddonApiAuthAttribute.LinkshellIdContextKey] = Ls;
        var pools = new DkpPoolResolver(db, new DkpPoolProvisioner(db));
        return new AddonApiController(
            db, null!, null!, null!, null!, null!, null!,
            new DkpLedgerWriter(db, pools, NullLogger<DkpLedgerWriter>.Instance),
            pools, new DkpPoolBalanceService(db, pools), null!, null!, TestPermissions.For(db),
            NullLogger<AddonApiController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = http },
        };
    }

    private static readonly AddonApiController.AddonPostLootRequest Ridill = new("Ridill", "Alice", 30);

    [Fact]
    public async Task AddonEventLoot_OnAParkedBoard_LandsOnTheCampThatJustEnded()
    {
        await using var db = await SeededAsync(Parked);

        var result = await NewAddonController(db).PostEventLootAsync(
            BoardId, Ridill, NewService(db), CancellationToken.None);

        var body = JsonSerializer.SerializeToElement(Assert.IsType<OkObjectResult>(result).Value);
        Assert.True(body.GetProperty("eventLinked").GetBoolean());
        var loot = await db.EventLootDetails.SingleAsync();
        Assert.Equal(NewestArchiveId, loot.EventHistoryId);
        Assert.NotNull(loot.DkpDebitedAt);
        Assert.Equal(-30, Assert.Single(await db.DkpLedgerEntries.ToListAsync()).Amount);
    }

    [Fact]
    public async Task AddonEventLoot_OnAParkedBoardWithNoArchive_IsA400()
    {
        await using var db = await SeededAsync(Parked, withArchives: false);

        var result = await NewAddonController(db).PostEventLootAsync(
            BoardId, Ridill, NewService(db), CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result);
        Assert.Empty(await db.EventLootDetails.ToListAsync());
    }

    [Fact]
    public async Task AddonTodLoot_AfterEndCamp_LandsOnTheBoardsNewestCamp()
    {
        await using var db = await SeededAsync(Parked);
        db.Tods.Add(new Tod { Id = 7, LinkshellId = Ls, MonsterName = "Fafnir", Time = Now.AddMinutes(-15) });
        await db.SaveChangesAsync();

        var result = await NewAddonController(db).PostTodLootAsync(7, Ridill, NewService(db), CancellationToken.None);

        Assert.IsType<OkObjectResult>(result);
        var loot = await db.EventLootDetails.SingleAsync();
        Assert.Null(loot.EventId);
        Assert.Equal(NewestArchiveId, loot.EventHistoryId);
        Assert.NotNull(loot.DkpDebitedAt);
    }

    [Fact]
    public async Task AddonTodLoot_PrefersTheRunningCamp_OverAParkedBoard()
    {
        await using var db = await SeededAsync(Parked);
        db.Events.Add(new Event
        {
            Id = 11, LinkshellId = Ls, EventName = "Fafnir 2", EventType = "HNM", AssignedMonsterName = "Fafnir",
            StartTime = Now.AddHours(-2), CommencementStartTime = Now.AddHours(-2),
        });
        db.Tods.Add(new Tod { Id = 7, LinkshellId = Ls, MonsterName = "Fafnir", Time = Now.AddMinutes(-15) });
        await db.SaveChangesAsync();

        var result = await NewAddonController(db).PostTodLootAsync(7, Ridill, NewService(db), CancellationToken.None);

        Assert.IsType<OkObjectResult>(result);
        var loot = await db.EventLootDetails.SingleAsync();
        Assert.Equal(11, loot.EventId);
        Assert.Null(loot.DkpDebitedAt);
    }

    // ---- Pending loot and the Loot History picker.

    [Fact]
    public async Task UnchargedLootLeftOnAParkedBoard_StillCountsAgainstTheWinner()
    {
        await using var db = await SeededAsync(Parked);
        // A row filed on the parked board before this fix: uncharged, waiting for the next End Camp.
        db.EventLootDetails.Add(new EventLootDetail
        {
            LinkshellId = Ls, EventId = BoardId, ItemName = "Ridill", ItemWinner = "Alice",
            ItemWinnerAppUserId = "u1", WinningDkpSpent = 40,
        });
        await db.SaveChangesAsync();

        var pending = await AuctionDkpService.ComputePendingLiveLootSpendByUserAsync(db, Ls, CancellationToken.None);

        Assert.Equal(40, pending["u1"]);
    }

    // The per-POOL twin has to agree, or a linkshell with several pools checks the pool balance
    // without the parked board's pending loot and lets the winner overspend that pool.
    [Fact]
    public async Task UnchargedLootLeftOnAParkedBoard_StillCountsAgainstTheWinnersPool()
    {
        await using var db = await SeededAsync(Parked);
        db.DkpPools.Add(new DkpPool { Id = 5, LinkshellId = Ls, Name = "Main", IsDefault = true });
        db.EventLootDetails.Add(new EventLootDetail
        {
            LinkshellId = Ls, EventId = BoardId, ItemName = "Ridill", ItemWinner = "Alice",
            ItemWinnerAppUserId = "u1", WinningDkpSpent = 40,
        });
        await db.SaveChangesAsync();

        var method = typeof(AuctionDkpService).GetMethod(
            "ComputePoolPendingLiveLootSpendByUserAsync",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
        var pending = await (Task<Dictionary<string, double>>)method.Invoke(
            null, [db, Ls, 5, CancellationToken.None])!;

        Assert.Equal(40, pending["u1"]);
    }

    // The live card's own Add loot (POST api/activity/events/{id}/loot) needs a signed-in HTTP
    // context, so its guard is pinned by source like the other ActivityDataController routes. A
    // stale card open across End Camp must not drop uncharged loot on the parked board.
    [Fact]
    public void LiveCardAddLoot_RefusesAnEventThatIsNotRunning()
    {
        var text = File.ReadAllText(Path.Combine(FindRepoPath("Controllers"), "ActivityDataController.EventsParticipation.cs"));
        var from = text.IndexOf("public async Task<IActionResult> AddLootAsync(", StringComparison.Ordinal);
        var add = text.IndexOf("_dbContext.EventLootDetails.Add(", from, StringComparison.Ordinal);
        Assert.True(from >= 0 && add > from, "AddLootAsync not found.");

        var guard = text.IndexOf("if (!EventRunState.IsRunning(eventEntity))", from, StringComparison.Ordinal);
        Assert.True(guard > from && guard < add, "AddLootAsync must refuse a non-running event before saving loot.");
    }

    [Fact]
    public void LootPicker_ListsOnlyRunningEventsAsLive()
    {
        var text = File.ReadAllText(Path.Combine(FindRepoPath("Controllers"), "ActivityDataController.LootHistory.cs"));
        var from = text.IndexOf("GetLootEventOptionsAsync(", StringComparison.Ordinal);
        var to = text.IndexOf("var pastQuery", from, StringComparison.Ordinal);
        Assert.True(from >= 0 && to > from, "GetLootEventOptionsAsync not found.");

        Assert.Contains(".Where(EventRunState.Running)", text[from..to]);
    }

    private static string FindRepoPath(string relative)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, relative);
            if (Directory.Exists(candidate))
            {
                return candidate;
            }
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException($"Could not locate '{relative}' above {AppContext.BaseDirectory}.");
    }
}
