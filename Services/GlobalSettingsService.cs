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

    // Key stored in the AppSettings table for the server-wide Claim Shield switch. Separate from
    // the addon kill-switch above: this turns off ONE addon feature everywhere without taking the
    // whole addon down with it.
    public const string ClaimShieldDisabledKey = "claimshield.disabled";

    // Key stored in the AppSettings table for the super-admin permission override.
    // When "true", a super admin gets every permission in every linkshell they are
    // a member of. See AdminOverrideService.
    public const string AdminOverrideKey = "admin.override";

    // Keys stored in the AppSettings table for the launcher download link. The URL is kept as a
    // setting rather than hard-coded so a new launcher build can be published by pasting its
    // release URL here, with no redeploy. The enabled flag gates whether the link is shown at
    // all -- and LauncherController re-checks it, so turning it off actually blocks the download
    // rather than just hiding the button.
    public const string LauncherDownloadEnabledKey = "launcher.download.enabled";
    public const string LauncherDownloadUrlKey = "launcher.download.url";

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
    public Task<bool> IsAddonGloballyDisabledAsync(CancellationToken cancellationToken = default)
        => GetBoolAsync(AddonDisabledKey, cancellationToken);

    public Task SetAddonGloballyDisabledAsync(bool disabled, CancellationToken cancellationToken = default)
        => SetBoolAsync(AddonDisabledKey, disabled, cancellationToken);

    // True when a super admin has globally switched Claim Shield off. Read on the addon's /me
    // sweep, so it is cached on the same short TTL as the rest.
    public Task<bool> IsClaimShieldGloballyDisabledAsync(CancellationToken cancellationToken = default)
        => GetBoolAsync(ClaimShieldDisabledKey, cancellationToken);

    public Task SetClaimShieldGloballyDisabledAsync(bool disabled, CancellationToken cancellationToken = default)
        => SetBoolAsync(ClaimShieldDisabledKey, disabled, cancellationToken);

    // True when the super-admin permission override is switched on. Cached briefly
    // because this is checked on every linkshell permission check.
    public Task<bool> IsAdminOverrideEnabledAsync(CancellationToken cancellationToken = default)
        => GetBoolAsync(AdminOverrideKey, cancellationToken);

    public Task SetAdminOverrideEnabledAsync(bool enabled, CancellationToken cancellationToken = default)
        => SetBoolAsync(AdminOverrideKey, enabled, cancellationToken);

    // True when the launcher download link should be shown. Off until a super admin turns it on,
    // because GetBoolAsync treats a missing row as false -- so the link fails closed.
    public Task<bool> IsLauncherDownloadEnabledAsync(CancellationToken cancellationToken = default)
        => GetBoolAsync(LauncherDownloadEnabledKey, cancellationToken);

    public Task SetLauncherDownloadEnabledAsync(bool enabled, CancellationToken cancellationToken = default)
        => SetBoolAsync(LauncherDownloadEnabledKey, enabled, cancellationToken);

    // The absolute URL the download link points at, or null if one has never been saved.
    public Task<string?> GetLauncherDownloadUrlAsync(CancellationToken cancellationToken = default)
        => GetStringAsync(LauncherDownloadUrlKey, cancellationToken);

    public Task SetLauncherDownloadUrlAsync(string? url, CancellationToken cancellationToken = default)
        => SetStringAsync(LauncherDownloadUrlKey, url, cancellationToken);

    // A missing row reads as false, so a setting that has never been toggled is off.
    private async Task<bool> GetBoolAsync(string key, CancellationToken cancellationToken)
    {
        var cacheKey = CachePrefix + key;
        if (_cache.TryGetValue(cacheKey, out bool cached))
        {
            return cached;
        }

        var raw = await _dbContext.AppSettings
            .AsNoTracking()
            .Where(s => s.Key == key)
            .Select(s => s.Value)
            .FirstOrDefaultAsync(cancellationToken);

        var value = string.Equals(raw, "true", StringComparison.OrdinalIgnoreCase);
        _cache.Set(cacheKey, value, CacheTtl);
        return value;
    }

    private async Task SetBoolAsync(string key, bool value, CancellationToken cancellationToken)
    {
        var setting = await _dbContext.AppSettings
            .FirstOrDefaultAsync(s => s.Key == key, cancellationToken);
        if (setting is null)
        {
            setting = new AppSetting { Key = key };
            _dbContext.AppSettings.Add(setting);
        }

        setting.Value = value ? "true" : "false";
        await _dbContext.SaveChangesAsync(cancellationToken);

        // Invalidate so the new state is visible immediately on this instance.
        _cache.Remove(CachePrefix + key);
    }

    // A missing row reads as null. Cached on the same short TTL as the bools so a never-set
    // value still avoids a query on every page render.
    private async Task<string?> GetStringAsync(string key, CancellationToken cancellationToken)
    {
        var cacheKey = CachePrefix + key;
        if (_cache.TryGetValue(cacheKey, out string? cached))
        {
            return cached;
        }

        var raw = await _dbContext.AppSettings
            .AsNoTracking()
            .Where(s => s.Key == key)
            .Select(s => s.Value)
            .FirstOrDefaultAsync(cancellationToken);

        _cache.Set(cacheKey, raw, CacheTtl);
        return raw;
    }

    private async Task SetStringAsync(string key, string? value, CancellationToken cancellationToken)
    {
        var setting = await _dbContext.AppSettings
            .FirstOrDefaultAsync(s => s.Key == key, cancellationToken);
        if (setting is null)
        {
            setting = new AppSetting { Key = key };
            _dbContext.AppSettings.Add(setting);
        }

        // Store null rather than a blank row when the value is cleared.
        setting.Value = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        await _dbContext.SaveChangesAsync(cancellationToken);

        // Invalidate so the new value is visible immediately on this instance.
        _cache.Remove(CachePrefix + key);
    }
}
