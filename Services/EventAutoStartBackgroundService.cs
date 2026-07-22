using LinkshellManagerDiscordApp.Data;
using Microsoft.EntityFrameworkCore;

namespace LinkshellManagerDiscordApp.Services;

// Periodically starts events flagged AutoStart once their StartTime has passed,
// so an officer doesn't have to click "Start" at the scheduled time. Opt-in per
// event (Event.AutoStart) and never touches HNM events (those are driven by the
// in-game addon). Stamping CommencementStartTime + each participation's StartTime
// is exactly the transition the manual "Start" performs; StarterUserId is left
// null to mark it system-started. The DbContext save hook then notifies the live
// change feed, so connected clients see the event go live without a refresh.
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
            .Where(evt => evt.AutoStart
                && evt.CommencementStartTime == null
                && evt.StartTime != null
                && evt.StartTime <= nowUtc
                && (evt.EventType == null || evt.EventType.ToLower() != "hnm"))
            .ToListAsync(cancellationToken);

        if (dueEvents.Count == 0)
        {
            return;
        }

        foreach (var evt in dueEvents)
        {
            evt.CommencementStartTime = nowUtc;
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
