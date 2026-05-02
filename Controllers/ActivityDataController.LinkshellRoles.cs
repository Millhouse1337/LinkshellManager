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
}
