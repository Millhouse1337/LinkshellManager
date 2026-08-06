using LinkshellManagerDiscordApp.Data;
using Microsoft.EntityFrameworkCore;

namespace LinkshellManagerDiscordApp.Services;

// Periodically starts due events once their StartTime has passed, so nobody has to click "Start"
// at the scheduled time. Two cohorts: ordinary events opt in via Event.AutoStart; HNM camps ALWAYS
// auto-start at their scheduled window-1 pop (no flag) so the board goes live and
// HnmWindowAdvanceBackgroundService can march it through its windows — the addon's own start still
// works too (its CommencementStartTime ??= is then a no-op). Stamping CommencementStartTime + each
// participation's StartTime is exactly the transition the manual "Start" performs; StarterUserId is
// left null to mark it system-started. The DbContext save hook then notifies the live change feed,
// so connected clients see the event go live without a refresh.
public sealed class EventAutoStartBackgroundService : BackgroundService
{
    // How punctual auto-start is: the event starts within this window of its
    // StartTime. 30s keeps it on-time without busy-looping.
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(30);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<EventAutoStartBackgroundService> _logger;

    public EventAutoStartBackgroundService(
        IServiceScopeFactory scopeFactory,
        ILogger<EventAutoStartBackgroundService> logger)
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
                await StartDueEventsAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unhandled error in EventAutoStartBackgroundService loop.");
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

    private async Task StartDueEventsAsync(CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var nowUtc = DateTime.UtcNow;
        var dueEvents = await db.Events
            .Include(evt => evt.AppUserEvents)
            .Where(evt => evt.CommencementStartTime == null
                && evt.StartTime != null
                && evt.StartTime <= nowUtc
                && evt.EndTime == null
                && (
                    // Opt-in auto-start for ordinary events (unchanged): the AutoStart flag, never HNM.
                    (evt.AutoStart && (evt.EventType == null || evt.EventType.ToLower() != "hnm"))
                    // HNM camps always auto-start at their scheduled window-1 time — no opt-in flag —
                    // so the board goes live and the window advancer takes over. Skip once popped.
                    || (evt.EventType != null && evt.EventType.ToLower() == "hnm" && evt.HnmDefeatedAt == null)
                ))
            .ToListAsync(cancellationToken);

        if (dueEvents.Count == 0)
        {
            return;
        }

        foreach (var evt in dueEvents)
        {
            var isHnm = string.Equals((evt.EventType ?? string.Empty).Trim(), "HNM", StringComparison.OrdinalIgnoreCase);
            // An HNM camp "started" at its scheduled Window-1 time (StartTime) — so the board's Started
            // timestamp matches what was entered in the app, even for a back-dated camp ("started 4h
            // ago"), instead of reading the poll moment we happened to notice it. Ordinary auto-start
            // events commence at the poll moment (nowUtc) so their DKP duration reflects real elapsed
            // time. StartTime is guaranteed non-null here by the query's StartTime filter.
            evt.CommencementStartTime = isHnm ? evt.StartTime : nowUtc;
            // Anchor the HNM window cadence to the scheduled window-1 pop (StartTime), not the poll
            // time, so a late tick doesn't shift the whole window schedule. Harmless on non-HNM.
            evt.WindowAnchorAt ??= evt.StartTime;
            // Window advance is MANUAL now (officers press "Next Window"), so seed the FIRST countdown
            // here — otherwise the freshly-live board would show no "Next window N in …" line until
            // the first click. The next window opens one cadence after Started; officers step it from
            // there. Null when the monster has no timed cadence or it's already the only window.
            if (isHnm && evt.StartTime is { } startUtc)
            {
                var minutes = HnmConfig.WindowAdvanceMinutes(evt.AssignedMonsterName);
                var count = DiscordEventMessageBuilder.EffectiveWindowCount(evt);
                evt.NextWindowAt = minutes > 0 && evt.HnmWindowNumber < count
                    ? startUtc.AddMinutes(minutes)
                    : null;
            }
            // Bring party-slot signups (Discord post / Activity) into the live event as
            // pending attendees — exactly what the manual "Start" does. Without this the
            // signups never become real AppUserEvents, so the client falls back to
            // synthesizing them as verified phantoms that land in the Active Room instead
            // of the Lobby (and can't be moderated — they have no participation row).
            await EventPartySignupService.MaterializeSignupsAsParticipantsAsync(db, evt, cancellationToken);
            foreach (var participation in evt.AppUserEvents)
            {
                participation.StartTime ??= nowUtc;
            }

            _logger.LogInformation(
                "Auto-started event {EventId} (\"{EventName}\") for linkshell {LinkshellId} at its scheduled time.",
                evt.Id, evt.EventName, evt.LinkshellId);
        }

        await db.SaveChangesAsync(cancellationToken);
    }
}
