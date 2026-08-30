using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LinkshellManagerDiscordApp.Data;
using LinkshellManagerDiscordApp.Models;
using LinkshellManagerDiscordApp.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace LinkshellManager.Tests;

// The re-post lead ("how many hours before the next repop the board re-posts") belongs to the End
// Camp / Post ToD form, where the officer knows the next pop. The EDIT event form can correct it
// without waiting for the next End Camp; the CREATE form deliberately never asks.
//
// Two forms writing one value is only safe because the edit box is OPTIONAL — an empty box posts
// null, meaning "keep the lead this board already has". If it ever posted 0 instead, every routine
// event edit would silently reset the lead to "re-post exactly at the pop" and the board would
// show up with no warning time.
//
// The other half of "the re-post must always work" is LastSourceTodId. The poller skips any ToD it
// has already stamped, so stamping on an edit would cancel the pending re-post of a board sitting
// in the "defeated / awaiting re-post" state — hence stampLatestTod: create yes, edit no.
public class HnmRecurringBoardLeadTests
{
    private const int LinkshellId = 3;
    private const int EventId = 42;
    private const string Monster = "Tiamat";

    private static readonly DateTime Repop = new(2026, 8, 20, 18, 0, 0, DateTimeKind.Utc);

    private static ApplicationDbContext NewInMemoryContext() =>
        new(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static Event BoardEvent() => new()
    {
        Id = EventId,
        LinkshellId = LinkshellId,
        EventName = "Tiamat",
        EventType = "HNM",
        AssignedMonsterName = Monster,
    };

    private static HnmRecurringBoard EnabledBoard(double leadHours) => new()
    {
        LinkshellId = LinkshellId,
        MonsterName = Monster,
        Enabled = true,
        LeadHours = leadHours,
    };

    // ===== UpsertAsync: what the edit event form writes =====

    [Fact]
    public async Task UpsertAsync_NullLead_KeepsTheLeadSetAtEndCamp()
    {
        using var db = NewInMemoryContext();
        db.Events.Add(BoardEvent());
        db.HnmRecurringBoards.Add(EnabledBoard(6));
        await db.SaveChangesAsync();
        var ev = await db.Events.SingleAsync();

        // The officer edited the event but left the lead box empty.
        await HnmRecurringBoardService.UpsertAsync(db, ev, null, "user-1", stampLatestTod: false, CancellationToken.None);

        var board = await db.HnmRecurringBoards.SingleAsync();
        // Untouched. An empty box is "no opinion", not "zero".
        Assert.Equal(6, board.LeadHours);
        Assert.True(board.Enabled);
    }

    [Fact]
    public async Task UpsertAsync_EnteredLead_OverwritesTheExistingLead()
    {
        using var db = NewInMemoryContext();
        db.Events.Add(BoardEvent());
        db.HnmRecurringBoards.Add(EnabledBoard(6));
        await db.SaveChangesAsync();
        var ev = await db.Events.SingleAsync();

        await HnmRecurringBoardService.UpsertAsync(db, ev, 2.5, "user-1", stampLatestTod: false, CancellationToken.None);

        var board = await db.HnmRecurringBoards.SingleAsync();
        // Fractional leads survive: 2.5 = 2h30m, the same precision the End Camp form accepts.
        Assert.Equal(2.5, board.LeadHours);
    }

    [Fact]
    public async Task UpsertAsync_ZeroLead_IsHonouredRatherThanTreatedAsUnset()
    {
        using var db = NewInMemoryContext();
        db.Events.Add(BoardEvent());
        db.HnmRecurringBoards.Add(EnabledBoard(6));
        await db.SaveChangesAsync();
        var ev = await db.Events.SingleAsync();

        // A typed 0 is a real choice ("re-post right at the pop"), distinct from an empty box.
        await HnmRecurringBoardService.UpsertAsync(db, ev, 0, "user-1", stampLatestTod: false, CancellationToken.None);

        var board = await db.HnmRecurringBoards.SingleAsync();
        Assert.Equal(0, board.LeadHours);
    }

    [Fact]
    public async Task UpsertAsync_OutOfRangeLead_IsClamped()
    {
        using var db = NewInMemoryContext();
        db.Events.Add(BoardEvent());
        await db.SaveChangesAsync();
        var ev = await db.Events.SingleAsync();

        // The form constrains 0..168, but the API is reachable directly, so the service clamps too.
        await HnmRecurringBoardService.UpsertAsync(db, ev, 500, "user-1", stampLatestTod: false, CancellationToken.None);

        var board = await db.HnmRecurringBoards.SingleAsync();
        Assert.Equal(168, board.LeadHours);
    }

    [Fact]
    public async Task UpsertAsync_NewBoardWithNoLead_FallsBackToTheDefault()
    {
        using var db = NewInMemoryContext();
        db.Events.Add(BoardEvent());
        await db.SaveChangesAsync();
        var ev = await db.Events.SingleAsync();

        // Nothing to preserve on a first-ever board, so "no opinion" resolves to the default.
        await HnmRecurringBoardService.UpsertAsync(db, ev, null, "user-1", stampLatestTod: false, CancellationToken.None);

        var board = await db.HnmRecurringBoards.SingleAsync();
        Assert.Equal(HnmRecurringBoardService.DefaultLeadHours, board.LeadHours);
    }

    // ===== stampLatestTod: whether a pending re-post survives =====

    [Fact]
    public async Task UpsertAsync_Editing_LeavesTheTodStampAloneSoThePendingRePostStillFires()
    {
        using var db = NewInMemoryContext();
        var tod = new Tod { LinkshellId = LinkshellId, MonsterName = Monster, RepopTime = Repop };
        db.Tods.Add(tod);
        db.Events.Add(BoardEvent());
        db.HnmRecurringBoards.Add(EnabledBoard(4));
        await db.SaveChangesAsync();
        var ev = await db.Events.SingleAsync();

        // An officer edits the camp while its board is parked awaiting the re-post for this ToD.
        await HnmRecurringBoardService.UpsertAsync(db, ev, 3, "user-1", stampLatestTod: false, CancellationToken.None);

        var board = await db.HnmRecurringBoards.SingleAsync();
        // The poller skips any ToD equal to LastSourceTodId. Stamping here would mark this pop
        // "already handled" and the board would never come back — the edit would silently cancel
        // the re-post. The new lead still lands.
        Assert.Null(board.LastSourceTodId);
        Assert.NotEqual(tod.Id, board.LastSourceTodId);
        Assert.Equal(3, board.LeadHours);
    }

    [Fact]
    public async Task UpsertAsync_Creating_StampsTheTodSoTheBoardIsNotRePostedForThisSamePop()
    {
        using var db = NewInMemoryContext();
        var tod = new Tod { LinkshellId = LinkshellId, MonsterName = Monster, RepopTime = Repop };
        db.Tods.Add(tod);
        db.Events.Add(BoardEvent());
        await db.SaveChangesAsync();
        var ev = await db.Events.SingleAsync();

        // Creating a camp for the pop this ToD predicts.
        await HnmRecurringBoardService.UpsertAsync(db, ev, null, "user-1", stampLatestTod: true, CancellationToken.None);

        var board = await db.HnmRecurringBoards.SingleAsync();
        // Without the stamp the poller would immediately post a SECOND board for the very pop
        // this event was just created for.
        Assert.Equal(tod.Id, board.LastSourceTodId);
    }

    // ===== RefreshRepostAtAsync: keeping the displayed re-post time honest =====

    [Fact]
    public async Task RefreshRepostAtAsync_DefeatedBoard_RecomputesFromTheNewLead()
    {
        using var db = NewInMemoryContext();
        var tod = new Tod { LinkshellId = LinkshellId, MonsterName = Monster, RepopTime = Repop };
        db.Tods.Add(tod);
        await db.SaveChangesAsync();

        var ev = BoardEvent();
        ev.HnmDefeatedAt = new DateTime(2026, 8, 20, 6, 0, 0, DateTimeKind.Utc);
        ev.SourceTodId = tod.Id;
        ev.StartTime = Repop;
        ev.HnmRepostAt = Repop.AddHours(-1); // what the old 1h lead scheduled
        db.Events.Add(ev);
        db.HnmRecurringBoards.Add(EnabledBoard(4)); // officer just changed it to 4h
        await db.SaveChangesAsync();

        await HnmRecurringBoardService.RefreshRepostAtAsync(db, ev, Monster, CancellationToken.None);

        var saved = await db.Events.SingleAsync();
        // The poller already recomputes its own window from LeadHours; this is what stops the card
        // from advertising the stale 1h-before time while the board actually re-posts 4h before.
        Assert.Equal(Repop.AddHours(-4), saved.HnmRepostAt);
    }

    [Fact]
    public async Task RefreshRepostAtAsync_RecurrenceTurnedOff_ClearsTheSchedule()
    {
        using var db = NewInMemoryContext();
        var tod = new Tod { LinkshellId = LinkshellId, MonsterName = Monster, RepopTime = Repop };
        db.Tods.Add(tod);
        await db.SaveChangesAsync();

        var ev = BoardEvent();
        ev.HnmDefeatedAt = new DateTime(2026, 8, 20, 6, 0, 0, DateTimeKind.Utc);
        ev.SourceTodId = tod.Id;
        ev.HnmRepostAt = Repop.AddHours(-1);
        db.Events.Add(ev);
        var board = EnabledBoard(4);
        board.Enabled = false; // the edit unchecked "Repeat post when ToD is updated"
        db.HnmRecurringBoards.Add(board);
        await db.SaveChangesAsync();

        await HnmRecurringBoardService.RefreshRepostAtAsync(db, ev, Monster, CancellationToken.None);

        var saved = await db.Events.SingleAsync();
        // No enabled board means no auto-re-post at all, so advertising a time would be a lie.
        Assert.Null(saved.HnmRepostAt);
    }

    // ===== SyncParkedBoardsForTodAsync: a corrected ToD has to reach the parked board =====

    // Builds the shape the ToD tracker leaves behind: a parked board whose displayed times came
    // from the ORIGINAL repop, and a ToD row that has since been edited to `newRepop`.
    private static async Task<ApplicationDbContext> ParkedBoardWithEditedTodAsync(
        DateTime newRepop, double leadHours, int? lastSourceTodId, DateTime? defeatedAt = null)
    {
        var db = NewInMemoryContext();
        var tod = new Tod { LinkshellId = LinkshellId, MonsterName = Monster, RepopTime = newRepop };
        db.Tods.Add(tod);
        await db.SaveChangesAsync();

        var ev = BoardEvent();
        ev.HnmDefeatedAt = defeatedAt ?? new DateTime(2026, 8, 20, 6, 0, 0, DateTimeKind.Utc);
        ev.SourceTodId = tod.Id;
        ev.StartTime = Repop;                    // the ORIGINAL predicted pop
        ev.HnmRepostAt = Repop.AddHours(-leadHours);
        db.Events.Add(ev);

        var board = EnabledBoard(leadHours);
        board.LastSourceTodId = lastSourceTodId == -1 ? tod.Id : lastSourceTodId;
        db.HnmRecurringBoards.Add(board);
        await db.SaveChangesAsync();
        return db;
    }

    [Fact]
    public async Task SyncParkedBoardsForTodAsync_MovedTod_ShiftsTheParkedBoardsPopAndRePostTime()
    {
        // Officer moves the ToD an hour later, so the pop and the re-post both slide an hour.
        var movedRepop = Repop.AddHours(1);
        using var db = await ParkedBoardWithEditedTodAsync(movedRepop, leadHours: 1, lastSourceTodId: null);

        await HnmRecurringBoardService.SyncParkedBoardsForTodAsync(db, LinkshellId, Monster, CancellationToken.None);

        var saved = await db.Events.SingleAsync();
        Assert.Equal(movedRepop, saved.StartTime);
        Assert.Equal(movedRepop.AddHours(-1), saved.HnmRepostAt);
    }

    [Fact]
    public async Task SyncParkedBoardsForTodAsync_TodCorrectedAfterThePollerGaveUp_ReopensTheCycle()
    {
        // The poller abandons a pop more than PostGrace old, stamping LastSourceTodId. Correcting
        // that same ToD used to be silently ignored, because the stamp still matched.
        var correctedRepop = DateTime.UtcNow.AddHours(4);
        using var db = await ParkedBoardWithEditedTodAsync(correctedRepop, leadHours: 1, lastSourceTodId: -1);

        await HnmRecurringBoardService.SyncParkedBoardsForTodAsync(db, LinkshellId, Monster, CancellationToken.None);

        var board = await db.HnmRecurringBoards.SingleAsync();
        // Cleared, so the poller stops skipping this ToD and can post the corrected pop.
        Assert.Null(board.LastSourceTodId);
    }

    [Fact]
    public async Task SyncParkedBoardsForTodAsync_StillHopelesslyStaleTod_LeavesTheCycleClosed()
    {
        // Edited, but the "corrected" pop is still well past PostGrace. Re-opening it would just
        // have the poller abandon it again on the next tick.
        var stillDeadRepop = DateTime.UtcNow.AddHours(-30);
        using var db = await ParkedBoardWithEditedTodAsync(stillDeadRepop, leadHours: 1, lastSourceTodId: -1);
        var todId = (await db.Tods.SingleAsync()).Id;

        await HnmRecurringBoardService.SyncParkedBoardsForTodAsync(db, LinkshellId, Monster, CancellationToken.None);

        var board = await db.HnmRecurringBoards.SingleAsync();
        Assert.Equal(todId, board.LastSourceTodId);
    }

    [Fact]
    public async Task SyncParkedBoardsForTodAsync_LiveCamp_IsLeftCompletelyAlone()
    {
        // A running camp's window grid is anchored to StartTime, so moving it mid-camp would
        // scramble the window counter and the attendance windows. Only PARKED boards shift.
        var movedRepop = Repop.AddHours(5);
        using var db = NewInMemoryContext();
        var tod = new Tod { LinkshellId = LinkshellId, MonsterName = Monster, RepopTime = movedRepop };
        db.Tods.Add(tod);
        await db.SaveChangesAsync();

        var ev = BoardEvent();
        ev.HnmDefeatedAt = null; // live, mid-camp
        ev.CommencementStartTime = Repop;
        ev.StartTime = Repop;
        ev.HnmWindowNumber = 3;
        db.Events.Add(ev);
        db.HnmRecurringBoards.Add(EnabledBoard(1));
        await db.SaveChangesAsync();

        await HnmRecurringBoardService.SyncParkedBoardsForTodAsync(db, LinkshellId, Monster, CancellationToken.None);

        var saved = await db.Events.SingleAsync();
        Assert.Equal(Repop, saved.StartTime);
        Assert.Equal(3, saved.HnmWindowNumber);
    }

    [Fact]
    public async Task SyncParkedBoardsForTodAsync_RecurrenceOff_ShiftsThePopButAdvertisesNoRePost()
    {
        var movedRepop = Repop.AddHours(2);
        using var db = await ParkedBoardWithEditedTodAsync(movedRepop, leadHours: 1, lastSourceTodId: null);
        var board = await db.HnmRecurringBoards.SingleAsync();
        board.Enabled = false;
        await db.SaveChangesAsync();

        await HnmRecurringBoardService.SyncParkedBoardsForTodAsync(db, LinkshellId, Monster, CancellationToken.None);

        var saved = await db.Events.SingleAsync();
        // The pop itself still moved — that's just the prediction. But with no enabled board there
        // is no auto-re-post, so promising one would be a lie.
        Assert.Equal(movedRepop, saved.StartTime);
        Assert.Null(saved.HnmRepostAt);
    }

    [Fact]
    public async Task RefreshRepostAtAsync_LiveBoard_LeavesTheScheduleAlone()
    {
        using var db = NewInMemoryContext();
        var ev = BoardEvent();
        ev.HnmDefeatedAt = null; // still accepting signups — nothing is waiting to re-post
        ev.HnmRepostAt = Repop.AddHours(-1);
        db.Events.Add(ev);
        db.HnmRecurringBoards.Add(EnabledBoard(4));
        await db.SaveChangesAsync();

        await HnmRecurringBoardService.RefreshRepostAtAsync(db, ev, Monster, CancellationToken.None);

        var saved = await db.Events.SingleAsync();
        Assert.Equal(Repop.AddHours(-1), saved.HnmRepostAt);
    }
}
