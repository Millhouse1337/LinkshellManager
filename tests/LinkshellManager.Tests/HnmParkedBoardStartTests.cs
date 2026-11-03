using System;
using System.Threading;
using System.Threading.Tasks;
using LinkshellManagerDiscordApp.Data;
using LinkshellManagerDiscordApp.Models;
using LinkshellManagerDiscordApp.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace LinkshellManager.Tests;

// Starting a board that is still parked from its last camp.
//
// Ending a camp with Repost on keeps its Event row, stamped HnmDefeatedAt + WdFinalizedAt, until the
// poller re-posts it before the next pop. Started from the addon before then, it went live while
// still marked ended, and the Activity -- which keeps ended camps out of the live list and started
// ones out of the queue -- showed it nowhere. The start now revives it first.
public class HnmParkedBoardStartTests
{
    private const int LinkshellId = 12;
    private const string Monster = "Fafnir/Nidhogg";

    private static readonly DateTime Now = new(2026, 11, 3, 2, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime NextRepop = Now.AddHours(20);

    private static ApplicationDbContext NewInMemoryContext() =>
        new(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static async Task<ApplicationDbContext> SeededAsync(Event board)
    {
        var db = NewInMemoryContext();
        db.Linkshells.Add(new Linkshell { Id = LinkshellId, LinkshellName = "TestLinkshell", EnableHnmSection = true });
        db.Tods.Add(new Tod
        {
            Id = 7,
            LinkshellId = LinkshellId,
            MonsterName = Monster,
            DayNumber = 1,
            Time = Now.AddHours(-2),
            RepopTime = NextRepop,
        });
        db.HnmRecurringBoards.Add(new HnmRecurringBoard
        {
            Id = 1,
            LinkshellId = LinkshellId,
            MonsterName = Monster,
            Enabled = true,
            LeadHours = 1,
            CreatedAt = Now.AddDays(-7),
        });
        db.Events.Add(board);
        await db.SaveChangesAsync();
        return db;
    }

    private static Event ParkedBoard() => new()
    {
        Id = 400,
        LinkshellId = LinkshellId,
        EventName = "faf",
        EventType = "HNM",
        AssignedMonsterName = Monster,
        DayNumber = 1,
        StartTime = NextRepop,
        SourceTodId = 7,
        HnmWindowNumber = 1,
        HnmDefeatedAt = Now.AddHours(-2),
        HnmRepostAt = NextRepop.AddHours(-1),
        WdFinalizedAt = Now.AddHours(-2),
    };

    [Fact]
    public async Task AParkedBoard_ComesBackAsAFreshCampForTheNextPop()
    {
        using var db = await SeededAsync(ParkedBoard());
        var ev = await db.Events.SingleAsync();

        var revived = await HnmRecurringBoardService.ReviveParkedBoardForStartAsync(db, ev, CancellationToken.None);

        Assert.True(revived);
        Assert.Null(ev.HnmDefeatedAt);
        Assert.Null(ev.HnmRepostAt);
        Assert.Null(ev.WdFinalizedAt);
        Assert.Equal(NextRepop, ev.StartTime);
        Assert.Equal(2, ev.DayNumber);
        Assert.Equal(7, ev.SourceTodId);
        // The poller must treat this ToD as handled, or it would revive the now-live camp again.
        Assert.Equal(7, (await db.HnmRecurringBoards.SingleAsync()).LastSourceTodId);
    }

    [Fact]
    public async Task AFreshBoard_IsLeftAlone()
    {
        var fresh = ParkedBoard();
        fresh.HnmDefeatedAt = null;
        fresh.HnmRepostAt = null;
        fresh.WdFinalizedAt = null;
        using var db = await SeededAsync(fresh);
        var ev = await db.Events.SingleAsync();

        var revived = await HnmRecurringBoardService.ReviveParkedBoardForStartAsync(db, ev, CancellationToken.None);

        Assert.False(revived);
        Assert.Equal(1, ev.DayNumber);
        Assert.Null((await db.HnmRecurringBoards.SingleAsync()).LastSourceTodId);
    }
}
