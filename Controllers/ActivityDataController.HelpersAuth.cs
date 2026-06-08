using System.Globalization;
using System.Net.Http.Headers;
using LinkshellManagerDiscordApp.Data;
using LinkshellManagerDiscordApp.Models;
using LinkshellManagerDiscordApp.Services;
using LinkshellManagerDiscordApp.ViewModels;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NodaTime;

namespace LinkshellManagerDiscordApp.Controllers;

public sealed partial class ActivityDataController
{
    // When a bearer token is presented, validate it strictly. Do NOT silently
    // downgrade to cookie auth when the bearer fails — that would let an
    // attacker who can disrupt outbound calls to discord.com (or who supplies
    // a bogus bearer to suppress preflight CSRF protection) coerce the
    // request into the cookie-auth path.
    // The Discord bearer token of the current request, captured once the bearer
    // path validates. Only the Activity (bearer) path has a Discord token; the
    // cookie/web path leaves this null. Used by the per-linkshell guild lock to
    // verify membership against discord.com. The controller is request-scoped,
    // so a field is safe here.
    private string? _resolvedDiscordAccessToken;

    private async Task<AppUser?> ResolveAppUserAsync(CancellationToken cancellationToken)
    {
        if (TryGetBearerToken(out var accessToken))
        {
            try
            {
                var localUser = await _discordIdentityService.GetCurrentLocalUserAsync(accessToken, cancellationToken);
                if (!string.IsNullOrWhiteSpace(localUser.AppUser?.Id))
                {
                    _resolvedDiscordAccessToken = accessToken;
                    return await _userManager.FindByIdAsync(localUser.AppUser.Id);
                }
            }
            catch (DiscordApiException)
            {
                // Hard reject — never fall through to cookie auth.
            }
            return null;
        }

        if (User.Identity?.IsAuthenticated == true)
        {
            return await _userManager.GetUserAsync(User);
        }

        return null;
    }

    private bool TryGetBearerToken(out string accessToken)
    {
        accessToken = string.Empty;

        if (!AuthenticationHeaderValue.TryParse(Request.Headers.Authorization, out var headerValue))
        {
            return false;
        }

        if (!"Bearer".Equals(headerValue.Scheme, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(headerValue.Parameter))
        {
            return false;
        }

        accessToken = headerValue.Parameter;
        return true;
    }

    // The Discord guild (server) this request came from, sent by the Activity
    // as X-Discord-Guild-Id. Null on the website (no Discord context) and when
    // launched outside a server. Used to enforce per-linkshell guild locks.
    private string? GetRequestGuildId()
    {
        var value = Request.Headers["X-Discord-Guild-Id"].ToString();
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    // A linkshell locked to a guild is only accessible from the Activity when
    // launched from that guild. Returns true when the lock blocks this request.
    // The website (no guild header) is intentionally unaffected — the lock only
    // governs the Discord Activity, so a request with no guild id passes.
    private bool IsBlockedByGuildLock(Linkshell? linkshell)
    {
        // Only an explicitly *locked* linkshell restricts access. Merely having a
        // server set (LockToDiscordGuild == false) never blocks anyone.
        if (linkshell is null || !linkshell.LockToDiscordGuild || string.IsNullOrWhiteSpace(linkshell.DiscordGuildId))
        {
            return false;
        }

        var requestGuildId = GetRequestGuildId();
        // No guild context (website / non-Discord) → not blocked. Activity
        // requests always carry the header, so a mismatch here means the
        // Activity is open in a different server than the linkshell is tied to.
        if (requestGuildId is null)
        {
            return false;
        }

        return !string.Equals(requestGuildId, linkshell.DiscordGuildId, StringComparison.Ordinal);
    }

    private async Task<AppUserLinkshell?> GetMembershipAsync(string appUserId, int linkshellId, CancellationToken cancellationToken)
    {
        var membership = await _dbContext.AppUserLinkshells
            .Include(link => link.Linkshell)
            .FirstOrDefaultAsync(link => link.AppUserId == appUserId && link.LinkshellId == linkshellId, cancellationToken);

        if (membership is null)
        {
            return null;
        }

        // Enforce the per-linkshell Discord guild lock for every endpoint that
        // resolves access through GetMembershipAsync. Only applies when the
        // linkshell is *locked* (a set-but-unlocked server doesn't gate access).
        // A locked linkshell the user can't prove membership in is treated as
        // "no access" (the caller maps a null membership to Forbid()/403).
        var guildId = membership.Linkshell?.LockToDiscordGuild == true ? membership.Linkshell?.DiscordGuildId : null;
        if (!string.IsNullOrWhiteSpace(guildId))
        {
            var allowed = await FilterAccessibleLinkshellIdsAsync(
                new[] { (linkshellId, (string?)guildId) }, cancellationToken);
            if (!allowed.Contains(linkshellId))
            {
                return null;
            }
        }

        return membership;
    }

    // Given candidate linkshells (id + the guild they're locked to), returns the
    // subset the current request is allowed to access:
    //   * no DiscordGuildId  -> always allowed (unlocked; never calls Discord)
    //   * locked + bearer token + verified member -> allowed
    //   * locked + no token (cookie path) or non-member -> excluded
    // Fail-closed: if the membership check can't reach Discord, the locked
    // linkshell is excluded rather than 500-ing the whole request, so unlocked
    // linkshells keep working during a Discord outage.
    private async Task<HashSet<int>> FilterAccessibleLinkshellIdsAsync(
        IReadOnlyCollection<(int LinkshellId, string? DiscordGuildId)> candidates,
        CancellationToken cancellationToken)
    {
        var allowed = new HashSet<int>();
        foreach (var candidate in candidates)
        {
            if (string.IsNullOrWhiteSpace(candidate.DiscordGuildId))
            {
                allowed.Add(candidate.LinkshellId);
                continue;
            }

            if (string.IsNullOrWhiteSpace(_resolvedDiscordAccessToken))
            {
                continue;
            }

            try
            {
                if (await _discordIdentityService.VerifyGuildMembershipAsync(
                        _resolvedDiscordAccessToken, candidate.DiscordGuildId!, cancellationToken))
                {
                    allowed.Add(candidate.LinkshellId);
                }
            }
            catch (DiscordApiException ex)
            {
                _logger.LogWarning(
                    ex,
                    "Guild membership check failed for linkshell {LinkshellId}; denying access (fail-closed).",
                    candidate.LinkshellId);
            }
        }

        return allowed;
    }

    private static bool CanManageLinkshell(AppUserLinkshell? membership)
    {
        return LinkshellRanks.IsLeaderOrOfficer(membership?.Rank);
    }

    private async Task<bool> CanAsync(
        AppUserLinkshell? membership,
        Func<LinkshellRole, bool> selector,
        CancellationToken cancellationToken)
    {
        if (membership is null)
        {
            return false;
        }

        var role = await GetEffectiveRoleAsync(membership.Rank, membership.LinkshellId, cancellationToken);
        return role is not null && selector(role);
    }

    private async Task<LinkshellRole?> GetEffectiveRoleAsync(
        string? rank,
        int linkshellId,
        CancellationToken cancellationToken)
    {
        await EnsureDefaultRolesAsync(linkshellId, cancellationToken);
        var rankName = string.IsNullOrWhiteSpace(rank) ? LinkshellRanks.Member : rank.Trim();
        var role = await _dbContext.LinkshellRoles
            .AsNoTracking()
            .FirstOrDefaultAsync(r => r.LinkshellId == linkshellId && r.Name == rankName, cancellationToken);
        if (role is null)
        {
            role = await _dbContext.LinkshellRoles
                .AsNoTracking()
                .FirstOrDefaultAsync(r => r.LinkshellId == linkshellId && r.Name == LinkshellRanks.Member, cancellationToken);
        }
        return role;
    }

    private async Task<List<LinkshellRole>> EnsureDefaultRolesAsync(int linkshellId, CancellationToken cancellationToken)
    {
        var seeded = await EnsureDefaultRolesForLinkshellsAsync(new[] { linkshellId }, cancellationToken);
        return seeded.TryGetValue(linkshellId, out var roles) ? roles : new List<LinkshellRole>();
    }

    // Batch variant: seeds missing default roles for every supplied linkshell in two
    // round-trips total (one SELECT, one INSERT) instead of N pairs from a foreach.
    private async Task<Dictionary<int, List<LinkshellRole>>> EnsureDefaultRolesForLinkshellsAsync(
        IReadOnlyCollection<int> linkshellIds,
        CancellationToken cancellationToken)
    {
        var result = new Dictionary<int, List<LinkshellRole>>();
        if (linkshellIds.Count == 0)
        {
            return result;
        }

        var existing = await _dbContext.LinkshellRoles
            .Where(r => linkshellIds.Contains(r.LinkshellId))
            .AsNoTracking()
            .ToListAsync(cancellationToken);

        var existingByLinkshell = existing
            .GroupBy(r => r.LinkshellId)
            .ToDictionary(g => g.Key, g => g.ToList());

        var toAdd = new List<LinkshellRole>();
        foreach (var linkshellId in linkshellIds)
        {
            existingByLinkshell.TryGetValue(linkshellId, out var rolesForLinkshell);
            rolesForLinkshell ??= new List<LinkshellRole>();

            var existingNames = new HashSet<string>(rolesForLinkshell.Select(r => r.Name), StringComparer.OrdinalIgnoreCase);
            foreach (var defaultRole in LinkshellRoleDefaults.BuildDefaultRoles(linkshellId))
            {
                if (!existingNames.Contains(defaultRole.Name))
                {
                    toAdd.Add(defaultRole);
                }
            }

            result[linkshellId] = rolesForLinkshell;
        }

        if (toAdd.Count > 0)
        {
            await _dbContext.LinkshellRoles.AddRangeAsync(toAdd, cancellationToken);
            await _dbContext.SaveChangesAsync(cancellationToken);

            foreach (var added in toAdd)
            {
                if (!result.TryGetValue(added.LinkshellId, out var bucket))
                {
                    bucket = new List<LinkshellRole>();
                    result[added.LinkshellId] = bucket;
                }
                bucket.Add(added);
            }
        }

        foreach (var key in result.Keys.ToList())
        {
            result[key] = result[key].OrderBy(r => r.SortOrder).ThenBy(r => r.Name).ToList();
        }

        return result;
    }

    private static void ApplyPermissions(LinkshellRole role, ActivityLinkshellRolePermissions permissions)
    {
        role.CanManageRoles = permissions.CanManageRoles;
        role.CanManageMembers = permissions.CanManageMembers;
        role.CanManageEvents = permissions.CanManageEvents;
        role.CanModerateLiveEvent = permissions.CanModerateLiveEvent;
        role.CanAddLoot = permissions.CanAddLoot;
        role.CanManageInventory = permissions.CanManageInventory;
        role.CanManageTreasury = permissions.CanManageTreasury;
        role.CanManageRules = permissions.CanManageRules;
        role.CanManageAnnouncements = permissions.CanManageAnnouncements;
        role.CanManageTods = permissions.CanManageTods;
        role.CanAuditDkp = permissions.CanAuditDkp;
        role.CanManageAuctions = permissions.CanManageAuctions;
        role.CanLockAuctions = permissions.CanLockAuctions;
        role.CanCustomizeLinkshell = permissions.CanCustomizeLinkshell;
        role.CanManageParties = permissions.CanManageParties;
        role.CanManageInvites = permissions.CanManageInvites;
        role.CanBid = permissions.CanBid;
    }
}
