using LinkshellManagerDiscordApp.Data;
using LinkshellManagerDiscordApp.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace LinkshellManagerDiscordApp.Services;

// App-wide settings accessor backed by the AppSettings table, with a short
// in-memory cache so the per-request addon kill-switch check doesn't hit the
// database on every addon call. Writes invalidate the cache so a toggle takes
// effect immediately on this instance; other instances pick it up within the
// cache TTL.
public sealed class GlobalSettingsService
{
    // Key stored in the AppSettings table for the addon kill-switch.
    public const string AddonDisabledKey = "addon.disabled";

    private const string CachePrefix = "globalsetting:";
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(30);

    private readonly ApplicationDbContext _dbContext;
    private readonly IMemoryCache _cache;

    public GlobalSettingsService(ApplicationDbContext dbContext, IMemoryCache cache)
    {
        _dbContext = dbContext;
        _cache = cache;
    }

    // True when a super admin has globally disabled the addon. Cached briefly
    // because this is checked on every addon API request.
    public async Task<bool> IsAddonGloballyDisabledAsync(CancellationToken cancellationToken = default)
    {
        var cacheKey = CachePrefix + AddonDisabledKey;
        if (_cache.TryGetValue(cacheKey, out bool cached))
        {
            return cached;
        }

        var raw = await _dbContext.AppSettings
            .AsNoTracking()
            .Where(s => s.Key == AddonDisabledKey)
            .Select(s => s.Value)
            .FirstOrDefaultAsync(cancellationToken);

        var disabled = string.Equals(raw, "true", StringComparison.OrdinalIgnoreCase);
        _cache.Set(cacheKey, disabled, CacheTtl);
        return disabled;
    }

    public async Task SetAddonGloballyDisabledAsync(bool disabled, CancellationToken cancellationToken = default)
    {
        var setting = await _dbContext.AppSettings
            .FirstOrDefaultAsync(s => s.Key == AddonDisabledKey, cancellationToken);
        if (setting is null)
        {
            setting = new AppSetting { Key = AddonDisabledKey };
            _dbContext.AppSettings.Add(setting);
        }

        setting.Value = disabled ? "true" : "false";
        await _dbContext.SaveChangesAsync(cancellationToken);

        // Invalidate so the new state is visible immediately on this instance.
        _cache.Remove(CachePrefix + AddonDisabledKey);
    }
}
