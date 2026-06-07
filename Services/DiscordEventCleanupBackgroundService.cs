namespace LinkshellManagerDiscordApp.Services;

// Drains DiscordEventCleanupQueue and deletes each event's signup-board message
// from Discord (so the board doesn't linger after the event is over). The
// "event ended" summary is posted separately by DiscordEventEndedPublisher.
// Mirrors DiscordEventEndedBackgroundService.
public sealed class DiscordEventCleanupBackgroundService : BackgroundService
{
    private readonly DiscordEventCleanupQueue _queue;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<DiscordEventCleanupBackgroundService> _logger;

    public DiscordEventCleanupBackgroundService(
        DiscordEventCleanupQueue queue,
        IServiceScopeFactory scopeFactory,
        ILogger<DiscordEventCleanupBackgroundService> logger)
    {
        _queue = queue;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var message = await _queue.Reader.ReadAsync(stoppingToken);

                // DiscordBotClient is scoped, so resolve it in a scope per job.
                using var scope = _scopeFactory.CreateScope();
                await scope.ServiceProvider
                    .GetRequiredService<DiscordBotClient>()
                    .DeleteMessageAsync(message.ChannelId, message.MessageId, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unhandled error in DiscordEventCleanupBackgroundService loop.");
                try { await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken); }
                catch (OperationCanceledException) { }
            }
        }
    }
}
