namespace LinkshellManagerDiscordApp.Services;

// Drains DiscordWebhookQueue and posts each snapshot to its linkshell's
// Discord channel. Each job is independent (one snapshot -> one message), so
// unlike SheetSyncBackgroundService there is no debounce/coalesce: just a
// bounded retry-with-backoff so a transient Discord/network blip recovers
// without ever blocking or failing the originating addon request.
public sealed class DiscordWebhookBackgroundService : BackgroundService
{
    private const int MaxAttempts = 4;

    private readonly DiscordWebhookQueue _queue;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<DiscordWebhookBackgroundService> _logger;

    public DiscordWebhookBackgroundService(
        DiscordWebhookQueue queue,
        IServiceScopeFactory scopeFactory,
        ILogger<DiscordWebhookBackgroundService> logger)
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
                var snapshotId = await _queue.Reader.ReadAsync(stoppingToken);
                await TryPublishAsync(snapshotId, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unhandled error in DiscordWebhookBackgroundService loop.");
                try { await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken); }
                catch (OperationCanceledException) { }
            }
        }
    }

    private async Task TryPublishAsync(int snapshotId, CancellationToken cancellationToken)
    {
        var attempt = 0;
        var delay = TimeSpan.FromSeconds(2);

        while (attempt < MaxAttempts)
        {
            attempt++;
            try
            {
                using var scope = _scopeFactory.CreateScope();
                await scope.ServiceProvider
                    .GetRequiredService<DiscordSnapshotPublisher>()
                    .PublishAsync(snapshotId, cancellationToken);
                return;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Discord snapshot post attempt {Attempt}/{Max} failed for snapshot {SnapshotId}.",
                    attempt, MaxAttempts, snapshotId);
                if (attempt >= MaxAttempts)
                {
                    break;
                }
                try { await Task.Delay(delay, cancellationToken); }
                catch (OperationCanceledException) { return; }
                delay = TimeSpan.FromSeconds(Math.Min(30, delay.TotalSeconds * 2));
            }
        }

        _logger.LogError(
            "Giving up posting snapshot {SnapshotId} to Discord after {Max} attempts.",
            snapshotId, MaxAttempts);
    }
}
