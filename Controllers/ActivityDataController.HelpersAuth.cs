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
            catch (DiscordApiException) when (!_environment.IsDevelopment())
            {
                return null;
            }
            catch (DiscordApiException)
            {
                return null;
            }
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
        if (membership is null || string.IsNullOrWhiteSpace(membership.Rank))
        {
            return false;
        }

        return membership.Rank.Equals("Leader", StringComparison.OrdinalIgnoreCase) ||
               membership.Rank.Equals("Officer", StringComparison.OrdinalIgnoreCase);
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
        var rankName = string.IsNullOrWhiteSpace(rank) ? "Member" : rank.Trim();
        var role = await _dbContext.LinkshellRoles
            .AsNoTracking()
            .FirstOrDefaultAsync(r => r.LinkshellId == linkshellId && r.Name == rankName, cancellationToken);
        if (role is null)
        {
            role = await _dbContext.LinkshellRoles
                .AsNoTracking()
                .FirstOrDefaultAsync(r => r.LinkshellId == linkshellId && r.Name == "Member", cancellationToken);
        }
        return role;
    }

    private async Task<List<LinkshellRole>> EnsureDefaultRolesAsync(int linkshellId, CancellationToken cancellationToken)
    {
        var existing = await _dbContext.LinkshellRoles
            .Where(r => r.LinkshellId == linkshellId)
            .AsNoTracking()
            .ToListAsync(cancellationToken);

        var existingByName = existing.ToDictionary(r => r.Name, StringComparer.OrdinalIgnoreCase);
        var added = new List<LinkshellRole>();

        if (!existingByName.ContainsKey("Leader"))
        {
            added.Add(new LinkshellRole
            {
                LinkshellId = linkshellId,
                Name = "Leader",
                IsSystem = true,
                SortOrder = 0,
                CanManageRoles = true,
                CanManageMembers = true,
                CanManageEvents = true,
                CanModerateLiveEvent = true,
                CanAddLoot = true,
                CanManageInventory = true,
                CanManageTreasury = true,
                CanManageRules = true,
                CanManageAnnouncements = true,
                CanManageTods = true,
                CanAuditDkp = true,
                CanManageAuctions = true,
                CanCustomizeLinkshell = true
            });
        }

        if (!existingByName.ContainsKey("Officer"))
        {
            added.Add(new LinkshellRole
            {
                LinkshellId = linkshellId,
                Name = "Officer",
                IsSystem = true,
                SortOrder = 1,
                CanManageRoles = false,
                CanManageMembers = false,
                CanManageEvents = true,
                CanModerateLiveEvent = true,
                CanAddLoot = true,
                CanManageInventory = true,
                CanManageTreasury = false,
                CanManageRules = true,
                CanManageAnnouncements = true,
                CanManageTods = true,
                CanAuditDkp = false,
                CanManageAuctions = true,
                CanCustomizeLinkshell = false
            });
        }

        if (!existingByName.ContainsKey("Member"))
        {
            added.Add(new LinkshellRole
            {
                LinkshellId = linkshellId,
                Name = "Member",
                IsSystem = true,
                SortOrder = 2
            });
        }

        if (added.Count > 0)
        {
            await _dbContext.LinkshellRoles.AddRangeAsync(added, cancellationToken);
            await _dbContext.SaveChangesAsync(cancellationToken);
            existing.AddRange(added);
        }

        return existing.OrderBy(r => r.SortOrder).ThenBy(r => r.Name).ToList();
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
    }
}
