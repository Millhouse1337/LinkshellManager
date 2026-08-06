using LinkshellManagerDiscordApp.Data;
using LinkshellManagerDiscordApp.Models;
using LinkshellManagerDiscordApp.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace LinkshellManager.Tests;

// The rule deciding whether a camp runs Manual Check In. It is applied at event creation, at
// recurring-board reactivation, and when an officer flips the linkshell's mode — they must agree,
// which is why they all call this one method.
public class HnmAttendanceModeResolutionTests
{
    private const string Wd = HnmAttendanceModes.Wd;
    private const string Standard = HnmAttendanceModes.Standard;

    [Theory]
    [InlineData("Tiamat")]        // long-window wyrm
    [InlineData("Fafnir")]        // short-window dragon
    [InlineData("King Behemoth")] // short-window king
    [InlineData("Goblin Furrier")] // testing monster
    public void WdLinkshell_CuratedHnm_RunsManualCheckIn(string monster)
    {
        Assert.Equal(Wd, HnmEventSeeder.ResolveMode(Wd, monster));
    }

    [Theory]
    [InlineData("Byakko")]        // sky god — not a windowed HNM camp
    [InlineData("Charybdis")]     // sea NM
    [InlineData("Some Custom Mob")]
    [InlineData(null)]
    public void WdLinkshell_UncuratedMonster_StaysStandard(string? monster)
    {
        // Null = Standard everywhere (Models/Event.cs). A check-in board whose windows mean
        // nothing would be worse than no board.
        Assert.Null(HnmEventSeeder.ResolveMode(Wd, monster));
    }

    [Theory]
    [InlineData(Standard)]
    [InlineData(null)]
    [InlineData("nonsense")] // unknown values fail closed to Standard
    public void NonWdLinkshell_NeverRunsManualCheckIn(string? linkshellMode)
    {
        Assert.Null(HnmEventSeeder.ResolveMode(linkshellMode, "Tiamat"));
    }
}

// A finalized Manual Check In camp is RECYCLED, not deleted — the recurring poller revives the same
// Event row for the next pop. Every Manual Check In surface gates on WdFinalizedAt being null, so the whole block
// has to be cleared or the re-posted board is permanently dead.
public class ClearWdCampStateTests
{
    private static Event FinalizedCamp() => new()
    {
        EventType = "HNM",
        AttendanceMode = HnmAttendanceModes.Wd,
        WdFinalizedAt = new DateTime(2026, 7, 30, 12, 0, 0, DateTimeKind.Utc),
        WdAwaitingProcessingSince = new DateTime(2026, 7, 30, 11, 0, 0, DateTimeKind.Utc),
        WdPopWindow = 6,
        WdClaimed = true,
        WdKilled = true,
    };

    [Fact]
    public void ClearsEverySentinel()
    {
        var ev = FinalizedCamp();

        HnmEventSeeder.ClearWdCampState(ev);

        Assert.Null(ev.WdFinalizedAt);
        Assert.Null(ev.WdPopWindow);
        Assert.False(ev.WdClaimed);
        Assert.False(ev.WdKilled);
    }

    [Fact]
    public void ClearsAwaitingProcessing_SoTheFreshBoardIsNotInstantlyReFinalized()
    {
        // The dangerous half-reset: WdProcessingBackgroundService selects on
        // (AttendanceMode == Wd && WdAwaitingProcessingSince != null && WdFinalizedAt == null).
        // Leaving this set would match the revived board against the PREVIOUS pop's elapsed grace
        // and finalize it on the next 60s tick, crediting nobody.
        var ev = FinalizedCamp();

        HnmEventSeeder.ClearWdCampState(ev);

        Assert.Null(ev.WdAwaitingProcessingSince);
        Assert.False(ev.WdAwaitingProcessingSince is not null && ev.WdFinalizedAt is null);
    }
}

// Flipping the linkshell's mode re-stamps camps that haven't started. Running camps keep their mode
// until they pop: the two finalizers read disjoint presence data and neither falls back, so a
// mid-camp switch would silently pay everyone 0 for the night.
public class ReStampNotStartedCampsTests
{
    private const int LinkshellId = 7;

    private static ApplicationDbContext NewInMemoryContext() =>
        new(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static Event Camp(string monster, string? mode = null, DateTime? commenced = null) => new()
    {
        LinkshellId = LinkshellId,
        EventType = "HNM",
        EventName = monster,
        AssignedMonsterName = monster,
        AttendanceMode = mode,
        CommencementStartTime = commenced,
    };

    [Fact]
    public async Task StandardToWd_StampsQueuedCuratedCamps()
    {
        using var db = NewInMemoryContext();
        var queued = Camp("Fafnir");
        db.Events.Add(queued);
        await db.SaveChangesAsync();

        var (restamped, skipped) = await HnmEventSeeder.ReStampNotStartedCampsAsync(
            db, LinkshellId, HnmAttendanceModes.Wd);

        Assert.Equal(1, restamped);
        Assert.Equal(0, skipped);
        Assert.Equal(HnmAttendanceModes.Wd, queued.AttendanceMode);
    }

    [Fact]
    public async Task RunningCamp_IsSkipped_SoItsAttendanceIsNotStranded()
    {
        using var db = NewInMemoryContext();
        var running = Camp("Fafnir", commenced: new DateTime(2026, 7, 30, 10, 0, 0, DateTimeKind.Utc));
        db.Events.Add(running);
        await db.SaveChangesAsync();

        var (restamped, skipped) = await HnmEventSeeder.ReStampNotStartedCampsAsync(
            db, LinkshellId, HnmAttendanceModes.Wd);

        Assert.Equal(0, restamped);
        Assert.Equal(1, skipped);
        Assert.Null(running.AttendanceMode); // still Standard — it keeps the mode it started with
    }

    [Fact]
    public async Task WdToStandard_ClearsQueuedCamps()
    {
        using var db = NewInMemoryContext();
        var queued = Camp("Tiamat", mode: HnmAttendanceModes.Wd);
        db.Events.Add(queued);
        await db.SaveChangesAsync();

        var (restamped, _) = await HnmEventSeeder.ReStampNotStartedCampsAsync(
            db, LinkshellId, HnmAttendanceModes.Standard);

        Assert.Equal(1, restamped);
        Assert.Null(queued.AttendanceMode);
    }

    [Fact]
    public async Task UncuratedMonster_IsLeftStandardEvenInWdMode()
    {
        using var db = NewInMemoryContext();
        var queued = Camp("Byakko");
        db.Events.Add(queued);
        await db.SaveChangesAsync();

        var (restamped, skipped) = await HnmEventSeeder.ReStampNotStartedCampsAsync(
            db, LinkshellId, HnmAttendanceModes.Wd);

        Assert.Equal(0, restamped); // already correct — no change, so nothing counted
        Assert.Equal(0, skipped);
        Assert.Null(queued.AttendanceMode);
    }

    [Fact]
    public async Task PoppedAndFinalizedCamps_AreNeverTouched()
    {
        using var db = NewInMemoryContext();
        var awaiting = Camp("Fafnir", mode: HnmAttendanceModes.Wd,
            commenced: new DateTime(2026, 7, 30, 10, 0, 0, DateTimeKind.Utc));
        awaiting.WdAwaitingProcessingSince = new DateTime(2026, 7, 30, 11, 0, 0, DateTimeKind.Utc);
        var defeated = Camp("Nidhogg", mode: HnmAttendanceModes.Wd);
        defeated.HnmDefeatedAt = new DateTime(2026, 7, 30, 11, 0, 0, DateTimeKind.Utc);
        db.Events.AddRange(awaiting, defeated);
        await db.SaveChangesAsync();

        var (restamped, skipped) = await HnmEventSeeder.ReStampNotStartedCampsAsync(
            db, LinkshellId, HnmAttendanceModes.Standard);

        Assert.Equal(0, restamped);
        Assert.Equal(0, skipped); // filtered out by the query, not counted as "running"
        Assert.Equal(HnmAttendanceModes.Wd, awaiting.AttendanceMode);
        Assert.Equal(HnmAttendanceModes.Wd, defeated.AttendanceMode);
    }

    [Fact]
    public async Task OtherLinkshellsAreUnaffected()
    {
        using var db = NewInMemoryContext();
        var mine = Camp("Fafnir");
        var theirs = Camp("Fafnir");
        theirs.LinkshellId = LinkshellId + 1;
        db.Events.AddRange(mine, theirs);
        await db.SaveChangesAsync();

        await HnmEventSeeder.ReStampNotStartedCampsAsync(db, LinkshellId, HnmAttendanceModes.Wd);

        Assert.Equal(HnmAttendanceModes.Wd, mine.AttendanceMode);
        Assert.Null(theirs.AttendanceMode);
    }
}

// Recycling an Event row for the next pop. Every field the previous cycle wrote has to be walked
// back together, or the fresh board inherits state that quietly disables a feature — the window
// counter and its clear high-water mark most of all, because they are read as a PAIR.
public class ReviveForNewPopTests
{
    private const int LinkshellId = 7;
    private static readonly DateTime Repop = new(2026, 8, 6, 10, 27, 56, DateTimeKind.Utc);

    private static ApplicationDbContext NewInMemoryContext() =>
        new(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static Event LastCyclesTiamat() => new()
    {
        LinkshellId = LinkshellId,
        EventType = "HNM",
        EventName = "Tiamat",
        AssignedMonsterName = "Tiamat",
        HnmWindowNumber = 9,
        HnmClearedWindow = 9,
        HnmDefeatedAt = new DateTime(2026, 8, 5, 22, 0, 0, DateTimeKind.Utc),
        CommencementStartTime = new DateTime(2026, 8, 5, 13, 0, 0, DateTimeKind.Utc),
        WindowAnchorAt = new DateTime(2026, 8, 5, 13, 0, 0, DateTimeKind.Utc),
        NextWindowAt = new DateTime(2026, 8, 5, 22, 0, 0, DateTimeKind.Utc),
    };

    // HnmClearedWindow is a high-water mark ("settled up to here"), so leaving the previous cycle's
    // value on a counter reset to 1 reads as "windows 1-9 are already cleared". The advancer's
    // `(HnmClearedWindow ?? 1) < HnmWindowNumber` guard then skips the roster wipe for the whole
    // first nine hours of the new camp — the board marches its window number forward over a roster
    // that never empties.
    [Fact]
    public async Task ClearedWindowIsResetWithTheCounter_SoTheNewCycleStillWipesItsRoster()
    {
        using var db = NewInMemoryContext();
        var ev = LastCyclesTiamat();
        db.Events.Add(ev);
        await db.SaveChangesAsync();

        await HnmEventSeeder.ReviveForNewPopAsync(db, ev, Repop, nextDay: 2, nextMonster: "Tiamat", sourceTodId: null);

        Assert.Equal(1, ev.HnmWindowNumber);
        Assert.Null(ev.HnmClearedWindow);
        // The advancer's guard, restated: window 2 must be clearable on the new cycle.
        Assert.True((ev.HnmClearedWindow ?? 1) < 2);
    }

    [Fact]
    public async Task WindowTimingIsReAnchored_SoTheBoardDoesNotCarryThePreviousPopsCountdown()
    {
        using var db = NewInMemoryContext();
        var ev = LastCyclesTiamat();
        db.Events.Add(ev);
        await db.SaveChangesAsync();

        await HnmEventSeeder.ReviveForNewPopAsync(db, ev, Repop, nextDay: 2, nextMonster: "Tiamat", sourceTodId: null);

        Assert.Equal(Repop, ev.StartTime);
        Assert.Null(ev.HnmDefeatedAt);
        Assert.Null(ev.CommencementStartTime);
        Assert.Null(ev.WindowAnchorAt);
        Assert.Null(ev.NextWindowAt);
    }
}
