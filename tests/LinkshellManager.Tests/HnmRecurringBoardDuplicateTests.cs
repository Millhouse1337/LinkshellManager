using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LinkshellManagerDiscordApp.Data;
using LinkshellManagerDiscordApp.Models;
using LinkshellManagerDiscordApp.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LinkshellManager.Tests;

// The Repeat-on-ToD poller has to recognise a board that is already up for the pop it is about to
// post for -- whoever put it there, and whatever they called it.
//
// The bug: an officer created their own board for the next Fafnir pop, its start defaulting to the
// ToD's repop. The poller then posted the template's board for the SAME pop, because it only looked
// for an event with the template's name. Two boards, scheduled to the same second off one ToD, went
// live together at the pop.
public class HnmRecurringBoardDuplicateTests
{
    private const int LinkshellId = 1;
    private static readonly DateTime Repop = new(2026, 9, 19, 8, 58, 31, DateTimeKind.Utc);

    private static ApplicationDbContext NewInMemoryContext() =>
        new(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static HnmRecurringBoardBackgroundService NewPoller() =>
        new(null!, NullLogger<HnmRecurringBoardBackgroundService>.Instance);

    // A Fafnir ToD whose repop is the pop in question, and a standing template for that monster
    // under a name of its own -- the "`test" / "aaa" board from the report.
    private static async Task<(ApplicationDbContext Db, HnmRecurringBoard Board)> SeededAsync()
    {
        var db = NewInMemoryContext();
        db.Linkshells.Add(new Linkshell { Id = LinkshellId, LinkshellName = "Test" });
        db.Tods.Add(new Tod
        {
            Id = 50, LinkshellId = LinkshellId, MonsterName = "Fafnir",
            Time = Repop.AddHours(-22), RepopTime = Repop,
        });
        var board = new HnmRecurringBoard
        {
            Id = 7, LinkshellId = LinkshellId, MonsterName = "Fafnir", Enabled = true,
            LeadHours = 1, EventNameTemplate = "`test", EventLocation = "aaa",
        };
        db.HnmRecurringBoards.Add(board);
        await db.SaveChangesAsync();
        return (db, board);
    }

    private static Event OfficersBoard(string monster, DateTime startUtc) => new()
    {
        Id = 100, LinkshellId = LinkshellId, EventName = "ada", EventType = "HNM",
        EventLocation = "Qufim Island", AssignedMonsterName = monster, StartTime = startUtc,
    };

    [Fact]
    public async Task ABoardAlreadyUpForThePop_IsReusedRatherThanDuplicated()
    {
        var (db, board) = await SeededAsync();
        db.Events.Add(OfficersBoard("Fafnir", Repop));
        await db.SaveChangesAsync();

        await NewPoller().ProcessBoardAsync(db, board, Repop.AddMinutes(-5), CancellationToken.None);

        var officers = Assert.Single(await db.Events.ToListAsync());
        Assert.Equal("ada", officers.EventName);        // theirs, not renamed to the template
        Assert.Equal(50, officers.SourceTodId);         // linked to the ToD it is the board for
        Assert.Equal(50, board.LastSourceTodId);        // and this ToD is done with
    }

    // Fafnir and Nidhogg are one spawn, so a board put up under either name is that pop's board.
    [Fact]
    public async Task ABoardForTheOtherHalfOfTheSpawn_CountsAsTheSameBoard()
    {
        var (db, board) = await SeededAsync();
        db.Events.Add(OfficersBoard("Nidhogg", Repop.AddMinutes(3)));
        await db.SaveChangesAsync();

        await NewPoller().ProcessBoardAsync(db, board, Repop.AddMinutes(-5), CancellationToken.None);

        Assert.Single(await db.Events.ToListAsync());
    }

    // A board for the same monster on a DIFFERENT pop is not this pop's board -- tomorrow's camp
    // does not stop tonight's being posted.
    [Fact]
    public async Task ABoardForAnotherPop_DoesNotStopThisOne()
    {
        var (db, board) = await SeededAsync();
        db.Events.Add(OfficersBoard("Fafnir", Repop.AddHours(22)));
        await db.SaveChangesAsync();

        await NewPoller().ProcessBoardAsync(db, board, Repop.AddMinutes(-5), CancellationToken.None);

        var events = await db.Events.OrderBy(e => e.Id).ToListAsync();
        Assert.Equal(2, events.Count);
        Assert.Equal("`test", events[1].EventName);
    }

    // A different monster at the same moment is simply a different camp.
    [Fact]
    public async Task ABoardForAnotherMonster_DoesNotStopThisOne()
    {
        var (db, board) = await SeededAsync();
        db.Events.Add(OfficersBoard("Behemoth", Repop));
        await db.SaveChangesAsync();

        await NewPoller().ProcessBoardAsync(db, board, Repop.AddMinutes(-5), CancellationToken.None);

        Assert.Equal(2, await db.Events.CountAsync());
    }

    // The ordinary case still works: nothing up yet, so the template's board is posted.
    [Fact]
    public async Task WithNoBoardUp_TheTemplatesBoardIsPosted()
    {
        var (db, board) = await SeededAsync();

        await NewPoller().ProcessBoardAsync(db, board, Repop.AddMinutes(-5), CancellationToken.None);

        var posted = Assert.Single(await db.Events.ToListAsync());
        Assert.Equal("`test", posted.EventName);
        Assert.Equal("aaa", posted.EventLocation);
        Assert.Equal(Repop, posted.StartTime);
    }
}
