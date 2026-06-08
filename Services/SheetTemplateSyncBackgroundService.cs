using LinkshellManagerDiscordApp.Data;
using Microsoft.EntityFrameworkCore;

namespace LinkshellManagerDiscordApp.Services;

// Drains SheetTemplateSyncQueue and re-exports each affected linkshell's "LSM DKP"
// template tab (live sync, push-only). Debounces: a burst of DKP changes (e.g. a
// multi-attendee event close) coalesces into a single export per linkshell rather
// than one Sheets write per change. Only acts when the linkshell has live sync
// enabled AND a connected Google sheet; otherwise skips silently. Best-effort — a
// failed export never crashes the host, and the next DKP change re-enqueues.
// Mirrors DiscordTodBoardBackgroundService's read-one-then-coalesce loop.
public sealed class SheetTemplateSyncBackgroundService : BackgroundService
{
    private static readonly TimeSpan Debounce = TimeSpan.FromSeconds(5);

    private readonly SheetTemplateSyncQueue _queue;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<SheetTemplateSyncBackgroundService> _logger;

    public SheetTemplateSyncBackgroundService(
        SheetTemplateSyncQueue queue,
        IServiceScopeFactory scopeFactory,
        ILogger<SheetTemplateSyncBackgroundService> logger)
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
                        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                        var linkshell = await db.Linkshells
                            .AsNoTracking()
                            .FirstOrDefaultAsync(l => l.Id == linkshellId, stoppingToken);

                        // Live sync off, or no connected sheet → nothing to push.
                        if (linkshell is null
                            || !linkshell.SheetTemplateSyncEnabled
                            || string.IsNullOrWhiteSpace(linkshell.GoogleSpreadsheetId)
                            || string.IsNullOrWhiteSpace(linkshell.GoogleOAuthRefreshTokenEnc))
                        {
                            continue;
                        }

                        await scope.ServiceProvider
                            .GetRequiredService<DkpTemplateSheetService>()
                            .ExportAsync(linkshellId, stoppingToken);
                    }
                    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                    {
                        return;
                    }
                    catch (Exception ex)
                    {
                        // Best-effort: the next DKP change re-enqueues; the export
                        // overwrites the tab so a retry is idempotent.
                        _logger.LogWarning(ex,
                            "Live DKP template sync failed for linkshell {LinkshellId}.", linkshellId);
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unhandled error in SheetTemplateSyncBackgroundService loop.");
                try { await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken); }
                catch (OperationCanceledException) { }
            }
        }
    }
}
