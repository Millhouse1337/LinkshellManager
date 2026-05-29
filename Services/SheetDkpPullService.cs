using LinkshellManagerDiscordApp.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace LinkshellManagerDiscordApp.Services;

// "Pull on page load (cached)" sheet -> app DKP refresh.
//
// For a linkshell whose Google Sheet is connected, the sheet is the source of
// truth for DKP. The app's stored LinkshellDkp can drift behind it -- e.g.
// when a point is added on the sheet, or posted from an event that only
// appends rows to the sheet without crediting the app balance. When a DKP page
// loads we re-read the sheet's Main!B:C and SET each matched member's balance
// to the sheet value, throttled per-linkshell so we don't call the Sheets API
// on every request.
//
// IMPORTANT: callers must run any app-side crediting (e.g.
// WindowEventDkpLedgerService.EnsurePostedWindowEventLedgerEntriesForLinkshellAsync)
// BEFORE calling EnsureFreshAsync. Because the pull SETS to the sheet value,
// running it last means the sheet wins and an app-side increment that is
// already reflected on the sheet produces no change here -- so it never
// double-counts and never writes a spurious ledger row for app-originated DKP.
public sealed class SheetDkpPullService
{
    // How long a pull result is considered fresh. Bounds the Sheets API call
    // rate to at most one read per linkshell per interval across page loads.
    private static readonly TimeSpan PullInterval = TimeSpan.FromSeconds(60);

    private readonly ApplicationDbContext _db;
    private readonly SheetMigrationService _migration;
    private readonly IMemoryCache _cache;
    private readonly ILogger<SheetDkpPullService> _logger;

    public SheetDkpPullService(
        ApplicationDbContext db,
        SheetMigrationService migration,
        IMemoryCache cache,
        ILogger<SheetDkpPullService> logger)
    {
        _db = db;
        _migration = migration;
        _cache = cache;
        _logger = logger;
    }

    public async Task EnsureFreshAsync(int linkshellId, CancellationToken cancellationToken)
    {
        if (linkshellId <= 0) return;

        var cacheKey = $"sheet-dkp-pull:{linkshellId}";
        if (_cache.TryGetValue(cacheKey, out _)) return;
        // Stamp the window first so concurrent requests don't all pull at once.
        _cache.Set(cacheKey, true, PullInterval);

        var linkshell = await _db.Linkshells
            .AsNoTracking()
            .Where(l => l.Id == linkshellId)
            .Select(l => new
            {
                l.GoogleSpreadsheetId,
                l.GoogleOAuthRefreshTokenEnc,
                l.SheetSyncEnabled,
            })
            .FirstOrDefaultAsync(cancellationToken);

        // Only pull when the sheet is connected AND push-sync is enabled. With
        // push-sync on, the app appends all of its DKP activity to the sheet, so
        // the sheet's totals are a superset of app activity -- safe to treat as
        // the source of truth and SET balances from. If push-sync is off, the
        // sheet would be missing app-side earnings and a SET pull would clobber
        // them, so we leave app-side accounting in charge.
        if (linkshell is null
            || !linkshell.SheetSyncEnabled
            || string.IsNullOrWhiteSpace(linkshell.GoogleSpreadsheetId)
            || string.IsNullOrWhiteSpace(linkshell.GoogleOAuthRefreshTokenEnc))
        {
            return;
        }

        try
        {
            var updated = await _migration.RefreshMemberBalancesAsync(
                linkshellId,
                linkshell.GoogleSpreadsheetId!,
                readRange: null, // defaults to Main!B7:C200, same as the manual Import
                cancellationToken);

            if (updated > 0)
            {
                _logger.LogInformation(
                    "Sheet DKP pull: linkshell {LinkshellId} refreshed {Count} member balance(s) from the sheet.",
                    linkshellId, updated);
            }
        }
        catch (GoogleOAuthRevokedException ex)
        {
            _logger.LogWarning(ex, "Sheet DKP pull skipped: linkshell {LinkshellId} Google OAuth revoked.", linkshellId);
        }
        catch (Exception ex)
        {
            // A sheet read hiccup must never fail the DKP page; serve stale data.
            _logger.LogWarning(ex, "Sheet DKP pull failed for linkshell {LinkshellId}.", linkshellId);
        }
    }
}
