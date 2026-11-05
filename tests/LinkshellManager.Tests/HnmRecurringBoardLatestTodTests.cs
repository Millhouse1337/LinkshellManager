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

// "The monster's latest ToD" means the latest POP, not the highest row id.
//
// The bug (OnlyFrens Turtle, 2026-11-05): ToD 581 had been edited to the newest kill, but ToD 582
// -- an OLDER kill -- had a higher id. Everything that asked for the latest ToD by id got 582:
// the poller saw it as already handled and never re-posted the board, and the Update button
// revived the parked board onto 582's pop, three days in the past, so it went live at once.
public class HnmRecurringBoardLatestTodTests
{
    private const int LinkshellId = 4;
    private const string Monster = "Adamantoise";

    private static readonly DateTime NewestPop = new(2026, 11, 5, 12, 28, 29, DateTimeKind.Utc);
    private static readonly DateTime OldPop = new(2026, 11, 2, 16, 17, 51, DateTimeKind.Utc);

    private static ApplicationDbContext NewInMemoryContext() =>
        new(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    // The prod shape: the newest kill in the LOWER id, an older kill in the higher id, the board
    // stamped as having handled the older one, and the Turtle board parked after its last camp.
    private static async Task<(ApplicationDbContext Db, HnmRecurringBoard Board)> SeededAsync()
    {
        var db = NewInMemoryContext();
        db.Linkshells.Add(new Linkshell { Id = LinkshellId, LinkshellName = "Test" });
        db.Tods.Add(new Tod
        {
            Id = 581, LinkshellId = LinkshellId, MonsterName = Monster, DayNumber = 4,
            Time = NewestPop.AddHours(-22), RepopTime = NewestPop,
        });
        db.Tods.Add(new Tod
        {
            Id = 582, LinkshellId = LinkshellId, MonsterName = "Adamantoise/Aspidochelone", DayNumber = 2,
            Time = OldPop.AddHours(-22), RepopTime = OldPop,
        });
        var board = new HnmRecurringBoard
        {
            Id = 43, LinkshellId = LinkshellId, MonsterName = Monster, Enabled = true,
            LeadHours = 3, LastSourceTodId = 582, EventNameTemplate = "Turtle",
        };
        db.HnmRecurringBoards.Add(board);
        db.Events.Add(new Event
        {
            Id = 419, LinkshellId = LinkshellId, EventName = "Turtle", EventType = "HNM",
            AssignedMonsterName = "Adamantoise/Aspidochelone", StartTime = OldPop, DayNumber = 3,
            SourceTodId = 582, HnmDefeatedAt = OldPop.AddHours(1),
        });
        await db.SaveChangesAsync();
        return (db, board);
    }

    [Fact]
    public async Task Poller_RePostsForTheNewestPop_EvenWhenAnOlderKillHasAHigherId()
    {
        var (db, board) = await SeededAsync();
        var poller = new HnmRecurringBoardBackgroundService(
            null!, NullLogger<HnmRecurringBoardBackgroundService>.Instance);

        // 2h before the pop: inside the 3h lead.
        await poller.ProcessBoardAsync(db, board, NewestPop.AddHours(-2), CancellationToken.None);

        var ev = await db.Events.SingleAsync();
        Assert.Null(ev.HnmDefeatedAt);           // brought back
        Assert.Equal(NewestPop, ev.StartTime);   // for the real next pop
        Assert.Equal(581, ev.SourceTodId);
        Assert.Equal(581, board.LastSourceTodId);
    }

    [Fact]
    public async Task ReviveForStart_UsesTheNewestPop_NotAPopAlreadyPast()
    {
        var (db, _) = await SeededAsync();
        var ev = await db.Events.SingleAsync();

        Assert.True(await HnmRecurringBoardService.ReviveParkedBoardForStartAsync(db, ev, CancellationToken.None));

        // On 582's pop the board would be days in the past and auto-start would fire at once.
        Assert.Equal(NewestPop, ev.StartTime);
        Assert.Equal(581, ev.SourceTodId);
    }

    [Fact]
    public async Task LatestTodId_IsTheNewestPop()
    {
        var (db, _) = await SeededAsync();

        Assert.Equal(581, await HnmRecurringBoardService.LatestTodIdAsync(db, LinkshellId, Monster, CancellationToken.None));
    }
}
