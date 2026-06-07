namespace LinkshellManagerDiscordApp.Services;

// Drains DiscordEventChannelQueue and runs each event id through the publisher
// (announce the event to its linkshell's Discord channel). Event creation is a
// discrete, low-frequency user action, so no debouncing is needed. Mirrors
// DiscordAuctionChannelBackgroundService.
public sealed class DiscordEventChannelBackgroundService : BackgroundService
{
    private readonly DiscordEventChannelQueue _queue;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<DiscordEventChannelBackgroundService> _logger;

    public DiscordEventChannelBackgroundService(
        DiscordEventChannelQueue queue,
        IServiceScopeFactory scopeFactory,
        ILogger<DiscordEventChannelBackgroundService> logger)
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
                var eventId = await _queue.Reader.ReadAsync(stoppingToken);

                using var scope = _scopeFactory.CreateScope();
                await scope.ServiceProvider
                    .GetRequiredService<DiscordEventChannelPublisher>()
                    .HandleAsync(eventId, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unhandled error in DiscordEventChannelBackgroundService loop.");
                try { await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken); }
                catch (OperationCanceledException) { }
            }
        }
    }
}
