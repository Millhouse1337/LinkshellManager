using LinkshellManagerDiscordApp.Data;
using LinkshellManagerDiscordApp.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace LinkshellManagerDiscordApp.Services;

// Which catalog tables the Data Admin UI currently SHOWS. This is the runtime half of the
// "choose the tables" requirement: the catalog (code) decides what CAN be reached, this service
// (one AppSettings row per shown table) decides what IS reachable right now. Missing rows read as
// not shown, so a fresh install shows nothing until a super admin ticks tables -- and a Hidden
// table stays hidden even if someone plants a row for it.
//
// Same shape as GlobalSettingsService: a short in-memory cache (the sidebar and every /data-admin
// request consult it) that a write invalidates so a change is visible immediately on this
// instance and within the TTL on others.
public sealed class DataAdminSelectionService
{
    public const string KeyPrefix = "dataadmin.show.";
    private const string CacheKey = "dataadmin:shown";
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(30);

    private readonly ApplicationDbContext _db;
    private readonly IMemoryCache _cache;
    private readonly DataAdminCatalog _catalog;

    public DataAdminSelectionService(ApplicationDbContext db, IMemoryCache cache, DataAdminCatalog catalog)
    {
        _db = db;
        _cache = cache;
        _catalog = catalog;
    }

    // CLR names of the shown tables, minus anything the policy hides.
    public async Task<IReadOnlySet<string>> GetShownClrNamesAsync(CancellationToken ct = default)
    {
        if (_cache.TryGetValue(CacheKey, out IReadOnlySet<string>? cached) && cached is not null)
        {
            return cached;
        }

        var rows = await _db.AppSettings.AsNoTracking()
            .Where(setting => setting.Key.StartsWith(KeyPrefix))
            .Select(setting => new { setting.Key, setting.Value })
            .ToListAsync(ct);

        var shown = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            // "true" however it was capitalised, the way GlobalSettingsService reads its bools.
            if (!string.Equals(row.Value, "true", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            var clrName = row.Key[KeyPrefix.Length..];
            if (_catalog.FindByClrName(clrName) is { IsHidden: false })
            {
                shown.Add(clrName);
            }
        }

        _cache.Set(CacheKey, (IReadOnlySet<string>)shown, CacheTtl);
        return shown;
    }

    public async Task<IReadOnlyList<DataAdminModel>> GetShownAsync(CancellationToken ct = default)
    {
        var shown = await GetShownClrNamesAsync(ct);
        return _catalog.Models.Where(model => shown.Contains(model.ClrName)).ToList();
    }

    public async Task<bool> IsShownAsync(DataAdminModel model, CancellationToken ct = default) =>
        !model.IsHidden && (await GetShownClrNamesAsync(ct)).Contains(model.ClrName);

    // Whole-set write from the chooser: a "true" row for every wanted table, no row for the rest
    // (a false row and a missing row mean the same thing). Unknown and Hidden names are dropped.
    public async Task SetShownAsync(IEnumerable<string> shownClrNames, CancellationToken ct = default)
    {
        var wanted = shownClrNames
            .Where(clrName => _catalog.FindByClrName(clrName) is { IsHidden: false })
            .ToHashSet(StringComparer.Ordinal);

        var rows = await _db.AppSettings
            .Where(setting => setting.Key.StartsWith(KeyPrefix))
            .ToListAsync(ct);
        foreach (var row in rows)
        {
            if (wanted.Remove(row.Key[KeyPrefix.Length..]))
            {
                row.Value = "true";
            }
            else
            {
                _db.AppSettings.Remove(row);
            }
        }
        foreach (var clrName in wanted)
        {
            _db.AppSettings.Add(new AppSetting { Key = KeyPrefix + clrName, Value = "true" });
        }

        await _db.SaveChangesAsync(ct);
        _cache.Remove(CacheKey);
    }
}
