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

        // Only a Leader may remove another Leader. Officers (with CanManageMembers)
        // must not be able to oust the Leader, otherwise they could leave the
        // linkshell leaderless and escalate themselves into the role.
        var targetIsLeader = string.Equals(targetMembership.Rank, "Leader", StringComparison.OrdinalIgnoreCase);
        var actorIsLeader = string.Equals(currentMembership!.Rank, "Leader", StringComparison.OrdinalIgnoreCase);
        if (targetIsLeader && !actorIsLeader)
        {
            return Forbid();
        }

        if (targetIsLeader)
        {
            var otherLeaderExists = await _dbContext.AppUserLinkshells
                .AnyAsync(link =>
                    link.LinkshellId == linkshellId &&
                    link.Id != targetMembership.Id &&
                    link.Rank != null &&
                    link.Rank.ToLower() == "leader",
                    cancellationToken);

            if (!otherLeaderExists)
            {
                return BadRequest(new { error = "Promote another member to Leader before removing the current Leader." });
            }
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

            if (string.Equals(currentMembership!.Rank, LinkshellRanks.Leader, StringComparison.OrdinalIgnoreCase))
            {
                currentMembership.Rank = LinkshellRanks.Officer;
            }
            targetMembership.Rank = LinkshellRanks.Leader;
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
}
