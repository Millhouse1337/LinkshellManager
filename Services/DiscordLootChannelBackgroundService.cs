namespace LinkshellManagerDiscordApp.Services;

// Drains DiscordLootChannelQueue and runs each awarded-loot job through the
// publisher (announce it to the linkshell's Loot channel). Mirrors
// DiscordAuctionChannelBackgroundService.
public sealed class DiscordLootChannelBackgroundService : BackgroundService
{
    private readonly DiscordLootChannelQueue _queue;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<DiscordLootChannelBackgroundService> _logger;

    public DiscordLootChannelBackgroundService(
        DiscordLootChannelQueue queue,
        IServiceScopeFactory scopeFactory,
        ILogger<DiscordLootChannelBackgroundService> logger)
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
                    .GetRequiredService<DiscordLootChannelPublisher>()
                    .HandleAsync(job, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unhandled error in DiscordLootChannelBackgroundService loop.");
                try { await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken); }
                catch (OperationCanceledException) { }
            }
        }
    }
}
