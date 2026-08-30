using LinkshellManagerDiscordApp.Data;
using LinkshellManagerDiscordApp.Models;
using Microsoft.EntityFrameworkCore;

namespace LinkshellManagerDiscordApp.Services;

// The super-admin permission override.
//
// When the global "admin.override" setting is on, a super admin (AppUser.IsSuperAdmin,
// seeded at startup from SuperAdmin:Email / SuperAdmin:CharacterName — see Program.cs)
// gets EVERY permission in EVERY linkshell they are a member of, without their stored
// AppUserLinkshell.Rank being modified. The roster surfaces this as an "ADMIN" badge
// next to their real rank.
//
// SCOPE — read this before adding a call site:
// This service deliberately knows nothing about linkshells, so it can never by itself
// grant access to a linkshell the user has not joined. Every caller MUST first establish
// membership (a non-null AppUserLinkshell) and only then consult this service:
//
//     if (membership is null) return false;                                   // FIRST
//     if (await _adminOverride.IsActiveForAsync(appUserId, ct)) return true;  // then
//
// Getting that order wrong turns the override into "admin can manage every linkshell on
// the server", which is NOT what this is.
//
// Scoped, so the two lookups are memoized for the lifetime of a request. When the toggle
// is off (the common case) this costs zero database reads beyond the 30s-cached setting.
public sealed class AdminOverrideService
{
    private readonly ApplicationDbContext _dbContext;
    private readonly GlobalSettingsService _globalSettings;
    private readonly ILogger<AdminOverrideService> _logger;

    private static readonly IReadOnlySet<string> EmptyAdminIds =
        new HashSet<string>(StringComparer.Ordinal);

    private bool? _enabled;
    private readonly Dictionary<string, bool> _isSuperAdminById = new(StringComparer.Ordinal);
    private HashSet<string>? _activeAdminIds;
    private bool _loggedGrantThisRequest;

    public AdminOverrideService(
        ApplicationDbContext dbContext,
        GlobalSettingsService globalSettings,
        ILogger<AdminOverrideService> logger)
    {
        _dbContext = dbContext;
        _globalSettings = globalSettings;
        _logger = logger;
    }

    // Whether the global toggle is on, regardless of who is asking. Used by the roster
    // mappers, which decide the ADMIN badge per member rather than for the caller.
    public async Task<bool> IsEnabledAsync(CancellationToken cancellationToken = default)
    {
        _enabled ??= await _globalSettings.IsAdminOverrideEnabledAsync(cancellationToken);
        return _enabled.Value;
    }

    public async Task<bool> IsActiveForAsync(string? appUserId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(appUserId))
        {
            return false;
        }

        // Short-circuit before touching the Users table: when the override is off,
        // nobody's super-admin flag matters.
        if (!await IsEnabledAsync(cancellationToken))
        {
            return false;
        }

        if (!_isSuperAdminById.TryGetValue(appUserId, out var isSuperAdmin))
        {
            isSuperAdmin = await _dbContext.Users
                .AsNoTracking()
                .Where(u => u.Id == appUserId)
                .Select(u => u.IsSuperAdmin)
                .FirstOrDefaultAsync(cancellationToken);
            _isSuperAdminById[appUserId] = isSuperAdmin;
        }

        if (isSuperAdmin)
        {
            LogGrantOnce(appUserId);
        }

        return isSuperAdmin;
    }

    // Every AppUserId that currently carries the override, for badging a whole roster
    // without an N+1. Memoized per request; returns an EMPTY set when the toggle is
    // off, so a caller that forgets to check IsEnabledAsync still gets the safe answer.
    // Prefer reading AppUser.IsSuperAdmin directly when the navigation is already
    // loaded — this exists for the views and projections where it is not.
    public async Task<IReadOnlySet<string>> GetActiveAdminAppUserIdsAsync(CancellationToken cancellationToken = default)
    {
        if (!await IsEnabledAsync(cancellationToken))
        {
            return EmptyAdminIds;
        }

        if (_activeAdminIds is null)
        {
            var ids = await _dbContext.Users
                .AsNoTracking()
                .Where(u => u.IsSuperAdmin)
                .Select(u => u.Id)
                .ToListAsync(cancellationToken);
            _activeAdminIds = ids.ToHashSet(StringComparer.Ordinal);
        }

        return _activeAdminIds;
    }

    // Overload for callers that already have the AppUser loaded, so the flag doesn't
    // cost a second read.
    public async Task<bool> IsActiveForAsync(AppUser? user, CancellationToken cancellationToken = default)
    {
        if (user is null)
        {
            return false;
        }

        _isSuperAdminById[user.Id] = user.IsSuperAdmin;

        if (!user.IsSuperAdmin)
        {
            return false;
        }

        if (!await IsEnabledAsync(cancellationToken))
        {
            return false;
        }

        LogGrantOnce(user.Id);
        return true;
    }

    // One line per request, not per check — the override is consulted dozens of times
    // while rendering a page, but an operator only needs to know that a given request
    // ran with admin elevation.
    private void LogGrantOnce(string appUserId)
    {
        if (_loggedGrantThisRequest)
        {
            return;
        }

        _loggedGrantThisRequest = true;
        _logger.LogInformation(
            "Admin override granted elevated linkshell permissions to super admin {AppUserId}.",
            appUserId);
    }
}
