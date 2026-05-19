namespace LinkshellManagerDiscordApp.Services;

// Drains DiscordDkpSpendQueue and posts the DKP-spend log. Debounces: a
// burst of spends (e.g. an auction close producing one ledger row per
// winner, all from a single SaveChanges) coalesces into one Discord embed
// instead of one POST per winner. The publisher groups the coalesced ids by
// linkshell internally. Mirrors DiscordTodBoardBackgroundService's
// read-one-then-coalesce loop.
public sealed class DiscordDkpSpendBackgroundService : BackgroundService
{
    private static readonly TimeSpan Debounce = TimeSpan.FromSeconds(4);

    private readonly DiscordDkpSpendQueue _queue;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<DiscordDkpSpendBackgroundService> _logger;

    public DiscordDkpSpendBackgroundService(
        DiscordDkpSpendQueue queue,
        IServiceScopeFactory scopeFactory,
        ILogger<DiscordDkpSpendBackgroundService> logger)
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
                var pending = new HashSet<int> { await _queue.Reader.ReadAsync(stoppingToken) };

                using var coalesceCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                coalesceCts.CancelAfter(Debounce);
                try
                {
                    while (await _queue.Reader.WaitToReadAsync(coalesceCts.Token))
                    {
                        while (_queue.Reader.TryRead(out var id))
                        {
                            pending.Add(id);
                        }
                    }
                }
                catch (OperationCanceledException)
                    when (coalesceCts.IsCancellationRequested && !stoppingToken.IsCancellationRequested)
                {
                    // Debounce window elapsed — flush the coalesced set.
                }

                if (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                try
                {
                    using var scope = _scopeFactory.CreateScope();
                    await scope.ServiceProvider
                        .GetRequiredService<DiscordDkpSpendPublisher>()
                        .PublishAsync(pending, stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    // Best-effort: a dropped post is acceptable (the spend is
                    // still recorded in the ledger / DKP history page).
                    _logger.LogWarning(ex,
                        "Failed posting coalesced DKP spend log for {Count} entries.", pending.Count);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unhandled error in DiscordDkpSpendBackgroundService loop.");
                try { await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken); }
                catch (OperationCanceledException) { }
            }
        }
    }
}
