using System;
using System.Threading;
using System.Threading.Tasks;
using LinkshellManagerDiscordApp.Data;
using LinkshellManagerDiscordApp.Models;
using LinkshellManagerDiscordApp.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LinkshellManager.Tests;

// Fafnir / Adamantoise / Behemoth: each camp + ToD moves the next board on a day, until the ToD says
// it was the HQ, and then the next board is Day 1.
//
// Three ways that broke: the addon's ToD Capture panel recorded every kill as the PREVIOUS ToD's day,
// so the board never got past it; an HQ posted from the game ("Nidhogg", no toggle) didn't count as
// HQ, so the cycle never reset; and a ToD with no day wiped the board's day.
public class HnmDayCycleTests
{
    private const int LinkshellId = 3;
    private const string Pair = "Fafnir/Nidhogg";
    private static readonly DateTime Repop = new(2026, 11, 5, 20, 0, 0, DateTimeKind.Utc);

    private static ApplicationDbContext NewInMemoryContext() =>
        new(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static Tod TodFor(string monster, int? day, bool hq = false) => new()
    {
        Id = 70, LinkshellId = LinkshellId, MonsterName = monster, DayNumber = day, Hq = hq,
        Time = Repop.AddHours(-22), RepopTime = Repop,
    };

    // ---- NextDay ----

    [Fact]
    public void AnNqKill_MovesTheNextBoardOnADay() =>
        Assert.Equal(4, HnmDayCycle.NextDay(TodFor(Pair, 3), boardDay: 3));

    [Fact]
    public void TheHqToggle_StartsTheCycleOverAtDayOne() =>
        Assert.Equal(1, HnmDayCycle.NextDay(TodFor(Pair, 6, hq: true), boardDay: 6));

    [Theory]
    [InlineData("Nidhogg")]
    [InlineData("King Behemoth")]
    [InlineData("Aspidochelone")]
    public void AnHqNamedByTheKillMessage_StartsTheCycleOverAtDayOne(string stronger) =>
        Assert.Equal(1, HnmDayCycle.NextDay(TodFor(stronger, 5), boardDay: 5));

    [Theory]
    [InlineData("Fafnir")]
    [InlineData("Behemoth")]
    [InlineData("Adamantoise")]
    public void AnNqNamedByTheKillMessage_MovesOnADay(string baseName) =>
        Assert.Equal(3, HnmDayCycle.NextDay(TodFor(baseName, 2), boardDay: 2));

    [Fact]
    public void AToDWithNoDay_MovesOnFromTheBoardsDay() =>
        Assert.Equal(4, HnmDayCycle.NextDay(TodFor(Pair, null), boardDay: 3));

    [Fact]
    public void NoDayAnywhere_StaysNoDay() =>
        Assert.Null(HnmDayCycle.NextDay(TodFor(Pair, null), boardDay: null));

    // ---- DayForAddonTodAsync ----

    private static Event Camp(int day, bool live = true, bool parked = false) => new()
    {
        Id = 500, LinkshellId = LinkshellId, EventName = "faf", EventType = "HNM",
        AssignedMonsterName = Pair, DayNumber = day, StartTime = Repop.AddHours(-24),
        CommencementStartTime = live ? Repop.AddHours(-24) : null,
        HnmDefeatedAt = parked ? Repop.AddHours(-22) : null,
    };

    [Fact]
    public async Task TheLiveCampsDay_WinsOverTheDayTheAddonCarriedForward()
    {
        using var db = NewInMemoryContext();
        db.Events.Add(Camp(day: 4));
        await db.SaveChangesAsync();

        // The capture names the half that died; the addon sent the previous ToD's day 3.
        var day = await HnmDayCycle.DayForAddonTodAsync(db, LinkshellId, "Fafnir", 3, CancellationToken.None);

        Assert.Equal(4, day);
    }

    [Fact]
    public async Task WithNoLiveCamp_TheAddonsDayIsKept()
    {
        using var db = NewInMemoryContext();
        db.Events.Add(Camp(day: 4, live: false));
        await db.SaveChangesAsync();

        Assert.Equal(3, await HnmDayCycle.DayForAddonTodAsync(db, LinkshellId, "Nidhogg", 3, CancellationToken.None));
    }

    [Fact]
    public async Task AParkedBoard_IsNotTheLiveCamp()
    {
        using var db = NewInMemoryContext();
        db.Events.Add(Camp(day: 4, parked: true));
        await db.SaveChangesAsync();

        Assert.Equal(3, await HnmDayCycle.DayForAddonTodAsync(db, LinkshellId, "Fafnir", 3, CancellationToken.None));
    }

    [Fact]
    public async Task AMonsterWithoutADayCycle_IsLeftAlone()
    {
        using var db = NewInMemoryContext();
        var camp = Camp(day: 4);
        camp.AssignedMonsterName = "Jormungand";
        db.Events.Add(camp);
        await db.SaveChangesAsync();

        Assert.Null(await HnmDayCycle.DayForAddonTodAsync(db, LinkshellId, "Jormungand", null, CancellationToken.None));
    }

    // ---- The board actually coming back ----

    private static async Task<(ApplicationDbContext Db, HnmRecurringBoard Board)> ParkedOnAsync(Tod tod, int boardDay)
    {
        var db = NewInMemoryContext();
        db.Linkshells.Add(new Linkshell { Id = LinkshellId, LinkshellName = "Test" });
        db.Tods.Add(tod);
        var board = new HnmRecurringBoard
        {
            Id = 9, LinkshellId = LinkshellId, MonsterName = Pair, Enabled = true, LeadHours = 1,
        };
        db.HnmRecurringBoards.Add(board);
        db.Events.Add(new Event
        {
            Id = 600, LinkshellId = LinkshellId, EventName = "faf", EventType = "HNM",
            AssignedMonsterName = Pair, DayNumber = boardDay, StartTime = Repop, SourceTodId = tod.Id,
            HnmWindowNumber = 1, HnmDefeatedAt = Repop.AddHours(-22), WdFinalizedAt = Repop.AddHours(-22),
        });
        await db.SaveChangesAsync();
        return (db, board);
    }

    private static HnmRecurringBoardBackgroundService NewPoller() =>
        new(null!, NullLogger<HnmRecurringBoardBackgroundService>.Instance);

    [Fact]
    public async Task ThePoller_BringsTheBoardBackOnTheNextDay()
    {
        var (db, board) = await ParkedOnAsync(TodFor(Pair, 3), boardDay: 3);
        using var _ = db;

        await NewPoller().ProcessBoardAsync(db, board, Repop.AddMinutes(-30), CancellationToken.None);

        var ev = await db.Events.SingleAsync();
        Assert.Null(ev.HnmDefeatedAt);
        Assert.Equal(4, ev.DayNumber);
    }

    [Fact]
    public async Task ThePoller_BringsTheBoardBackOnDayOneAfterAnHqPostedFromTheGame()
    {
        var (db, board) = await ParkedOnAsync(TodFor("Nidhogg", 6), boardDay: 6);
        using var _ = db;

        await NewPoller().ProcessBoardAsync(db, board, Repop.AddMinutes(-30), CancellationToken.None);

        var ev = await db.Events.SingleAsync();
        Assert.Equal(1, ev.DayNumber);
        Assert.Equal(Pair, ev.AssignedMonsterName); // still the pair; Day 1 prints as Fafnir
    }

    [Fact]
    public async Task StartingTheParkedBoard_MovesOnFromTheBoardsDayWhenTheToDHasNone()
    {
        var (db, _) = await ParkedOnAsync(TodFor(Pair, null), boardDay: 3);
        using var __ = db;
        var ev = await db.Events.SingleAsync();

        var revived = await HnmRecurringBoardService.ReviveParkedBoardForStartAsync(db, ev, CancellationToken.None);

        Assert.True(revived);
        Assert.Equal(4, ev.DayNumber);
    }
}
