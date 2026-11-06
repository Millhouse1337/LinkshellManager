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

// Saving the edit form (Update event) on a board that is still parked from its last camp.
//
// Saving used to re-draw the "defeated" note and nothing else, so a board whose re-post time had
// passed without the poller bringing it back stayed down however often an officer edited it. Once
// the advertised re-post time has come, Update now brings the sign-up board back itself.
public class HnmParkedBoardEditTests
{
    private const int LinkshellId = 12;
    private const string Monster = "Adamantoise/Aspidochelone";

    private static readonly DateTime Now = new(2026, 11, 4, 13, 39, 0, DateTimeKind.Utc);
    private static readonly DateTime NextRepop = Now.AddHours(1);

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
            DayNumber = 3,
            Time = Now.AddHours(-21),
            RepopTime = NextRepop,
        });
        db.HnmRecurringBoards.Add(new HnmRecurringBoard
        {
            Id = 1,
            LinkshellId = LinkshellId,
            MonsterName = Monster,
            Enabled = true,
            LeadHours = 2,
            CreatedAt = Now.AddDays(-7),
        });
        db.Events.Add(board);
        await db.SaveChangesAsync();
        return db;
    }

    // Parked after its day-3 kill, and its re-post time (repop - 2h) is already an hour gone.
    private static Event DueParkedBoard() => new()
    {
        Id = 400,
        LinkshellId = LinkshellId,
        EventName = "Turtle",
        EventType = "HNM",
        AssignedMonsterName = Monster,
        DayNumber = 3,
        StartTime = NextRepop,
        SourceTodId = 7,
        HnmWindowNumber = 1,
        HnmDefeatedAt = Now.AddHours(-20),
        HnmRepostAt = NextRepop.AddHours(-2),
        WdFinalizedAt = Now.AddHours(-20),
    };

    [Fact]
    public async Task ADueParkedBoard_ComesBackOnUpdate_WithThePollersValues()
    {
        using var db = await SeededAsync(DueParkedBoard());
        var ev = await db.Events.SingleAsync();

        var revived = await HnmRecurringBoardService.ReviveDueParkedBoardAfterEditAsync(
            db, ev, Now, editedStartUtc: null, editedDayNumber: null, CancellationToken.None);

        Assert.True(revived);
        Assert.Null(ev.HnmDefeatedAt);
        Assert.Null(ev.HnmRepostAt);
        Assert.Null(ev.WdFinalizedAt);
        Assert.Equal(NextRepop, ev.StartTime);
        // The form's untouched "3" was the killed day; the next pop is day 4.
        Assert.Equal(4, ev.DayNumber);
        // Stamped, so the poller does not bring it back a second time and wipe the new sign-ups.
        Assert.Equal(7, (await db.HnmRecurringBoards.SingleAsync()).LastSourceTodId);
    }

    [Fact]
    public async Task FieldsTheOfficerChanged_WinOverThePollersValues()
    {
        using var db = await SeededAsync(DueParkedBoard());
        var ev = await db.Events.SingleAsync();
        var typedStart = NextRepop.AddMinutes(30);

        var revived = await HnmRecurringBoardService.ReviveDueParkedBoardAfterEditAsync(
            db, ev, Now, editedStartUtc: typedStart, editedDayNumber: 5, CancellationToken.None);

        Assert.True(revived);
        Assert.Equal(typedStart, ev.StartTime);
        Assert.Equal(5, ev.DayNumber);
    }

    [Fact]
    public async Task ABoardStillWaitingOnItsRepost_StaysParked()
    {
        var waiting = DueParkedBoard();
        waiting.HnmRepostAt = Now.AddMinutes(30);
        using var db = await SeededAsync(waiting);
        var ev = await db.Events.SingleAsync();

        var revived = await HnmRecurringBoardService.ReviveDueParkedBoardAfterEditAsync(
            db, ev, Now, editedStartUtc: null, editedDayNumber: null, CancellationToken.None);

        Assert.False(revived);
        Assert.NotNull(ev.HnmDefeatedAt);
        Assert.Equal(3, ev.DayNumber);
        Assert.Null((await db.HnmRecurringBoards.SingleAsync()).LastSourceTodId);
    }

    [Fact]
    public async Task AParkedBoardWithNoRepostScheduled_StaysParked()
    {
        var noRepost = DueParkedBoard();
        noRepost.HnmRepostAt = null;
        using var db = await SeededAsync(noRepost);
        var ev = await db.Events.SingleAsync();

        var revived = await HnmRecurringBoardService.ReviveDueParkedBoardAfterEditAsync(
            db, ev, Now, editedStartUtc: null, editedDayNumber: null, CancellationToken.None);

        Assert.False(revived);
        Assert.NotNull(ev.HnmDefeatedAt);
    }

    // ---- the edit form's re-post lead ----

    [Fact]
    public async Task EditingTheLead_MovesTheBoardsRepostTime()
    {
        var waiting = DueParkedBoard();
        waiting.HnmRepostAt = NextRepop.AddHours(-2);
        using var db = await SeededAsync(waiting);
        var ev = await db.Events.SingleAsync();

        var changed = await HnmRecurringBoardService.ApplyEditedLeadAsync(db, ev, 6, CancellationToken.None);
        await db.SaveChangesAsync();
        await HnmRecurringBoardService.RefreshRepostAtAsync(db, ev, ev.AssignedMonsterName, CancellationToken.None);

        Assert.True(changed);
        Assert.Equal(6, (await db.HnmRecurringBoards.SingleAsync()).LeadHours);
        Assert.Equal(NextRepop.AddHours(-6), ev.HnmRepostAt);
    }

    // OnlyFrens, 2026-11-05: two enabled rows for one spawn. The form showed the combined row's
    // 2h while the save went to the "Adamantoise" row, so the lead "never changed".
    [Fact]
    public async Task EditingTheLead_UpdatesEveryEnabledBoardForTheSpawn()
    {
        using var db = await SeededAsync(DueParkedBoard());
        db.HnmRecurringBoards.Add(new HnmRecurringBoard
        {
            Id = 2, LinkshellId = LinkshellId, MonsterName = "Adamantoise", Enabled = true, LeadHours = 3,
        });
        await db.SaveChangesAsync();
        var ev = await db.Events.SingleAsync();

        var changed = await HnmRecurringBoardService.ApplyEditedLeadAsync(db, ev, 4, CancellationToken.None);
        await db.SaveChangesAsync();

        Assert.True(changed);
        Assert.All(await db.HnmRecurringBoards.ToListAsync(), board => Assert.Equal(4, board.LeadHours));
    }

    [Fact]
    public async Task EditingTheLead_NeverSwitchesRepostOn()
    {
        using var db = await SeededAsync(DueParkedBoard());
        var board = await db.HnmRecurringBoards.SingleAsync();
        board.Enabled = false;
        await db.SaveChangesAsync();
        var ev = await db.Events.SingleAsync();

        var changed = await HnmRecurringBoardService.ApplyEditedLeadAsync(db, ev, 6, CancellationToken.None);

        Assert.False(changed);
        Assert.False(board.Enabled);
        Assert.Equal(2, board.LeadHours);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0.0)]
    [InlineData(-1.0)]
    public async Task ABlankOrNonPositiveLead_LeavesTheLeadAlone(double? lead)
    {
        using var db = await SeededAsync(DueParkedBoard());
        var ev = await db.Events.SingleAsync();

        var changed = await HnmRecurringBoardService.ApplyEditedLeadAsync(db, ev, lead, CancellationToken.None);

        Assert.False(changed);
        Assert.Equal(2, (await db.HnmRecurringBoards.SingleAsync()).LeadHours);
    }

    [Fact]
    public async Task ABoardThatIsUp_IsLeftAlone()
    {
        var up = DueParkedBoard();
        up.HnmDefeatedAt = null;
        up.WdFinalizedAt = null;
        using var db = await SeededAsync(up);
        var ev = await db.Events.SingleAsync();

        var revived = await HnmRecurringBoardService.ReviveDueParkedBoardAfterEditAsync(
            db, ev, Now, editedStartUtc: null, editedDayNumber: null, CancellationToken.None);

        Assert.False(revived);
        Assert.Equal(3, ev.DayNumber);
    }

    // ---- Update never opens a board before the re-post time the officer set ----

    private static readonly DateTime FarPop = Now.AddHours(10);
    private const double FourHourLead = 4;

    // What the Update button runs once the form is saved.
    private static async Task<bool> UpdateAsync(ApplicationDbContext db, Event ev, DateTime nowUtc)
    {
        await HnmRecurringBoardService.RefreshRepostAtAsync(db, ev, ev.AssignedMonsterName, CancellationToken.None);
        return await HnmRecurringBoardService.ReviveDueParkedBoardAfterEditAsync(
            db, ev, nowUtc, editedStartUtc: null, editedDayNumber: null, CancellationToken.None);
    }

    // The board still points at ToD 7, whose pop is long gone, and the real next pop is on ToD 8.
    private static async Task<ApplicationDbContext> SeededWithNewerTodAsync(Event board)
    {
        var db = await SeededAsync(board);
        (await db.Tods.SingleAsync()).RepopTime = Now.AddDays(-2);
        db.Tods.Add(new Tod { Id = 8, LinkshellId = LinkshellId, MonsterName = Monster, DayNumber = 4, RepopTime = FarPop });
        (await db.HnmRecurringBoards.SingleAsync()).LeadHours = FourHourLead;
        await db.SaveChangesAsync();
        return db;
    }

    [Fact]
    public async Task Update_ANewerTodSetsTheRepostTime_NotTheOldTodTheBoardPointsAt()
    {
        var parked = DueParkedBoard();
        parked.StartTime = FarPop; // the ToD tracker already moved the board here
        using var db = await SeededWithNewerTodAsync(parked);
        var ev = await db.Events.SingleAsync();

        var revived = await UpdateAsync(db, ev, Now);

        Assert.False(revived);
        Assert.NotNull(ev.HnmDefeatedAt);
        Assert.Equal(FarPop.AddHours(-FourHourLead), ev.HnmRepostAt);
    }

    [Fact]
    public async Task Update_AStartLeftOnAnOldPop_DoesNotOpenABoardWhoseNewestTodIsAhead()
    {
        var parked = DueParkedBoard();
        parked.StartTime = Now.AddDays(-2);
        using var db = await SeededWithNewerTodAsync(parked);
        var ev = await db.Events.SingleAsync();

        var revived = await UpdateAsync(db, ev, Now);

        Assert.False(revived);
        Assert.NotNull(ev.HnmDefeatedAt);
        Assert.Equal(FarPop.AddHours(-FourHourLead), ev.HnmRepostAt);
    }

    // The form's own hint: Starts minus the lead.
    [Fact]
    public async Task Update_AStartTypedLaterThanTheTod_CountsFromTheTypedStart()
    {
        var parked = DueParkedBoard();
        parked.StartTime = FarPop;
        using var db = await SeededAsync(parked);
        (await db.Tods.SingleAsync()).RepopTime = Now.AddDays(-2);
        (await db.HnmRecurringBoards.SingleAsync()).LeadHours = FourHourLead;
        await db.SaveChangesAsync();
        var ev = await db.Events.SingleAsync();

        var revived = await UpdateAsync(db, ev, Now);

        Assert.False(revived);
        Assert.NotNull(ev.HnmDefeatedAt);
        Assert.Equal(FarPop.AddHours(-FourHourLead), ev.HnmRepostAt);
    }

    [Fact]
    public async Task Update_OnceTheRepostTimeHasCome_StillOpensTheBoard()
    {
        using var db = await SeededAsync(DueParkedBoard());
        var ev = await db.Events.SingleAsync();

        var revived = await UpdateAsync(db, ev, Now);

        Assert.True(revived);
        Assert.Null(ev.HnmDefeatedAt);
        Assert.Equal(NextRepop, ev.StartTime);
    }

    // ---- the poller keeps to that same time ----

    private static HnmRecurringBoardBackgroundService Poller() =>
        new(null!, NullLogger<HnmRecurringBoardBackgroundService>.Instance);

    // Moved by hand to FarPop while ToD 7 says NextRepop, and that ToD's own re-post time has passed.
    private static Event BoardMovedLaterByAnOfficer()
    {
        var moved = DueParkedBoard();
        moved.StartTime = FarPop;
        moved.HnmRepostAt = FarPop.AddHours(-FourHourLead);
        return moved;
    }

    [Fact]
    public async Task Poller_WaitsForTheTimeAnOfficerMovedTheBoardTo()
    {
        using var db = await SeededAsync(BoardMovedLaterByAnOfficer());
        var board = await db.HnmRecurringBoards.SingleAsync();

        await Poller().ProcessBoardAsync(db, board, Now, CancellationToken.None);

        var ev = await db.Events.SingleAsync();
        Assert.NotNull(ev.HnmDefeatedAt);
        Assert.Equal(FarPop, ev.StartTime);
        Assert.Null(board.LastSourceTodId);
    }

    [Fact]
    public async Task Poller_OpensAMovedBoardAtItsTime_AndKeepsTheStartTheOfficerSet()
    {
        using var db = await SeededAsync(BoardMovedLaterByAnOfficer());
        var board = await db.HnmRecurringBoards.SingleAsync();

        await Poller().ProcessBoardAsync(db, board, FarPop.AddHours(-FourHourLead), CancellationToken.None);

        var ev = await db.Events.SingleAsync();
        Assert.Null(ev.HnmDefeatedAt);
        Assert.Null(ev.HnmRepostAt);
        Assert.Equal(FarPop, ev.StartTime);
        Assert.Equal(4, ev.DayNumber);
        Assert.Equal(7, board.LastSourceTodId);
    }

    // The poller is done with this ToD, so only the officer's time can bring the board back.
    [Fact]
    public async Task Poller_OpensAMovedBoard_EvenWhenItsTodWasAlreadyHandled()
    {
        using var db = await SeededAsync(BoardMovedLaterByAnOfficer());
        var board = await db.HnmRecurringBoards.SingleAsync();
        board.LastSourceTodId = 7;
        await db.SaveChangesAsync();

        await Poller().ProcessBoardAsync(db, board, FarPop.AddHours(-FourHourLead), CancellationToken.None);

        var ev = await db.Events.SingleAsync();
        Assert.Null(ev.HnmDefeatedAt);
        Assert.Equal(FarPop, ev.StartTime);
    }

    [Fact]
    public async Task Poller_AnOrdinaryParkedBoard_StillComesBackOnItsTod()
    {
        using var db = await SeededAsync(DueParkedBoard());
        var board = await db.HnmRecurringBoards.SingleAsync();

        await Poller().ProcessBoardAsync(db, board, Now, CancellationToken.None);

        var ev = await db.Events.SingleAsync();
        Assert.Null(ev.HnmDefeatedAt);
        Assert.Equal(NextRepop, ev.StartTime);
        Assert.Equal(7, board.LastSourceTodId);
    }
}
