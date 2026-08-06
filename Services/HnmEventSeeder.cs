using LinkshellManagerDiscordApp.Data;
using LinkshellManagerDiscordApp.Models;
using Microsoft.EntityFrameworkCore;

namespace LinkshellManagerDiscordApp.Services;

// Seeds a freshly-created HNM event: its window count and — when the linkshell runs Manual Check In mode and
// the monster is a tracked HNM — its AttendanceMode = "Wd" stamp.
//
// Window counts and auto-advance cadence are built in per monster (HnmConfig.EffectiveWindowCount /
// WindowAdvanceMinutes) and are NOT configurable per linkshell: the wyrms run 25 windows at 60 min,
// the kings/dragons 7 windows at 10 min, everything else advances manually.
//
// Static + ApplicationDbContext-arg by design, matching the existing
// EventPartySignupService.MaterializeSignupsAsParticipantsAsync(dbContext, ...) convention, so
// it can be called from controllers, services, and the background poller without DI plumbing.
public static class HnmEventSeeder
{
    // THE rule for "does this camp run Manual Check In". One home, because it is applied in three
    // places that must never disagree: event creation (below), recurring-board reactivation
    // (HnmRecurringBoardBackgroundService), and the re-stamp when an officer flips the linkshell's
    // mode (ReStampNotStartedCampsAsync). Returns the value for Event.AttendanceMode — "Wd", or
    // null which every consumer reads as Standard (see Models/Event.cs).
    //
    // Gated on IsTrueHnm: only the curated wyrms / kings / dragons (and the testing monsters) run
    // the windowed check-in flow, so a Manual Check In linkshell camping something else still gets a Standard
    // board rather than a check-in board whose windows mean nothing.
    public static string? ResolveMode(string? linkshellMode, string? monster)
        => HnmAttendanceModes.Normalize(linkshellMode) == HnmAttendanceModes.Wd
           && HnmConfig.IsTrueHnm(monster)
            ? HnmAttendanceModes.Wd
            : null;

    // Clears every Manual Check In sentinel so a RECYCLED board reads as a fresh camp.
    //
    // WdCampFinalizer pays a camp out by stamping both WdFinalizedAt and HnmDefeatedAt and then
    // recycling the Event row; HnmRecurringBoardBackgroundService revives that same row for the next
    // pop. Clearing only HnmDefeatedAt left WdFinalizedAt set forever, and every Manual Check In surface gates on
    // it being null — the camp vanished from /api/addon/wd-camps, the Discord Check In row stopped
    // rendering, FocusWindow froze the counter, the advance poller skipped it, and End Camp refused.
    //
    // All five move TOGETHER, which is the reason this is one named method rather than five lines at
    // the call site: WdProcessingBackgroundService selects on (AttendanceMode == Wd &&
    // WdAwaitingProcessingSince != null && WdFinalizedAt == null), so clearing WdFinalizedAt while
    // leaving WdAwaitingProcessingSince set would match the fresh board against the PREVIOUS pop's
    // long-elapsed grace and re-finalize it on the next tick — crediting nobody and re-locking it.
    public static void ClearWdCampState(Event ev)
    {
        ev.WdFinalizedAt = null;
        ev.WdAwaitingProcessingSince = null;
        ev.WdPopWindow = null;
        ev.WdClaimed = false;
        ev.WdKilled = false;
    }

    // Recycles an EXISTING event row for the next pop instead of leaving it behind and inserting a
    // second one: new repop, window back to 1, signups cleared, day advanced.
    //
    // Two callers, and they must not diverge — HnmRecurringBoardBackgroundService reviving a board
    // parked as "defeated", and HnmAutoEventService reviving the queued event when a fresh ToD is
    // posted from the addon. Every line below is here because leaving it out broke something:
    //
    //   * CommencementStartTime / WindowAnchorAt / NextWindowAt — without these the recycled board
    //     reads as "Started" and carries the PREVIOUS pop's window countdown.
    //   * HnmClearedWindow — it MUST move with HnmWindowNumber. It is the "settled up to here" high
    //     water mark, so a row recycled after reaching window 9 last cycle reads as "windows 1-9
    //     already cleared" against a counter back at 1, and HnmWindowAdvanceBackgroundService
    //     silently skips the roster wipe for the new cycle's windows 2 through 9.
    //   * ClearWdCampState — without it the board reads as "already processed" forever and Manual
    //     Check In silently disappears from both Discord and the addon.
    //   * AttendanceMode — re-derived because between camps is the one safe moment to pick up a
    //     linkshell mode flip that happened while the board was parked. Follows nextMonster, since
    //     that is what this cycle actually camps.
    //
    // Stages the changes only; the caller saves. The DbContext save-hook re-posts to Discord by
    // EDITING the existing message, because the event keeps its message id — which is the whole
    // point of reviving rather than creating.
    public static async Task ReviveForNewPopAsync(
        ApplicationDbContext db,
        Event ev,
        DateTime repopUtc,
        int? nextDay,
        string? nextMonster,
        int? sourceTodId,
        CancellationToken ct = default)
    {
        ev.HnmDefeatedAt = null;
        ev.HnmRepostAt = null;
        ev.StartTime = repopUtc;
        ev.HnmWindowNumber = 1; // new pop cycle → back to Window 1
        ev.HnmClearedWindow = null; // …and nothing in the new cycle is settled yet (null reads as "window 1 handled")
        ev.DayNumber = nextDay;
        ev.AssignedMonsterName = nextMonster;
        ev.CommencementStartTime = null;
        ev.WindowAnchorAt = null;
        ev.NextWindowAt = null;
        HnmEventSeeder.ClearWdCampState(ev);

        // Stamping the source ToD is what makes the two revive callers coexist: the recurring
        // board's idempotency check matches on SourceTodId, so once this row is linked to the ToD
        // the background poller finds it instead of posting a second board for the same pop.
        if (sourceTodId.HasValue)
        {
            ev.SourceTodId = sourceTodId.Value;
        }

        ev.AttendanceMode = ResolveMode(
            await db.Linkshells
                .Where(l => l.Id == ev.LinkshellId)
                .Select(l => l.HnmAttendanceMode)
                .FirstOrDefaultAsync(ct),
            nextMonster);
    }

    public static async Task SeedHnmEventAsync(
        ApplicationDbContext db, Event ev, Linkshell? linkshell, string? monster, CancellationToken ct = default)
    {
        ev.WindowCountOverride = HnmConfig.EffectiveWindowCount(monster);

        var mode = linkshell is not null
            ? linkshell.HnmAttendanceMode
            : await db.Linkshells
                .Where(l => l.Id == ev.LinkshellId)
                .Select(l => l.HnmAttendanceMode)
                .FirstOrDefaultAsync(ct);

        ev.AttendanceMode = ResolveMode(mode, monster);
    }

    // Re-stamps AttendanceMode on the linkshell's HNM camps that have NOT STARTED, so flipping the
    // mode in Customize takes effect on boards that are already posted and waiting instead of only
    // on ones created afterwards. Stages the changes only — the caller saves (which also lets the
    // DbContext save-hook re-render the affected Discord boards in the same transaction).
    //
    // Deliberately excludes running / popped / finalized camps. The two finalizers read DISJOINT
    // presence data — Standard from AppUserEventWindow scans, Wd from AppUserEvent.WdArrivalWindow —
    // and neither falls back, so switching a camp mid-flight silently pays everyone 0 for the night.
    // A live camp keeps the mode it started with until it pops.
    //
    // Returns (restamped, skippedRunning) so the caller can tell an officer what actually moved.
    public static async Task<(int Restamped, int SkippedRunning)> ReStampNotStartedCampsAsync(
        ApplicationDbContext db, int linkshellId, string? linkshellMode, CancellationToken ct = default)
    {
        var camps = await db.Events
            .Where(e => e.LinkshellId == linkshellId
                        && e.EventType == "HNM"
                        && e.EndTime == null
                        && e.HnmDefeatedAt == null
                        && e.WdAwaitingProcessingSince == null
                        && e.WdFinalizedAt == null)
            .ToListAsync(ct);

        var restamped = 0;
        var skippedRunning = 0;
        foreach (var camp in camps)
        {
            // Commenced = the camp is live and may already hold attendance for its current mode.
            if (camp.CommencementStartTime is not null)
            {
                skippedRunning++;
                continue;
            }

            var mode = ResolveMode(linkshellMode, camp.AssignedMonsterName);
            if (string.Equals(camp.AttendanceMode, mode, StringComparison.Ordinal)) continue;

            camp.AttendanceMode = mode;
            restamped++;
        }

        return (restamped, skippedRunning);
    }
}
