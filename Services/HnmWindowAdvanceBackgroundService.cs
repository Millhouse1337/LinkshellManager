using LinkshellManagerDiscordApp.Data;
using Microsoft.EntityFrameworkCore;

namespace LinkshellManagerDiscordApp.Services;

// Advances a live HNM camp's spawn window on its monster's timed cadence, so the Discord board
// marches "Window N of M" forward on its own (Tiamat/Jormungand/Vrtra = 60 min/window; the
// kings/dragons = 10 min/window) until an officer pops/ends the camp. Applies to EVERY windowed
// HNM board — Standard and Manual Check In alike — not just Manual Check In camps, which is the difference
// from the old WdProcessingBackgroundService.AdvanceLiveCampsAsync it replaces.
//
// Moving the counter never touches the roster. The roster clear is a separate step a beat behind it,
// and applies to WYRM boards only (HnmConfig.WindowAdvanceWipesRoster) in Standard mode: once a
// window has been open for HnmConfig.WindowClearGrace — seconds, so in practice always this service
// rather than an officer — the clear is performed here. "Next Window" stays the officer's way to turn
// a window over EARLY. Either way it's stamped on Event.HnmClearedWindow, so a window is cleared
// exactly once and the manual button doesn't double-step a counter this service already moved.
//
// Modifying the tracked Event + SaveChanges lets the DbContext save-hook (CollectEditedEvents)
// edit the posted board message. Polls faster than EventAutoStartBackgroundService's 30s cadence
// because the roster wipe rides these same ticks: a board that flipped its window number but still
// shows the old roster is the artifact worth keeping short.
public sealed class HnmWindowAdvanceBackgroundService : BackgroundService
{
    // Both the window flip and the roster clear fire on the first tick at-or-after their moment, so
    // this is the jitter on each: a window flips within one tick of its boundary, and the wipe within
    // one tick of boundary + WindowClearGrace. 10s honours a seconds-scale grace without busy-looping.
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(10);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<HnmWindowAdvanceBackgroundService> _logger;

    public HnmWindowAdvanceBackgroundService(
        IServiceScopeFactory scopeFactory,
        ILogger<HnmWindowAdvanceBackgroundService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(PollInterval);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await AdvanceLiveCampsAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unhandled error in HnmWindowAdvanceBackgroundService loop.");
            }

            try
            {
                if (!await timer.WaitForNextTickAsync(stoppingToken))
                {
                    break;
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task AdvanceLiveCampsAsync(CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var now = DateTime.UtcNow;
        // Live, un-popped HNM boards. The Manual Check In-only sentinels (WdAwaitingProcessingSince /
        // WdFinalizedAt) are always null on a Standard board, so filtering on them is a no-op
        // there and correctly excludes a Manual Check In camp that already popped.
        var live = await db.Events
            .Where(e => e.EventType == "HNM"
                        && e.EndTime == null
                        && e.CommencementStartTime != null   // don't march a queued board
                        && e.HnmDefeatedAt == null
                        && e.WdAwaitingProcessingSince == null
                        && e.WdFinalizedAt == null
                        && e.StartTime != null)
            .ToListAsync(cancellationToken);
        if (live.Count == 0)
        {
            return;
        }

        var changed = false;
        foreach (var ev in live)
        {
            // Only advance a counter the board actually renders + an officer can correct.
            if (!DiscordEventMessageBuilder.UsesWindows(ev))
            {
                continue;
            }
            var effectiveCount = DiscordEventMessageBuilder.EffectiveWindowCount(ev);
            if (effectiveCount <= 1)
            {
                continue;
            }

            // Window 1 opened at the anchor (re-stamped to "now" on a manual step so an officer's
            // Prev/Next isn't reverted here); fall back to StartTime for boards created before the
            // anchor existed. Window N opens at anchor + (N-1) × minutes.
            var anchor = ev.WindowAnchorAt ?? ev.StartTime!.Value;
            if (now < anchor)
            {
                continue; // window 1 hasn't opened yet
            }
            var minutes = HnmConfig.WindowAdvanceMinutes(ev.AssignedMonsterName);
            if (minutes <= 0)
            {
                continue; // this monster has no built-in cadence — it advances manually
            }

            var expected = ScheduledWindow(anchor, now, minutes, effectiveCount);
            if (expected > ev.HnmWindowNumber)
            {
                ev.HnmWindowNumber = expected;
                changed = true;
                _logger.LogInformation(
                    "HNM camp auto-advanced: event {EventId} → window {Window} of {Count}.",
                    ev.Id, expected, effectiveCount);
            }

            // Keep the next-window countdown fresh: window N+1 opens at anchor + N×minutes (null on
            // the final window). Recomputed each tick so a re-anchor after a manual step is picked up.
            DateTime? nextAt = ev.HnmWindowNumber >= effectiveCount
                ? null
                : anchor.AddMinutes(ev.HnmWindowNumber * (double)minutes);
            if (ev.NextWindowAt != nextAt)
            {
                ev.NextWindowAt = nextAt;
                changed = true;
            }

            // Roster clear, wyrm boards only. WindowClearGrace is zero, so this runs on the SAME tick
            // that moved the counter above: the window number and the empty roster reach the board
            // together, in one edit, and nobody sees a new window still wearing the old signups.
            //
            // Window 1 is never cleared — it's the roster the camp opened with, and clearing at the
            // camp's own start would wipe everyone who just signed up. `HnmClearedWindow`
            // reads null as "window 1 handled", so a fresh board falls straight through.
            if (ev.HnmWindowNumber > 1
                && (ev.HnmClearedWindow ?? 1) < ev.HnmWindowNumber
                && DiscordEventMessageBuilder.ClearsRosterOnWindowAdvance(ev))
            {
                var windowOpenedAt = anchor.AddMinutes((ev.HnmWindowNumber - 1) * (double)minutes);
                if (now >= windowOpenedAt + HnmConfig.WindowClearGrace)
                {
                    // Only clear while we're still INSIDE the window the clear belongs to. A board
                    // that sat un-advanced — this service was off, or the app was down — jumps
                    // several windows on its next tick, and clearing then would wipe a live roster
                    // over a boundary nobody was watching. Stamp it settled either way so a stale
                    // boundary can't fire the clear later.
                    if (now <= windowOpenedAt.AddMinutes(minutes))
                    {
                        // The roster on the board belongs to the window that just ENDED, one below
                        // the counter we moved above — that's the number "View Previous Window"
                        // will ask for. Zero grace is what makes this exact: the clear rides the
                        // same tick as the advance, so nobody can have signed up under the new
                        // window yet.
                        await EventPartySignupService.ClearWindowRosterAsync(
                            db, ev.Id, ev.HnmWindowNumber - 1, cancellationToken);
                        _logger.LogInformation(
                            "HNM camp roster auto-cleared: event {EventId} window {Window} ({Grace} after the window opened).",
                            ev.Id, ev.HnmWindowNumber, HnmConfig.WindowClearGrace);
                    }
                    else
                    {
                        _logger.LogInformation(
                            "HNM camp window {Window} on event {EventId} settled without clearing — its boundary is already {Age:0} min past.",
                            ev.HnmWindowNumber, ev.Id, (now - windowOpenedAt).TotalMinutes);
                    }
                    ev.HnmClearedWindow = ev.HnmWindowNumber;
                    changed = true;
                }
            }
        }
        if (changed)
        {
            await db.SaveChangesAsync(cancellationToken);
        }
    }

    // The window a camp should be on at `now`. Delegates to HnmConfig.WindowNumberAt, which is the
    // one implementation of "which window is this moment in" — the same mapping that labels
    // attendance snapshots, so a snapshot can never disagree with the board it was taken from.
    public static int ScheduledWindow(DateTime anchor, DateTime now, int minutes, int windowCount)
        => HnmConfig.WindowNumberAt(anchor, now, minutes, windowCount);
}
