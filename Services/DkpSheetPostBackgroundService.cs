namespace LinkshellManagerDiscordApp.Services;

// Drains DkpSheetPostQueue and refreshes each affected linkshell's live DKP sheet
// Discord post. Debounces: a burst of DKP changes (e.g. closing an event credits
// the whole roster) coalesces into a single edit per linkshell instead of one
// Discord PATCH per ledger row. Mirrors DiscordTodBoardBackgroundService.
public sealed class DkpSheetPostBackgroundService : BackgroundService
{
    private static readonly TimeSpan Debounce = TimeSpan.FromSeconds(5);

    private readonly DkpSheetPostQueue _queue;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<DkpSheetPostBackgroundService> _logger;

    public DkpSheetPostBackgroundService(
        DkpSheetPostQueue queue,
        IServiceScopeFactory scopeFactory,
        ILogger<DkpSheetPostBackgroundService> logger)
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
                            .GetRequiredService<DiscordDkpSheetPublisher>()
                            .PublishAsync(linkshellId, stoppingToken);
                    }
                    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                    {
                        return;
                    }
                    catch (Exception ex)
                    {
                        // Best-effort: the next DKP change re-enqueues, and the stored
                        // message id makes the retry an idempotent edit.
                        _logger.LogWarning(ex,
                            "Failed refreshing the DKP sheet post for linkshell {LinkshellId}.", linkshellId);
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unhandled error in DkpSheetPostBackgroundService loop.");
                try { await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken); }
                catch (OperationCanceledException) { }
            }
        }
    }
}
