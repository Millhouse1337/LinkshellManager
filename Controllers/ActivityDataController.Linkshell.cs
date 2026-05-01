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
    [HttpGet("linkshells/{linkshellId:int}/roles")]
    public async Task<IActionResult> GetLinkshellRolesAsync(int linkshellId, CancellationToken cancellationToken)
    {
        var appUser = await ResolveAppUserAsync(cancellationToken);
        if (appUser is null)
        {
            return Unauthorized(new { error = "Sign in to load linkshell roles." });
        }

        var membership = await GetMembershipAsync(appUser.Id, linkshellId, cancellationToken);
        if (membership is null)
        {
            return Forbid();
        }

        var roles = await EnsureDefaultRolesAsync(linkshellId, cancellationToken);
        var dtoRoles = roles.Select(MapLinkshellRoleDto).ToList();
        return Ok(new ActivityLinkshellRolesResponse(linkshellId, dtoRoles));
    }

    [HttpPost("linkshells/{linkshellId:int}/roles")]
    public async Task<IActionResult> CreateLinkshellRoleAsync(
        int linkshellId,
        [FromBody] ActivityLinkshellRolePermissions request,
        CancellationToken cancellationToken)
    {
        var appUser = await ResolveAppUserAsync(cancellationToken);
        if (appUser is null)
        {
            return Unauthorized(new { error = "Sign in to create a linkshell role." });
        }

        var membership = await GetMembershipAsync(appUser.Id, linkshellId, cancellationToken);
        if (!await CanAsync(membership, r => r.CanManageRoles, cancellationToken))
        {
            return Forbid();
        }

        var name = request.Name?.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            return BadRequest(new { error = "A role name is required." });
        }
        if (name.Length > 64)
        {
            return BadRequest(new { error = "Role name must be 64 characters or fewer." });
        }

        await EnsureDefaultRolesAsync(linkshellId, cancellationToken);

        var existing = await _dbContext.LinkshellRoles
            .AsNoTracking()
            .FirstOrDefaultAsync(r => r.LinkshellId == linkshellId && r.Name == name, cancellationToken);
        if (existing is not null)
        {
            return BadRequest(new { error = "A role with that name already exists." });
        }

        var maxSort = await _dbContext.LinkshellRoles
            .Where(r => r.LinkshellId == linkshellId)
            .MaxAsync(r => (int?)r.SortOrder, cancellationToken) ?? 0;

        var role = new LinkshellRole
        {
            LinkshellId = linkshellId,
            Name = name,
            IsSystem = false,
            SortOrder = maxSort + 1
        };
        ApplyPermissions(role, request);
        _dbContext.LinkshellRoles.Add(role);
        await _dbContext.SaveChangesAsync(cancellationToken);

        return Ok(MapLinkshellRoleDto(role));
    }

    [HttpPost("linkshells/{linkshellId:int}/roles/{roleId:int}/update")]
    public async Task<IActionResult> UpdateLinkshellRoleAsync(
        int linkshellId,
        int roleId,
        [FromBody] ActivityLinkshellRolePermissions request,
        CancellationToken cancellationToken)
    {
        var appUser = await ResolveAppUserAsync(cancellationToken);
        if (appUser is null)
        {
            return Unauthorized(new { error = "Sign in to update a linkshell role." });
        }

        var membership = await GetMembershipAsync(appUser.Id, linkshellId, cancellationToken);
        if (!await CanAsync(membership, r => r.CanManageRoles, cancellationToken))
        {
            return Forbid();
        }

        var role = await _dbContext.LinkshellRoles
            .FirstOrDefaultAsync(r => r.Id == roleId && r.LinkshellId == linkshellId, cancellationToken);
        if (role is null)
        {
            return NotFound(new { error = "The role was not found." });
        }

        if (!role.IsSystem)
        {
            var name = request.Name?.Trim();
            if (!string.IsNullOrWhiteSpace(name))
            {
                if (name.Length > 64)
                {
                    return BadRequest(new { error = "Role name must be 64 characters or fewer." });
                }

                if (!string.Equals(role.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    var clash = await _dbContext.LinkshellRoles.AnyAsync(r =>
                        r.LinkshellId == linkshellId && r.Id != roleId && r.Name == name, cancellationToken);
                    if (clash)
                    {
                        return BadRequest(new { error = "Another role with that name already exists." });
                    }

                    var previousName = role.Name;
                    role.Name = name;
                    await _dbContext.AppUserLinkshells
                        .Where(link => link.LinkshellId == linkshellId && link.Rank == previousName)
                        .ExecuteUpdateAsync(setters => setters.SetProperty(link => link.Rank, name), cancellationToken);
                }
            }
        }

        if (role.Name.Equals("Leader", StringComparison.OrdinalIgnoreCase))
        {
            // Safety: a Leader must always retain CanManageRoles so there is a way back.
            request = request with { CanManageRoles = true };
        }

        ApplyPermissions(role, request);
        await _dbContext.SaveChangesAsync(cancellationToken);

        return Ok(MapLinkshellRoleDto(role));
    }

    [HttpPost("linkshells/{linkshellId:int}/roles/{roleId:int}/delete")]
    public async Task<IActionResult> DeleteLinkshellRoleAsync(
        int linkshellId,
        int roleId,
        CancellationToken cancellationToken)
    {
        var appUser = await ResolveAppUserAsync(cancellationToken);
        if (appUser is null)
        {
            return Unauthorized(new { error = "Sign in to delete a linkshell role." });
        }

        var membership = await GetMembershipAsync(appUser.Id, linkshellId, cancellationToken);
        if (!await CanAsync(membership, r => r.CanManageRoles, cancellationToken))
        {
            return Forbid();
        }

        var role = await _dbContext.LinkshellRoles
            .FirstOrDefaultAsync(r => r.Id == roleId && r.LinkshellId == linkshellId, cancellationToken);
        if (role is null)
        {
            return NotFound(new { error = "The role was not found." });
        }

        if (role.IsSystem)
        {
            return BadRequest(new { error = "System roles cannot be deleted." });
        }

        var inUse = await _dbContext.AppUserLinkshells
            .AnyAsync(link => link.LinkshellId == linkshellId && link.Rank == role.Name, cancellationToken);
        if (inUse)
        {
            return BadRequest(new { error = "Members still have this role. Reassign them first." });
        }

        _dbContext.LinkshellRoles.Remove(role);
        await _dbContext.SaveChangesAsync(cancellationToken);

        return Ok(new { success = true });
    }

    [HttpPost("linkshells")]
    public async Task<IActionResult> CreateLinkshellAsync(
        [FromBody] ActivityCreateLinkshellRequest request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
        {
            return BadRequest(new { error = "Linkshell name is required." });
        }

        var appUser = await ResolveAppUserAsync(cancellationToken);
        if (appUser is null)
        {
            return Unauthorized(new
            {
                error = "Sign in with ASP.NET Identity or provide a Discord bearer token to create a linkshell."
            });
        }

        var trimmedName = request.Name.Trim();
        var duplicateLinkshell = await _dbContext.Linkshells
            .AnyAsync(
                linkshell => linkshell.AppUserId == appUser.Id && linkshell.LinkshellName == trimmedName,
                cancellationToken);

        if (duplicateLinkshell)
        {
            return BadRequest(new { error = "A linkshell with that name already exists for the current app user." });
        }

        var linkshell = new Linkshell
        {
            AppUserId = appUser.Id,
            LinkshellName = trimmedName,
            Details = request.Details?.Trim(),
            Status = "Active"
        };

        _dbContext.Linkshells.Add(linkshell);
        await _dbContext.SaveChangesAsync(cancellationToken);

        _dbContext.AppUserLinkshells.Add(new AppUserLinkshell
        {
            AppUserId = appUser.Id,
            LinkshellId = linkshell.Id,
            CharacterName = appUser.CharacterName ?? appUser.UserName,
            Rank = "Leader",
            Status = "Active",
            LinkshellDkp = 0,
            DateJoined = DateTime.UtcNow
        });

        appUser.PrimaryLinkshellId ??= linkshell.Id;
        appUser.PrimaryLinkshellName ??= linkshell.LinkshellName;

        await _dbContext.SaveChangesAsync(cancellationToken);
        await _userManager.UpdateAsync(appUser);

        return Ok(new { success = true, linkshellId = linkshell.Id });
    }

    [HttpGet("linkshells/{linkshellId:int}")]
    public async Task<IActionResult> GetLinkshellDetailAsync(int linkshellId, CancellationToken cancellationToken)
    {
        var appUser = await ResolveAppUserAsync(cancellationToken);
        if (appUser is null)
        {
            return Unauthorized(new
            {
                error = "Sign in with ASP.NET Identity or provide a Discord bearer token to load linkshell details."
            });
        }

        var hasAccess = await _dbContext.AppUserLinkshells
            .AnyAsync(link => link.AppUserId == appUser.Id && link.LinkshellId == linkshellId, cancellationToken);

        if (!hasAccess)
        {
            return Forbid();
        }

        var linkshell = await _dbContext.Linkshells
            .Include(item => item.AppUserLinkshells)
            .ThenInclude(link => link.AppUser)
            .AsNoTracking()
            .FirstOrDefaultAsync(item => item.Id == linkshellId, cancellationToken);

        if (linkshell is null)
        {
            return NotFound(new { error = "The selected linkshell was not found." });
        }

        return Ok(new ActivityLinkshellDetailDto(
            linkshell.Id,
            linkshell.LinkshellName ?? "Unknown linkshell",
            linkshell.AppUserLinkshells.Count,
            linkshell.Details,
            linkshell.Status,
            linkshell.AppUserLinkshells
                .OrderBy(link => link.CharacterName)
                .Select(link => new ActivityMemberDto(
                    link.Id,
                    link.AppUserId,
                    link.CharacterName ?? link.AppUser?.UserName ?? "Unknown member",
                    link.Rank,
                    link.Status,
                    link.LinkshellDkp))
                .ToList()));
    }

    [HttpPost("linkshells/{linkshellId:int}/primary")]
    public async Task<IActionResult> SetPrimaryLinkshellAsync(int linkshellId, CancellationToken cancellationToken)
    {
        var appUser = await ResolveAppUserAsync(cancellationToken);
        if (appUser is null)
        {
            return Unauthorized(new
            {
                error = "Sign in with ASP.NET Identity or provide a Discord bearer token to update the primary linkshell."
            });
        }

        var membership = await _dbContext.AppUserLinkshells
            .Include(link => link.Linkshell)
            .AsNoTracking()
            .FirstOrDefaultAsync(link => link.AppUserId == appUser.Id && link.LinkshellId == linkshellId, cancellationToken);

        if (membership?.Linkshell is null)
        {
            return NotFound(new { error = "The selected linkshell membership was not found." });
        }

        appUser.PrimaryLinkshellId = membership.LinkshellId;
        appUser.PrimaryLinkshellName = membership.Linkshell.LinkshellName;

        await _userManager.UpdateAsync(appUser);
        await _dbContext.SaveChangesAsync(cancellationToken);

        return Ok(new { success = true });
    }

    [HttpPost("linkshells/{linkshellId:int}/members/{memberId:int}/remove")]
    public async Task<IActionResult> RemoveMemberAsync(int linkshellId, int memberId, CancellationToken cancellationToken)
    {
        var appUser = await ResolveAppUserAsync(cancellationToken);
        if (appUser is null)
        {
            return Unauthorized(new
            {
                error = "Sign in with ASP.NET Identity or provide a Discord bearer token to remove members."
            });
        }

        var currentMembership = await GetMembershipAsync(appUser.Id, linkshellId, cancellationToken);
        if (!await CanAsync(currentMembership, r => r.CanManageMembers, cancellationToken))
        {
            return Forbid();
        }

        var targetMembership = await _dbContext.AppUserLinkshells
            .FirstOrDefaultAsync(link => link.Id == memberId && link.LinkshellId == linkshellId, cancellationToken);

        if (targetMembership is null)
        {
            return NotFound(new { error = "The selected member was not found." });
        }

        if (string.Equals(targetMembership.AppUserId, appUser.Id, StringComparison.Ordinal))
        {
            return BadRequest(new { error = "Use the website membership tools to leave your own primary linkshell." });
        }

        _dbContext.AppUserLinkshells.Remove(targetMembership);

        if (!string.IsNullOrWhiteSpace(targetMembership.AppUserId))
        {
            var targetUser = await _dbContext.Users.FindAsync(new object?[] { targetMembership.AppUserId }, cancellationToken);
            if (targetUser is not null && targetUser.PrimaryLinkshellId == linkshellId)
            {
                var fallbackMembership = await _dbContext.AppUserLinkshells
                    .Include(link => link.Linkshell)
                    .Where(link => link.AppUserId == targetUser.Id && link.LinkshellId != linkshellId)
                    .OrderBy(link => link.Linkshell!.LinkshellName)
                    .AsNoTracking()
                    .FirstOrDefaultAsync(cancellationToken);

                targetUser.PrimaryLinkshellId = fallbackMembership?.LinkshellId;
                targetUser.PrimaryLinkshellName = fallbackMembership?.Linkshell?.LinkshellName;
            }

            var pendingInvites = await _dbContext.Invites
                .Where(invite => invite.LinkshellId == linkshellId && invite.AppUserId == targetMembership.AppUserId)
                .ToListAsync(cancellationToken);

            if (pendingInvites.Count > 0)
            {
                _dbContext.Invites.RemoveRange(pendingInvites);
            }
        }

        await _dbContext.SaveChangesAsync(cancellationToken);
        return Ok(new { success = true });
    }

    [HttpPost("linkshells/{linkshellId:int}/members/{memberId:int}/role")]
    public async Task<IActionResult> UpdateMemberRoleAsync(
        int linkshellId,
        int memberId,
        [FromBody] ActivityUpdateMemberRoleRequest request,
        CancellationToken cancellationToken)
    {
        var appUser = await ResolveAppUserAsync(cancellationToken);
        if (appUser is null)
        {
            return Unauthorized(new
            {
                error = "Sign in with ASP.NET Identity or provide a Discord bearer token to update member roles."
            });
        }

        var currentMembership = await GetMembershipAsync(appUser.Id, linkshellId, cancellationToken);
        if (!await CanAsync(currentMembership, r => r.CanManageRoles, cancellationToken))
        {
            return Forbid();
        }

        var targetMembership = await _dbContext.AppUserLinkshells
            .FirstOrDefaultAsync(link => link.Id == memberId && link.LinkshellId == linkshellId, cancellationToken);

        if (targetMembership is null)
        {
            return NotFound(new { error = "The selected member was not found." });
        }

        var normalizedRole = request.Role?.Trim();
        if (string.IsNullOrWhiteSpace(normalizedRole))
        {
            return BadRequest(new { error = "A role name is required." });
        }

        await EnsureDefaultRolesAsync(linkshellId, cancellationToken);
        var roleRow = await _dbContext.LinkshellRoles
            .AsNoTracking()
            .FirstOrDefaultAsync(r => r.LinkshellId == linkshellId && r.Name == normalizedRole, cancellationToken);
        if (roleRow is null)
        {
            return BadRequest(new { error = "That role does not exist for this linkshell." });
        }
        normalizedRole = roleRow.Name;

        if (string.Equals(normalizedRole, "Leader", StringComparison.OrdinalIgnoreCase))
        {
            if (string.Equals(targetMembership.AppUserId, appUser.Id, StringComparison.Ordinal))
            {
                return BadRequest(new { error = "You are already the leader of this linkshell." });
            }

            if (string.Equals(currentMembership!.Rank, "Leader", StringComparison.OrdinalIgnoreCase))
            {
                currentMembership.Rank = "Officer";
            }
            targetMembership.Rank = "Leader";
        }
        else
        {
            if (string.Equals(targetMembership.AppUserId, appUser.Id, StringComparison.Ordinal))
            {
                return BadRequest(new { error = "Leaders cannot change their own role without transferring leadership." });
            }

            targetMembership.Rank = normalizedRole;
        }

        await _dbContext.SaveChangesAsync(cancellationToken);
        return Ok(new { success = true });
    }

    [HttpPost("linkshells/{linkshellId:int}/update")]
    public async Task<IActionResult> UpdateLinkshellAsync(
        int linkshellId,
        [FromBody] ActivityUpdateLinkshellRequest request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
        {
            return BadRequest(new { error = "Linkshell name is required." });
        }

        var appUser = await ResolveAppUserAsync(cancellationToken);
        if (appUser is null)
        {
            return Unauthorized(new
            {
                error = "Sign in with ASP.NET Identity or provide a Discord bearer token to update the linkshell."
            });
        }

        var membership = await GetMembershipAsync(appUser.Id, linkshellId, cancellationToken);
        if (!await CanAsync(membership, r => r.CanCustomizeLinkshell, cancellationToken))
        {
            return Forbid();
        }

        var linkshell = await _dbContext.Linkshells.FirstOrDefaultAsync(item => item.Id == linkshellId, cancellationToken);
        if (linkshell is null)
        {
            return NotFound(new { error = "The selected linkshell was not found." });
        }

        var trimmedName = request.Name.Trim();
        var duplicate = await _dbContext.Linkshells
            .AnyAsync(
                item => item.Id != linkshellId &&
                        item.AppUserId == linkshell.AppUserId &&
                        item.LinkshellName == trimmedName,
                cancellationToken);

        if (duplicate)
        {
            return BadRequest(new { error = "Another linkshell with that name already exists." });
        }

        linkshell.LinkshellName = trimmedName;
        linkshell.Details = request.Details?.Trim();

        if (!string.IsNullOrWhiteSpace(request.LootStructure))
        {
            var requestedStructure = request.LootStructure.Trim();
            if (!IsValidLootStructure(requestedStructure))
            {
                return BadRequest(new { error = "Loot Structure must be Dkp, LootCouncil, or Hybrid." });
            }
            linkshell.LootStructure = NormalizeLootStructure(requestedStructure);
        }

        if (request.EnableHnmSection.HasValue) linkshell.EnableHnmSection = request.EnableHnmSection.Value;
        if (request.EnableMissions.HasValue) linkshell.EnableMissions = request.EnableMissions.Value;
        if (request.EnableAuctions.HasValue) linkshell.EnableAuctions = request.EnableAuctions.Value;
        if (request.EnableToDs.HasValue) linkshell.EnableToDs = request.EnableToDs.Value;
        if (request.EnableEndgame.HasValue) linkshell.EnableEndgame = request.EnableEndgame.Value;
        if (request.EnableEvents.HasValue) linkshell.EnableEvents = request.EnableEvents.Value;
        if (request.EnableDkp.HasValue) linkshell.EnableDkp = request.EnableDkp.Value;
        if (request.EnableItems.HasValue) linkshell.EnableItems = request.EnableItems.Value;
        if (request.EnableRevenue.HasValue) linkshell.EnableRevenue = request.EnableRevenue.Value;
        if (!string.IsNullOrWhiteSpace(request.DkpRoundingIncrement))
        {
            linkshell.DkpRoundingIncrement = NormalizeDkpRounding(request.DkpRoundingIncrement);
        }

        var memberships = await _dbContext.AppUserLinkshells
            .Where(link => link.LinkshellId == linkshellId)
            .AsNoTracking()
            .ToListAsync(cancellationToken);

        var memberIds = memberships
            .Where(link => !string.IsNullOrWhiteSpace(link.AppUserId))
            .Select(link => link.AppUserId!)
            .Distinct()
            .ToList();

        if (memberIds.Count > 0)
        {
            var users = await _dbContext.Users.Where(user => memberIds.Contains(user.Id)).ToListAsync(cancellationToken);
            foreach (var user in users.Where(user => user.PrimaryLinkshellId == linkshellId))
            {
                user.PrimaryLinkshellName = trimmedName;
            }
        }

        await _dbContext.SaveChangesAsync(cancellationToken);
        return Ok(new { success = true });
    }

    [HttpPost("linkshells/{linkshellId:int}/delete")]
    public async Task<IActionResult> DeleteLinkshellAsync(int linkshellId, CancellationToken cancellationToken)
    {
        var appUser = await ResolveAppUserAsync(cancellationToken);
        if (appUser is null)
        {
            return Unauthorized(new
            {
                error = "Sign in with ASP.NET Identity or provide a Discord bearer token to delete the linkshell."
            });
        }

        var membership = await GetMembershipAsync(appUser.Id, linkshellId, cancellationToken);
        if (!IsLeader(membership))
        {
            return Forbid();
        }

        var linkshell = await _dbContext.Linkshells
            .Include(ls => ls.AppUserLinkshells)
            .Include(ls => ls.Events)
                .ThenInclude(evt => evt.Jobs)
            .Include(ls => ls.Events)
                .ThenInclude(evt => evt.AppUserEvents)
            .Include(ls => ls.Events)
                .ThenInclude(evt => evt.EventLootDetails)
            .Include(ls => ls.EventHistories)
                .ThenInclude(history => history.AppUserEventHistories)
            .FirstOrDefaultAsync(ls => ls.Id == linkshellId, cancellationToken);

        if (linkshell is null)
        {
            return NotFound(new { error = "The selected linkshell was not found." });
        }

        if (linkshell.AppUserLinkshells.Count > 1)
        {
            return BadRequest(new
            {
                error = "Remove the remaining members or transfer ownership before deleting this linkshell."
            });
        }

        if (linkshell.Events.Count > 0)
        {
            return BadRequest(new
            {
                error = "Cancel or end all active and queued events before deleting this linkshell."
            });
        }

        var impactedUserIds = linkshell.AppUserLinkshells
            .Where(link => !string.IsNullOrWhiteSpace(link.AppUserId))
            .Select(link => link.AppUserId!)
            .Distinct()
            .ToList();

        if (impactedUserIds.Count > 0)
        {
            var impactedUsers = await _dbContext.Users
                .Where(user => impactedUserIds.Contains(user.Id))
                .ToListAsync(cancellationToken);

            foreach (var user in impactedUsers.Where(user => user.PrimaryLinkshellId == linkshellId))
            {
                var fallback = await _dbContext.AppUserLinkshells
                    .Include(link => link.Linkshell)
                    .Where(link => link.AppUserId == user.Id && link.LinkshellId != linkshellId)
                    .OrderBy(link => link.Linkshell!.LinkshellName)
                    .AsNoTracking()
                    .FirstOrDefaultAsync(cancellationToken);

                user.PrimaryLinkshellId = fallback?.LinkshellId;
                user.PrimaryLinkshellName = fallback?.Linkshell?.LinkshellName;
            }
        }

        var pendingInvites = await _dbContext.Invites
            .Where(invite => invite.LinkshellId == linkshellId)
            .ToListAsync(cancellationToken);

        if (pendingInvites.Count > 0)
        {
            _dbContext.Invites.RemoveRange(pendingInvites);
        }

        _dbContext.AppUserLinkshells.RemoveRange(linkshell.AppUserLinkshells);
        _dbContext.Jobs.RemoveRange(linkshell.Events.SelectMany(evt => evt.Jobs));
        _dbContext.AppUserEvents.RemoveRange(linkshell.Events.SelectMany(evt => evt.AppUserEvents));
        _dbContext.EventLootDetails.RemoveRange(linkshell.Events.SelectMany(evt => evt.EventLootDetails));
        _dbContext.Events.RemoveRange(linkshell.Events);
        _dbContext.AppUserEventHistories.RemoveRange(linkshell.EventHistories.SelectMany(history => history.AppUserEventHistories));
        _dbContext.EventHistories.RemoveRange(linkshell.EventHistories);
        _dbContext.Linkshells.Remove(linkshell);

        await _dbContext.SaveChangesAsync(cancellationToken);
        return Ok(new { success = true });
    }

    [HttpPost("linkshells/{linkshellId:int}/leave")]
    public async Task<IActionResult> LeaveLinkshellAsync(int linkshellId, CancellationToken cancellationToken)
    {
        var appUser = await ResolveAppUserAsync(cancellationToken);
        if (appUser is null)
        {
            return Unauthorized(new
            {
                error = "Sign in with ASP.NET Identity or provide a Discord bearer token to leave the linkshell."
            });
        }

        var membership = await GetMembershipAsync(appUser.Id, linkshellId, cancellationToken);
        if (membership is null)
        {
            return NotFound(new { error = "The selected linkshell membership was not found." });
        }

        var memberCount = await _dbContext.AppUserLinkshells
            .CountAsync(link => link.LinkshellId == linkshellId, cancellationToken);

        if (IsLeader(membership) && memberCount > 1)
        {
            return BadRequest(new { error = "Leaders must transfer ownership or remove remaining members before leaving." });
        }

        if (IsLeader(membership) && memberCount == 1)
        {
            return await DeleteLinkshellAsync(linkshellId, cancellationToken);
        }

        _dbContext.AppUserLinkshells.Remove(membership);

        if (appUser.PrimaryLinkshellId == linkshellId)
        {
            var fallback = await _dbContext.AppUserLinkshells
                .Include(link => link.Linkshell)
                .Where(link => link.AppUserId == appUser.Id && link.LinkshellId != linkshellId)
                .OrderBy(link => link.Linkshell!.LinkshellName)
                .AsNoTracking()
                .FirstOrDefaultAsync(cancellationToken);

            appUser.PrimaryLinkshellId = fallback?.LinkshellId;
            appUser.PrimaryLinkshellName = fallback?.Linkshell?.LinkshellName;
        }

        var eventParticipations = await _dbContext.AppUserEvents
            .Include(participation => participation.Event)
            .Where(participation => participation.AppUserId == appUser.Id && participation.Event!.LinkshellId == linkshellId)
            .ToListAsync(cancellationToken);

        if (eventParticipations.Count > 0)
        {
            var affectedEventIds = eventParticipations.Select(participation => participation.EventId).Distinct().ToList();
            var jobs = await _dbContext.Jobs.Where(job => affectedEventIds.Contains(job.EventId)).ToListAsync(cancellationToken);
            var displayNames = new[]
            {
                appUser.CharacterName,
                appUser.UserName
            }.Where(name => !string.IsNullOrWhiteSpace(name)).ToArray();

            foreach (var participation in eventParticipations)
            {
                var job = jobs.FirstOrDefault(item =>
                    item.EventId == participation.EventId &&
                    item.JobName == participation.JobName &&
                    item.SubJobName == participation.SubJobName);

                if (job is not null)
                {
                    foreach (var name in displayNames)
                    {
                        job.Enlisted.RemoveAll(item => item == name);
                    }

                    if (!string.IsNullOrWhiteSpace(participation.CharacterName))
                    {
                        job.Enlisted.RemoveAll(item => item == participation.CharacterName);
                    }

                    job.SignedUp = job.Enlisted.Count;
                }
            }

            _dbContext.AppUserEvents.RemoveRange(eventParticipations);
        }

        var pendingInvites = await _dbContext.Invites
            .Where(invite => invite.LinkshellId == linkshellId && invite.AppUserId == appUser.Id)
            .ToListAsync(cancellationToken);

        if (pendingInvites.Count > 0)
        {
            _dbContext.Invites.RemoveRange(pendingInvites);
        }

        await _dbContext.SaveChangesAsync(cancellationToken);
        return Ok(new { success = true });
    }
}
