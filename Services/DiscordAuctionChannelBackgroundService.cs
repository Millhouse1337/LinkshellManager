namespace LinkshellManagerDiscordApp.Services;

// Drains DiscordAuctionChannelQueue and runs each job through the publisher
// (create the auction's channel, or post results + delete it on close). No
// debounce: auction create/close are discrete, low-frequency user actions,
// not bursts. A Close job blocks the loop for the configured post-results
// delay (default 30s) before deleting — acceptable given the volume.
public sealed class DiscordAuctionChannelBackgroundService : BackgroundService
{
    private readonly DiscordAuctionChannelQueue _queue;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<DiscordAuctionChannelBackgroundService> _logger;

    public DiscordAuctionChannelBackgroundService(
        DiscordAuctionChannelQueue queue,
        IServiceScopeFactory scopeFactory,
        ILogger<DiscordAuctionChannelBackgroundService> logger)
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
                var job = await _queue.Reader.ReadAsync(stoppingToken);

                using var scope = _scopeFactory.CreateScope();
                await scope.ServiceProvider
                    .GetRequiredService<DiscordAuctionChannelPublisher>()
                    .HandleAsync(job, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unhandled error in DiscordAuctionChannelBackgroundService loop.");
                try { await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken); }
                catch (OperationCanceledException) { }
            }
        }
    }
}
