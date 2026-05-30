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
    private async Task<AppUser?> ResolveAppUserAsync(CancellationToken cancellationToken)
    {
        if (TryGetBearerToken(out var accessToken))
        {
            try
            {
                var localUser = await _discordIdentityService.GetCurrentLocalUserAsync(accessToken, cancellationToken);
                if (!string.IsNullOrWhiteSpace(localUser.AppUser?.Id))
                {
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

    private async Task<AppUserLinkshell?> GetMembershipAsync(string appUserId, int linkshellId, CancellationToken cancellationToken)
    {
        return await _dbContext.AppUserLinkshells
            .Include(link => link.Linkshell)
            .FirstOrDefaultAsync(link => link.AppUserId == appUserId && link.LinkshellId == linkshellId, cancellationToken);
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
        role.CanCustomizeLinkshell = permissions.CanCustomizeLinkshell;
        role.CanManageParties = permissions.CanManageParties;
    }
}
