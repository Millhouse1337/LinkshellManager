using System;
using System.Threading;
using System.Threading.Tasks;
using LinkshellManagerDiscordApp.Data;
using LinkshellManagerDiscordApp.Models;
using LinkshellManagerDiscordApp.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace LinkshellManager.Tests;

// Changing Starts on the edit event form moves the ToD on the ToD page with it, once the officer has
// confirmed the popup. These pin which ToD that is, and what moving it does to a parked board.
public class HnmEventStartMovesTodTests
{
    private const int LinkshellId = 12;
    private const string Monster = "Adamantoise/Aspidochelone";

    private static readonly DateTime Now = new(2026, 11, 6, 12, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime OldPop = Now.AddDays(-2);
    private static readonly DateTime NextPop = Now.AddHours(10);

    private static ApplicationDbContext NewInMemoryContext() =>
        new(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static Tod TodFor(int id, DateTime repop, string monster = Monster, int linkshellId = LinkshellId) => new()
    {
        Id = id,
        LinkshellId = linkshellId,
        MonsterName = monster,
        DayNumber = 3,
        Time = repop.AddHours(-22),
        RepopTime = repop,
    };

    private static Event BoardFor(DateTime start, int? sourceTodId) => new()
    {
        Id = 400,
        LinkshellId = LinkshellId,
        EventName = "Turtle",
        EventType = "HNM",
        AssignedMonsterName = Monster,
        DayNumber = 3,
        StartTime = start,
        SourceTodId = sourceTodId,
    };

    private static Event ParkedBoardFor(DateTime start, int? sourceTodId)
    {
        var board = BoardFor(start, sourceTodId);
        board.HnmDefeatedAt = Now.AddHours(-12);
        return board;
    }

    private static async Task<ApplicationDbContext> SeededAsync(Event board, params Tod[] tods)
    {
        var db = NewInMemoryContext();
        db.Linkshells.Add(new Linkshell { Id = LinkshellId, LinkshellName = "Test" });
        db.Tods.AddRange(tods);
        db.Events.Add(board);
        await db.SaveChangesAsync();
        return db;
    }

    private static Task<Tod?> LinkedAsync(ApplicationDbContext db, Event board) =>
        HnmRecurringBoardService.FindLinkedTodAsync(db, board, CancellationToken.None);

    // ---- which ToD ----

    [Fact]
    public async Task AParkedBoard_MovesTheNewestTod_NotTheOneItStillPointsAt()
    {
        var board = ParkedBoardFor(NextPop, sourceTodId: 7);
        using var db = await SeededAsync(board, TodFor(7, OldPop), TodFor(8, NextPop));

        Assert.Equal(8, (await LinkedAsync(db, board))?.Id);
    }

    [Fact]
    public async Task ABoardThatIsUp_MovesTheTodItWasPostedFor()
    {
        var board = BoardFor(NextPop, sourceTodId: 7);
        using var db = await SeededAsync(board, TodFor(7, NextPop), TodFor(8, NextPop.AddDays(1)));

        Assert.Equal(7, (await LinkedAsync(db, board))?.Id);
    }

    // The addon logs the kill while the camp is still live, so the newest ToD is the NEXT pop's.
    [Fact]
    public async Task ALiveCamp_MovesTheTodItWasPostedFor_NotItsOwnKill()
    {
        var board = BoardFor(OldPop, sourceTodId: 7);
        board.CommencementStartTime = OldPop;
        using var db = await SeededAsync(board, TodFor(7, OldPop), TodFor(8, NextPop));

        Assert.Equal(7, (await LinkedAsync(db, board))?.Id);
    }

    [Fact]
    public async Task AHandMadeBoardOnThePredictedPop_MovesThatTod()
    {
        var board = BoardFor(NextPop, sourceTodId: null);
        using var db = await SeededAsync(board, TodFor(8, NextPop));

        Assert.Equal(8, (await LinkedAsync(db, board))?.Id);
    }

    [Fact]
    public async Task AHandMadeBoardAtItsOwnTime_MovesNothing()
    {
        var board = BoardFor(NextPop.AddHours(3), sourceTodId: null);
        using var db = await SeededAsync(board, TodFor(8, NextPop));

        Assert.Null(await LinkedAsync(db, board));
    }

    [Fact]
    public async Task AnotherMonstersOrLinkshellsTod_IsNeverMoved()
    {
        var board = ParkedBoardFor(NextPop, sourceTodId: 7);
        using var db = await SeededAsync(
            board,
            TodFor(7, NextPop, monster: "Behemoth"),
            TodFor(8, NextPop, linkshellId: LinkshellId + 1));

        Assert.Null(await LinkedAsync(db, board));
    }

    [Fact]
    public async Task EitherHalfOfAMergePair_IsTheSameSpawn()
    {
        var board = ParkedBoardFor(NextPop, sourceTodId: null);
        using var db = await SeededAsync(board, TodFor(8, NextPop, monster: "Adamantoise"));

        Assert.Equal(8, (await LinkedAsync(db, board))?.Id);
    }

    // ---- what moving it does ----

    [Fact]
    public void Moving_ShiftsTheTimeOfDeathByTheSameAmount()
    {
        var tod = TodFor(8, NextPop);
        var killedAt = tod.Time!.Value;

        HnmRecurringBoardService.MoveTodToRepop(tod, NextPop.AddMinutes(30), Now);

        Assert.Equal(NextPop.AddMinutes(30), tod.RepopTime);
        Assert.Equal(killedAt.AddMinutes(30), tod.Time);
        Assert.Equal(Now, tod.TimeStamp);
    }

    [Fact]
    public void Moving_ToTheSameTime_LeavesTheTodUntouched()
    {
        var tod = TodFor(8, NextPop);

        HnmRecurringBoardService.MoveTodToRepop(tod, NextPop, Now);

        Assert.Null(tod.TimeStamp);
    }

    // What the Update button runs for a confirmed move, in order.
    private static async Task<bool> UpdateMovingTodAsync(ApplicationDbContext db, Event board, Tod tod, DateTime newStart)
    {
        board.StartTime = newStart;
        HnmRecurringBoardService.MoveTodToRepop(tod, newStart, Now);
        await db.SaveChangesAsync();
        await HnmRecurringBoardService.SyncParkedBoardsForTodAsync(db, LinkshellId, tod.MonsterName, CancellationToken.None, Now);
        await HnmRecurringBoardService.RefreshRepostAtAsync(db, board, board.AssignedMonsterName, CancellationToken.None);
        return await HnmRecurringBoardService.ReviveDueParkedBoardAfterEditAsync(
            db, board, Now, newStart, editedDayNumber: null, CancellationToken.None);
    }

    private static async Task<(ApplicationDbContext Db, Event Board, Tod Tod, HnmRecurringBoard Standing)> ParkedOnNextPopAsync()
    {
        var board = ParkedBoardFor(NextPop, sourceTodId: 8);
        var tod = TodFor(8, NextPop);
        var db = await SeededAsync(board, tod);
        var standing = new HnmRecurringBoard
        {
            LinkshellId = LinkshellId, MonsterName = Monster, Enabled = true, LeadHours = 4, LastSourceTodId = 8,
        };
        db.HnmRecurringBoards.Add(standing);
        await db.SaveChangesAsync();
        return (db, board, tod, standing);
    }

    [Fact]
    public async Task MovingLater_KeepsTheBoardParked_AndTheRepostTimeFollows()
    {
        var (db, board, tod, standing) = await ParkedOnNextPopAsync();
        using var _ = db;
        var newStart = NextPop.AddHours(2);

        var revived = await UpdateMovingTodAsync(db, board, tod, newStart);

        Assert.False(revived);
        Assert.Equal(newStart, tod.RepopTime);
        Assert.Equal(newStart, board.StartTime);
        Assert.Equal(newStart.AddHours(-4), board.HnmRepostAt);
        // Re-opened, like an edit on the ToD page, so the poller brings the board back at that time.
        Assert.Null(standing.LastSourceTodId);
    }

    [Fact]
    public async Task MovingEarlier_PastTheRepostTime_OpensTheBoard()
    {
        var (db, board, tod, _) = await ParkedOnNextPopAsync();
        using var __ = db;
        var newStart = Now.AddHours(1);

        var revived = await UpdateMovingTodAsync(db, board, tod, newStart);

        Assert.True(revived);
        Assert.Equal(newStart, tod.RepopTime);
        Assert.Equal(newStart, board.StartTime);
        Assert.Null(board.HnmDefeatedAt);
    }
}
