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
            Id = 50, LinkshellId = LinkshellId, MonsterName = "Fafnir", DayNumber = 1,
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

    private static Event OfficersBoard(int id, string monster, DateTime startUtc) => new()
    {
        Id = id, LinkshellId = LinkshellId, EventName = "ada", EventType = "HNM",
        EventLocation = "Qufim Island", AssignedMonsterName = monster, StartTime = startUtc,
    };

    [Fact]
    public async Task ABoardAlreadyUpForThePop_IsReusedRatherThanDuplicated()
    {
        var (db, board) = await SeededAsync();
        db.Events.Add(OfficersBoard(100, "Fafnir", Repop));
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
        db.Events.Add(OfficersBoard(100, "Nidhogg", Repop.AddMinutes(3)));
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
        db.Events.Add(OfficersBoard(100, "Fafnir", Repop.AddHours(22)));
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
        db.Events.Add(OfficersBoard(100, "Behemoth", Repop));
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

    // ------------------------------------------------- a camp is still being fought ---

    private static Event LiveCamp(string monster, DateTime startUtc)
    {
        var camp = OfficersBoard(200, monster, startUtc);
        camp.CommencementStartTime = startUtc;   // live: an officer is standing at it
        return camp;
    }

    // THE SECOND BOARD IN THE REPORT. The addon settles the ToD while the camp is still live, so
    // nothing owns the new pop yet and the live camp's own start is the PREVIOUS repop -- outside
    // the ±10 minutes that would have matched it. The poller posted a second board beside the camp
    // being fought.
    [Fact]
    public async Task WhileACampForTheSpawnIsLive_NothingIsPosted()
    {
        var (db, board) = await SeededAsync();
        db.Events.Add(LiveCamp("Fafnir", Repop.AddHours(-22)));
        await db.SaveChangesAsync();

        await NewPoller().ProcessBoardAsync(db, board, Repop.AddMinutes(-5), CancellationToken.None);

        var live = Assert.Single(await db.Events.ToListAsync());
        Assert.Equal(200, live.Id);
        Assert.NotNull(live.CommencementStartTime);          // left alone, mid-camp
        // NOT marked handled: ending the camp parks it on this ToD, and a later tick finds it.
        Assert.Null(board.LastSourceTodId);
    }

    // A different monster's camp is somebody else's night.
    [Fact]
    public async Task WhileAnotherMonstersCampIsLive_ThisBoardIsStillPosted()
    {
        var (db, board) = await SeededAsync();
        db.Events.Add(LiveCamp("Behemoth", Repop.AddHours(-22)));
        await db.SaveChangesAsync();

        await NewPoller().ProcessBoardAsync(db, board, Repop.AddMinutes(-5), CancellationToken.None);

        Assert.Equal(2, await db.Events.CountAsync());
    }

    // --------------------------------------------- a leftover row from the last pop ---

    // Recycled onto the new pop rather than left beside a fresh one, or every kill adds another
    // entry to Queued Events.
    [Fact]
    public async Task APreviousPopsQueuedBoard_IsRecycledOntoTheNewPop()
    {
        var (db, board) = await SeededAsync();
        var lastPop = OfficersBoard(100, "Fafnir", Repop.AddHours(-22));
        lastPop.DayNumber = 1;
        lastPop.HnmWindowNumber = 5;
        db.Events.Add(lastPop);
        await db.SaveChangesAsync();

        await NewPoller().ProcessBoardAsync(db, board, Repop.AddMinutes(-5), CancellationToken.None);

        var recycled = Assert.Single(await db.Events.ToListAsync());
        Assert.Equal(100, recycled.Id);                      // the same row, moved on
        Assert.Equal(Repop, recycled.StartTime);
        Assert.Equal(50, recycled.SourceTodId);
        Assert.Equal(2, recycled.DayNumber);                 // the next pop is day 2
        Assert.Equal(1, recycled.HnmWindowNumber);           // a new cycle starts at window 1
        Assert.Null(recycled.CommencementStartTime);         // queued, not live
    }

    // Fafnir and Nidhogg are one spawn, so either half's leftover is this board's row.
    [Fact]
    public async Task ThePreviousPopsBoardIsRecycled_AcrossTheMergePair()
    {
        var (db, board) = await SeededAsync();
        db.Events.Add(OfficersBoard(100, "Nidhogg", Repop.AddHours(-22)));
        await db.SaveChangesAsync();

        await NewPoller().ProcessBoardAsync(db, board, Repop.AddMinutes(-5), CancellationToken.None);

        Assert.Equal(Repop, Assert.Single(await db.Events.ToListAsync()).StartTime);
    }

    // A camp that has ENDED is history; it is not a row to recycle.
    [Fact]
    public async Task AnEndedCamp_IsNotRecycled()
    {
        var (db, board) = await SeededAsync();
        var ended = OfficersBoard(100, "Fafnir", Repop.AddHours(-22));
        ended.EndTime = Repop.AddHours(-1);
        db.Events.Add(ended);
        await db.SaveChangesAsync();

        await NewPoller().ProcessBoardAsync(db, board, Repop.AddMinutes(-5), CancellationToken.None);

        var events = await db.Events.OrderBy(e => e.Id).ToListAsync();
        Assert.Equal(2, events.Count);
        Assert.Equal("`test", events[1].EventName);           // a fresh board, the ended one intact
    }
}
