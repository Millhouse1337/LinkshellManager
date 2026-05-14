using LinkshellManagerDiscordApp.Data;
using LinkshellManagerDiscordApp.Options;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace LinkshellManagerDiscordApp.Services;

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
        var pending = new HashSet<int>();
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
                    // Debounce window elapsed - flush pending IDs.
                }

                foreach (var linkshellId in pending)
                {
                    if (stoppingToken.IsCancellationRequested) break;
                    await TrySyncAsync(linkshellId, stoppingToken);
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

    private async Task TrySyncAsync(int linkshellId, CancellationToken cancellationToken)
    {
        var attempt = 0;
        var maxAttempts = 4;
        var delay = TimeSpan.FromSeconds(2);

        while (attempt < maxAttempts)
        {
            attempt++;
            try
            {
                await SyncAsync(linkshellId, cancellationToken);
                return;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (GoogleOAuthRevokedException ex)
            {
                _logger.LogWarning(ex, "Refresh token rejected for linkshell {LinkshellId}; marking disconnected.", linkshellId);
                using var scope = _scopeFactory.CreateScope();
                var oauth = scope.ServiceProvider.GetRequiredService<GoogleOAuthService>();
                try { await oauth.MarkDisconnectedAsync(linkshellId, cancellationToken); } catch (Exception clearEx) { _logger.LogWarning(clearEx, "Failed to clear OAuth state for linkshell {LinkshellId}.", linkshellId); }
                return;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Sheets sync attempt {Attempt}/{Max} failed for linkshell {LinkshellId}.", attempt, maxAttempts, linkshellId);
                if (attempt >= maxAttempts) break;
                try { await Task.Delay(delay, cancellationToken); } catch (OperationCanceledException) { return; }
                delay = TimeSpan.FromSeconds(Math.Min(60, delay.TotalSeconds * 2));
            }
        }

        _logger.LogError("Sheets sync giving up after {Max} attempts for linkshell {LinkshellId}.", maxAttempts, linkshellId);
    }

    private async Task SyncAsync(int linkshellId, CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var sheets = scope.ServiceProvider.GetRequiredService<GoogleSheetsSyncService>();

        if (!sheets.IsConfigured)
        {
            _logger.LogDebug("Skipping sync for linkshell {LinkshellId}: Google Sheets not configured.", linkshellId);
            return;
        }

        var linkshell = await db.Linkshells
            .AsNoTracking()
            .FirstOrDefaultAsync(l => l.Id == linkshellId, cancellationToken);

        if (linkshell is null)
        {
            _logger.LogDebug("Skipping sync for linkshell {LinkshellId}: not found.", linkshellId);
            return;
        }

        if (string.IsNullOrWhiteSpace(linkshell.GoogleSpreadsheetId))
        {
            _logger.LogDebug("Skipping sync for linkshell {LinkshellId}: no spreadsheet configured.", linkshellId);
            return;
        }

        if (string.IsNullOrWhiteSpace(linkshell.GoogleOAuthRefreshTokenEnc))
        {
            _logger.LogDebug("Skipping sync for linkshell {LinkshellId}: no Google account connected.", linkshellId);
            return;
        }

        if (!linkshell.SheetSyncEnabled)
        {
            _logger.LogDebug("Skipping sync for linkshell {LinkshellId}: sync disabled in settings.", linkshellId);
            return;
        }

        var members = await db.AppUserLinkshells
            .AsNoTracking()
            .Where(m => m.LinkshellId == linkshellId && m.CharacterName != null)
            .Select(m => new { m.CharacterName, m.LinkshellDkp })
            .ToListAsync(cancellationToken);

        if (members.Count == 0)
        {
            _logger.LogDebug("Skipping sync for linkshell {LinkshellId}: no members to push.", linkshellId);
            return;
        }

        var tab = string.IsNullOrWhiteSpace(linkshell.GoogleSheetTabName) ? _options.DefaultTabName : linkshell.GoogleSheetTabName;

        // Read the sheet's column B to discover which row each member sits on.
        // Per-row update only: we never clear, add, or remove rows. The leader
        // owns the sheet's roster shape; we only touch column C values for
        // names we can find.
        var nameColumnRange = $"{tab}!B1:B500";
        var nameColumn = await sheets.ReadAsync(linkshellId, linkshell.GoogleSpreadsheetId, nameColumnRange, unformatted: false, cancellationToken)
                        ?? new List<IList<object>>();

        var rowByName = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < nameColumn.Count; i++)
        {
            var cellRow = nameColumn[i];
            if (cellRow == null || cellRow.Count == 0) continue;
            var name = cellRow[0]?.ToString()?.Trim();
            if (string.IsNullOrEmpty(name)) continue;
            if (string.Equals(name, "TOTAL", StringComparison.OrdinalIgnoreCase)) continue;
            // i is 0-indexed within the range starting at row 1, so the sheet row number is i + 1.
            if (!rowByName.ContainsKey(name)) rowByName[name] = i + 1;
        }

        var updates = 0;
        var skipped = 0;
        foreach (var member in members)
        {
            var name = member.CharacterName?.Trim();
            if (string.IsNullOrEmpty(name)) continue;
            if (!rowByName.TryGetValue(name, out var rowNumber))
            {
                skipped++;
                continue;
            }
            var cellRange = $"{tab}!C{rowNumber}";
            var dkpValue = member.LinkshellDkp ?? 0d;
            await sheets.WriteAsync(linkshellId, linkshell.GoogleSpreadsheetId, cellRange,
                new List<IList<object>> { new List<object> { dkpValue } }, cancellationToken);
            updates++;
        }

        _logger.LogInformation("Per-row sync for linkshell {LinkshellId}: updated {Updates} cells, skipped {Skipped} members not on sheet.", linkshellId, updates, skipped);
    }
}
