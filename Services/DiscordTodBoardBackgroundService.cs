namespace LinkshellManagerDiscordApp.Services;

// Drains DiscordTodBoardQueue and rebuilds each affected linkshell's live
// Discord ToD board. Debounces: a burst of ToD changes (e.g. an addon
// posting several pops, or an officer editing rows) coalesces into a single
// board edit per linkshell instead of one Discord PATCH per change. Mirrors
// SheetSyncBackgroundService's read-one-then-coalesce loop.
public sealed class DiscordTodBoardBackgroundService : BackgroundService
{
    private static readonly TimeSpan Debounce = TimeSpan.FromSeconds(4);

    private readonly DiscordTodBoardQueue _queue;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<DiscordTodBoardBackgroundService> _logger;

    public DiscordTodBoardBackgroundService(
        DiscordTodBoardQueue queue,
        IServiceScopeFactory scopeFactory,
        ILogger<DiscordTodBoardBackgroundService> logger)
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

                foreach (var linkshellId in pending)
                {
                    if (stoppingToken.IsCancellationRequested)
                    {
                        break;
                    }
                    try
                    {
                        using var scope = _scopeFactory.CreateScope();
                        await scope.ServiceProvider
                            .GetRequiredService<DiscordTodBoardPublisher>()
                            .PublishAsync(linkshellId, stoppingToken);
                    }
                    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                    {
                        return;
                    }
                    catch (Exception ex)
                    {
                        // Best-effort: the next ToD change re-enqueues, and the
                        // stored message id makes the retry an idempotent edit.
                        _logger.LogWarning(ex,
                            "Failed rebuilding ToD board for linkshell {LinkshellId}.", linkshellId);
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unhandled error in DiscordTodBoardBackgroundService loop.");
                try { await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken); }
                catch (OperationCanceledException) { }
            }
        }
    }
}
