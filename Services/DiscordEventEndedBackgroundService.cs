namespace LinkshellManagerDiscordApp.Services;

// Drains DiscordEventEndedQueue and runs each EventHistory id through the
// publisher (post the "event ended" summary to the linkshell's Discord channel).
// Mirrors DiscordEventChannelBackgroundService.
public sealed class DiscordEventEndedBackgroundService : BackgroundService
{
    private readonly DiscordEventEndedQueue _queue;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<DiscordEventEndedBackgroundService> _logger;

    public DiscordEventEndedBackgroundService(
        DiscordEventEndedQueue queue,
        IServiceScopeFactory scopeFactory,
        ILogger<DiscordEventEndedBackgroundService> logger)
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
                var eventHistoryId = await _queue.Reader.ReadAsync(stoppingToken);

                using var scope = _scopeFactory.CreateScope();
                await scope.ServiceProvider
                    .GetRequiredService<DiscordEventEndedPublisher>()
                    .HandleAsync(eventHistoryId, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unhandled error in DiscordEventEndedBackgroundService loop.");
                try { await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken); }
                catch (OperationCanceledException) { }
            }
        }
    }
}
