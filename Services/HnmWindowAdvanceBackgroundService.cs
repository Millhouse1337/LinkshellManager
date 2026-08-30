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
// and applies to WYRM boards only (HnmConfig.WindowAdvanceWipesRoster) — in BOTH attendance modes,
// since a Tiamat camp re-forms every hour whether its attendance is read from scans or from
// self-serve check-ins. Once a window has been open for HnmConfig.WindowClearGrace the clear is
// performed here, and stamped on Event.HnmClearedWindow so a window is cleared exactly once.
// On a Manual Check In camp the clear takes the party grid only: the check-in ledger those camps are
// paid from rides through it (see EventPartySignupService.ClearWindowRosterAsync).
//
// The clear tracks the number the BOARD PRINTS (DiscordEventMessageBuilder.FocusWindow), not the
// raw counter, so the two can never come apart: every change of that number wipes the roster it
// was naming — including the change the camp makes by going LIVE, where window 1's single pop
// chance is spent the instant the camp forms and the board steps to window 2 with the counter
// still on 1.
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

    // internal, not private: HnmWindowClearLifecycleTests drives this directly, which is the only
    // way to assert that the window number the board prints and the roster underneath it move on
    // the same tick.
    internal async Task AdvanceLiveCampsAsync(CancellationToken cancellationToken)
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
            var minutes = DiscordEventMessageBuilder.EffectiveWindowMinutes(ev);
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

            // Roster clear, wyrm boards only (either attendance mode). WindowClearGrace is zero, so this runs on the SAME tick
            // that moved the counter above: the window number and the empty roster reach the board
            // together, in one edit, and nobody sees a new window still wearing the old signups.
            //
            // Gated on the number the board actually PRINTS (FocusWindow — the window being
            // awaited), not on the raw counter. They step together at every boundary but not at the
            // camp's own start: FocusWindow is counter + 1 only once a next window exists, and
            // that becomes true the moment CommencementStartTime/NextWindowAt are stamped. So a
            // board going live flips "Window 1 of 25" → "Window 2 of 25" on its own, and gating the
            // clear on the counter left that ONE change unpaired — the board named window 2 over
            // window 1's signups for a full cadence, which is exactly the "it advanced but never
            // cleared" report. Window 1 is a knife edge like every other: its pop chance is spent
            // the instant the camp goes live, so its roster is settled there too.
            //
            // `HnmClearedWindow` is therefore on the SAME scale — the highest printed window whose
            // predecessor's roster has been settled. Null reads as 1: a board that has not gone
            // live yet is still collecting window 1's signups and must never be wiped.
            var focusWindow = DiscordEventMessageBuilder.FocusWindow(ev);
            if (focusWindow > 1
                && (ev.HnmClearedWindow ?? 1) < focusWindow
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
                        // The roster on the board belongs to the window the board was NAMING until
                        // this tick — one below the number it prints from here on, and the number
                        // "View Previous Window" will ask for. Zero grace is what makes this exact:
                        // the clear rides the same tick as the advance, so nobody can have signed
                        // up under the new window yet.
                        await EventPartySignupService.ClearWindowRosterAsync(
                            db, ev.Id, focusWindow - 1, cancellationToken);
                        _logger.LogInformation(
                            "HNM camp roster auto-cleared: event {EventId} window {Window} ({Grace} after the window opened).",
                            ev.Id, focusWindow, HnmConfig.WindowClearGrace);
                    }
                    else
                    {
                        _logger.LogInformation(
                            "HNM camp window {Window} on event {EventId} settled without clearing — its boundary is already {Age:0} min past.",
                            focusWindow, ev.Id, (now - windowOpenedAt).TotalMinutes);
                    }
                    ev.HnmClearedWindow = focusWindow;
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
