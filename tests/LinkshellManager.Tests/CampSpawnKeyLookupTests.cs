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

// Round 5 (CampSpawnKeySwitch): camps are found by their SpawnKey now, one equality, where they used
// to be found by "is AssignedMonsterName one of every spelling of this spawn". A ToD names the half
// that died ("Nidhogg"), while its camp may be on the combined label or on the other half, so each
// lookup below must still find the camp under every spelling, and must not find another spawn's.
//
// The camps' EventName is deliberately NOT a monster name, so a match here can only come from the key.
public class CampSpawnKeyLookupTests
{
    private const int Ls = 41;
    private static readonly DateTime Now = DateTime.UtcNow;

    private static ApplicationDbContext NewDb() =>
        new(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    // Every spelling a Fafnir/Nidhogg camp can carry.
    public static TheoryData<string> FafnirCamps => new() { "Fafnir/Nidhogg", "Fafnir", "Nidhogg", " fafnir " };

    // ---- Addon loot by ToD (AddonApiController.PostTodLootAsync) ----

    private static async Task<ApplicationDbContext> LootSeededAsync(string campMonster)
    {
        var db = NewDb();
        db.Linkshells.Add(new Linkshell { Id = Ls, LinkshellName = "LS", LootStructure = "Dkp" });
        db.Users.Add(new AppUser { Id = "u1", UserName = "alice" });
        db.AppUserLinkshells.Add(new AppUserLinkshell
        {
            Id = 1, LinkshellId = Ls, AppUserId = "u1", CharacterName = "Alice", LinkshellDkp = 100,
            Rank = LinkshellRanks.Leader,
        });
        db.Events.Add(new Event
        {
            Id = 11, LinkshellId = Ls, EventName = "Tuesday camp", EventType = "HNM", AssignedMonsterName = campMonster,
            StartTime = Now.AddHours(-2), CommencementStartTime = Now.AddHours(-2),
        });
        db.Tods.Add(new Tod { Id = 7, LinkshellId = Ls, MonsterName = "Nidhogg", Time = Now.AddMinutes(-15) });
        await db.SaveChangesAsync();
        return db;
    }

    private static async Task<EventLootDetail> PostLootOnTodAsync(ApplicationDbContext db)
    {
        var http = new DefaultHttpContext();
        http.Items[AddonApiAuthAttribute.TokenContextKey] = new AddonApiToken
        {
            Id = 1, LinkshellId = Ls, IssuedToAppUserId = "u1", TokenPrefix = "att_1", TokenHash = "hash-1",
            CreatedAt = Now,
        };
        http.Items[AddonApiAuthAttribute.LinkshellIdContextKey] = Ls;
        var pools = new DkpPoolResolver(db, new DkpPoolProvisioner(db));
        var writer = new DkpLedgerWriter(db, pools, NullLogger<DkpLedgerWriter>.Instance);
        var balances = new DkpPoolBalanceService(db, pools);
        var controller = new AddonApiController(
            db, null!, null!, null!, null!, writer, pools, balances, null!, null!, TestPermissions.For(db),
            NullLogger<AddonApiController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = http },
        };

        var result = await controller.PostTodLootAsync(
            7, new AddonApiController.AddonPostLootRequest("Ridill", "Alice", 30),
            new ManualLootService(db, writer, pools, balances), new AddonLootReplayGuard(), CancellationToken.None);

        Assert.IsType<OkObjectResult>(result);
        return await db.EventLootDetails.SingleAsync();
    }

    [Theory]
    [MemberData(nameof(FafnirCamps))]
    public async Task AddonLoot_OnANidhoggToD_FindsTheFafnirCamp_UnderAnySpelling(string campMonster)
    {
        await using var db = await LootSeededAsync(campMonster);

        var loot = await PostLootOnTodAsync(db);

        Assert.Equal(11, loot.EventId);
    }

    [Fact]
    public async Task AddonLoot_OnANidhoggToD_DoesNotFindAnotherSpawnsCamp()
    {
        await using var db = await LootSeededAsync("Behemoth/King Behemoth");

        var loot = await PostLootOnTodAsync(db);

        Assert.Null(loot.EventId);
        Assert.Null(loot.EventHistoryId);
    }

    // A ToD with no name has no key. It must not land on a camp that has no monster (and so no key).
    [Fact]
    public async Task AddonLoot_OnAToDWithNoName_DoesNotFindACampWithNoMonster()
    {
        await using var db = await LootSeededAsync("Fafnir");
        (await db.Events.SingleAsync()).AssignedMonsterName = null;
        (await db.Tods.SingleAsync()).MonsterName = " ";
        await db.SaveChangesAsync();
        Assert.Null(await db.Events.Select(e => e.SpawnKey).SingleAsync());

        var loot = await PostLootOnTodAsync(db);

        Assert.Null(loot.EventId);
    }

    // ---- HnmDayCycle.DayForAddonTodAsync ----

    private static Event LiveCamp(int id, string monster, int day) => new()
    {
        Id = id, LinkshellId = Ls, EventName = "Tuesday camp", EventType = "HNM", AssignedMonsterName = monster,
        DayNumber = day, StartTime = Now.AddHours(-3), CommencementStartTime = Now.AddHours(-3),
    };

    [Theory]
    [MemberData(nameof(FafnirCamps))]
    public async Task DayCycle_ANidhoggToD_TakesTheDayOfTheFafnirCamp_UnderAnySpelling(string campMonster)
    {
        await using var db = NewDb();
        db.Events.Add(LiveCamp(20, campMonster, day: 5));
        await db.SaveChangesAsync();

        Assert.Equal(5, await HnmDayCycle.DayForAddonTodAsync(db, Ls, "Nidhogg", 2, CancellationToken.None));
    }

    [Fact]
    public async Task DayCycle_ANidhoggToD_IgnoresAnotherSpawnsCamp()
    {
        await using var db = NewDb();
        db.Events.Add(LiveCamp(20, "Behemoth/King Behemoth", day: 5));
        await db.SaveChangesAsync();

        Assert.Equal(2, await HnmDayCycle.DayForAddonTodAsync(db, Ls, "Nidhogg", 2, CancellationToken.None));
    }

    // ---- The standing board: the poller's revive, the parked-board sync, and Stop Repeating ----

    private static readonly DateTime Repop = new(2026, 11, 20, 20, 0, 0, DateTimeKind.Utc);

    private static async Task<(ApplicationDbContext Db, HnmRecurringBoard Board)> ParkedBoardAsync(string parkedMonster)
    {
        var db = NewDb();
        db.Linkshells.Add(new Linkshell { Id = Ls, LinkshellName = "LS" });
        db.Tods.Add(new Tod
        {
            Id = 70, LinkshellId = Ls, MonsterName = "Nidhogg", DayNumber = 3,
            Time = Repop.AddHours(-84), RepopTime = Repop,
        });
        var board = new HnmRecurringBoard { Id = 9, LinkshellId = Ls, MonsterName = "Fafnir/Nidhogg", Enabled = true, LeadHours = 1 };
        db.HnmRecurringBoards.Add(board);
        db.Events.Add(new Event
        {
            Id = 600, LinkshellId = Ls, EventName = "Tuesday camp", EventType = "HNM", AssignedMonsterName = parkedMonster,
            DayNumber = 3, StartTime = Repop.AddHours(-90), SourceTodId = 1, HnmWindowNumber = 1,
            HnmDefeatedAt = Repop.AddHours(-84), WdFinalizedAt = Repop.AddHours(-84),
        });
        await db.SaveChangesAsync();
        return (db, board);
    }

    [Theory]
    [MemberData(nameof(FafnirCamps))]
    public async Task ThePoller_RevivesTheParkedFafnirCamp_ForANidhoggToD(string parkedMonster)
    {
        var (db, board) = await ParkedBoardAsync(parkedMonster);
        await using var _ = db;

        await new HnmRecurringBoardBackgroundService(null!, NullLogger<HnmRecurringBoardBackgroundService>.Instance)
            .ProcessBoardAsync(db, board, Repop.AddMinutes(-30), CancellationToken.None);

        var ev = await db.Events.SingleAsync();
        Assert.Null(ev.HnmDefeatedAt);
        Assert.Equal(Repop, ev.StartTime);
        Assert.Equal(70, ev.SourceTodId);
    }

    [Fact]
    public async Task ThePoller_LeavesAnotherSpawnsParkedCampAlone_AndPostsItsOwn()
    {
        var (db, board) = await ParkedBoardAsync("Behemoth/King Behemoth");
        await using var _ = db;

        await new HnmRecurringBoardBackgroundService(null!, NullLogger<HnmRecurringBoardBackgroundService>.Instance)
            .ProcessBoardAsync(db, board, Repop.AddMinutes(-30), CancellationToken.None);

        var behemoth = await db.Events.SingleAsync(e => e.Id == 600);
        Assert.NotNull(behemoth.HnmDefeatedAt);
        Assert.Equal(Repop.AddHours(-90), behemoth.StartTime);
        var posted = await db.Events.SingleAsync(e => e.Id != 600);
        Assert.Equal("fafnir/nidhogg", posted.SpawnKey);
        Assert.Equal(70, posted.SourceTodId);
    }

    [Theory]
    [MemberData(nameof(FafnirCamps))]
    public async Task SyncParkedBoards_ANidhoggToD_MovesTheParkedFafnirCamp(string parkedMonster)
    {
        var (db, _) = await ParkedBoardAsync(parkedMonster);
        await using var __ = db;

        await HnmRecurringBoardService.SyncParkedBoardsForTodAsync(db, Ls, "Nidhogg", CancellationToken.None, Repop.AddHours(-10));

        var ev = await db.Events.SingleAsync();
        Assert.Equal(Repop, ev.StartTime);
        Assert.Equal(Repop.AddHours(-1), ev.HnmRepostAt);
    }

    [Fact]
    public async Task SyncParkedBoards_ANidhoggToD_LeavesAnotherSpawnsParkedCampAlone()
    {
        var (db, _) = await ParkedBoardAsync("Behemoth/King Behemoth");
        await using var __ = db;

        await HnmRecurringBoardService.SyncParkedBoardsForTodAsync(db, Ls, "Nidhogg", CancellationToken.None, Repop.AddHours(-10));

        var ev = await db.Events.SingleAsync();
        Assert.Equal(Repop.AddHours(-90), ev.StartTime);
        Assert.Null(ev.HnmRepostAt);
    }

    // Removing a "Fafnir/Nidhogg" board keeps Repeat on while another board for the spawn is up,
    // under any spelling, and turns it off when the only other board is another spawn's.
    [Theory]
    [InlineData("Nidhogg", true)]
    [InlineData("Fafnir", true)]
    [InlineData("Fafnir/Nidhogg", true)]
    [InlineData("Behemoth/King Behemoth", false)]
    public async Task StopRepeating_KeepsRepeatOn_WhileAnotherBoardForTheSpawnIsUp(string otherMonster, bool staysOn)
    {
        await using var db = NewDb();
        db.Linkshells.Add(new Linkshell { Id = Ls, LinkshellName = "LS" });
        db.HnmRecurringBoards.Add(new HnmRecurringBoard { Id = 9, LinkshellId = Ls, MonsterName = "Fafnir/Nidhogg", Enabled = true });
        var removed = new Event
        {
            Id = 30, LinkshellId = Ls, EventName = "Tuesday camp", EventType = "HNM", AssignedMonsterName = "Fafnir/Nidhogg",
            StartTime = Repop,
        };
        db.Events.Add(removed);
        db.Events.Add(new Event
        {
            Id = 31, LinkshellId = Ls, EventName = "Other camp", EventType = "HNM", AssignedMonsterName = otherMonster,
            StartTime = Repop,
        });
        await db.SaveChangesAsync();

        await HnmRecurringBoardService.StopRepeatingForRemovedBoardAsync(db, removed, CancellationToken.None);

        Assert.Equal(staysOn, (await db.HnmRecurringBoards.SingleAsync()).Enabled);
    }
}
