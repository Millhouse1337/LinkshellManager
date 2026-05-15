using LinkshellManagerDiscordApp.Options;
using Microsoft.Extensions.Options;

namespace LinkshellManagerDiscordApp.Services;

// Drains the SheetSyncQueue and dispatches AttInput append jobs to the
// AttInputAppendService. Preserves the debounce + retry-with-backoff +
// invalid-grant -> mark-disconnected scaffolding the old per-row sync had,
// but the body of work is now "append rows to AttInput" not "overwrite Main!C".
public sealed class SheetSyncBackgroundService : BackgroundService
{
    private readonly SheetSyncQueue _queue;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly GoogleSheetsOptions _options;
    private readonly ILogger<SheetSyncBackgroundService> _logger;

    public SheetSyncBackgroundService(
        SheetSyncQueue queue,
        IServiceScopeFactory scopeFactory,
        IOptions<GoogleSheetsOptions> options,
        ILogger<SheetSyncBackgroundService> logger)
    {
        _queue = queue;
        _scopeFactory = scopeFactory;
        _options = options.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Coalesce jobs that arrive within the debounce window so a busy
        // event-end (many windows + close in quick succession) collapses
        // into a single drain pass. Jobs are deduplicated by (kind, id) so
        // double-enqueues don't translate to duplicate API calls.
        var pending = new HashSet<SheetSyncJob>();
        var debounce = TimeSpan.FromSeconds(Math.Max(1, _options.DebounceSeconds));

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var first = await _queue.Reader.ReadAsync(stoppingToken);
                pending.Add(first);

                using var coalesceCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                coalesceCts.CancelAfter(debounce);
                try
                {
                    while (await _queue.Reader.WaitToReadAsync(coalesceCts.Token))
                    {
                        while (_queue.Reader.TryRead(out var next))
                        {
                            pending.Add(next);
                        }
                    }
                }
                catch (OperationCanceledException) when (coalesceCts.IsCancellationRequested && !stoppingToken.IsCancellationRequested)
                {
                    // Debounce window elapsed - flush pending jobs.
                }

                foreach (var job in pending)
                {
                    if (stoppingToken.IsCancellationRequested) break;
                    await TryRunJobAsync(job, stoppingToken);
                }
                pending.Clear();
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unhandled error in SheetSyncBackgroundService loop.");
                try { await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken); } catch (OperationCanceledException) { }
            }
        }
    }

    private async Task TryRunJobAsync(SheetSyncJob job, CancellationToken cancellationToken)
    {
        var attempt = 0;
        const int maxAttempts = 4;
        var delay = TimeSpan.FromSeconds(2);

        while (attempt < maxAttempts)
        {
            attempt++;
            try
            {
                await RunJobAsync(job, cancellationToken);
                return;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (GoogleOAuthRevokedException ex)
            {
                _logger.LogWarning(ex, "Refresh token rejected during {Kind} job {Id}; marking disconnected.", job.Kind, job.TargetId);
                using var scope = _scopeFactory.CreateScope();
                var oauth = scope.ServiceProvider.GetRequiredService<GoogleOAuthService>();
                try { await oauth.MarkDisconnectedAsync(ex.LinkshellId, cancellationToken); }
                catch (Exception clearEx) { _logger.LogWarning(clearEx, "Failed to clear OAuth state for linkshell {LinkshellId}.", ex.LinkshellId); }
                return;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "AttInput append attempt {Attempt}/{Max} failed for {Kind} job {Id}.", attempt, maxAttempts, job.Kind, job.TargetId);
                if (attempt >= maxAttempts) break;
                try { await Task.Delay(delay, cancellationToken); } catch (OperationCanceledException) { return; }
                delay = TimeSpan.FromSeconds(Math.Min(60, delay.TotalSeconds * 2));
            }
        }

        _logger.LogError("AttInput append giving up after {Max} attempts for {Kind} job {Id}.", maxAttempts, job.Kind, job.TargetId);
    }

    private async Task RunJobAsync(SheetSyncJob job, CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();

        switch (job.Kind)
        {
            case SheetSyncJobKind.Snapshot:
                await scope.ServiceProvider.GetRequiredService<AttInputAppendService>()
                    .AppendSnapshotAsync(job.TargetId, cancellationToken);
                break;
            case SheetSyncJobKind.EventWindow:
                await scope.ServiceProvider.GetRequiredService<AttInputAppendService>()
                    .AppendEventWindowAsync(job.TargetId, cancellationToken);
                break;
            case SheetSyncJobKind.EventClose:
                await scope.ServiceProvider.GetRequiredService<AttInputAppendService>()
                    .AppendEventCloseAsync(job.TargetId, cancellationToken);
                break;
            case SheetSyncJobKind.DkpAudit:
                await scope.ServiceProvider.GetRequiredService<ManualPointsAppendService>()
                    .AppendAuditAsync(job.TargetId, cancellationToken);
                break;
            case SheetSyncJobKind.WindowEventPost:
                await scope.ServiceProvider.GetRequiredService<AttInputAppendService>()
                    .AppendWindowEventAsync(job.TargetId, cancellationToken);
                break;
        }
    }
}
