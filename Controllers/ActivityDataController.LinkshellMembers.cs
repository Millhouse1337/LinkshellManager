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

    // Manually set a member's Active/Pending/Inactive status. Gated on
    // CanManageMembers (same as remove). If the linkshell has automatic activity
    // tracking enabled, the computed Active/Inactive may overwrite this on the
    // next recompute — Pending is the only sticky manual state.
    [HttpPost("linkshells/{linkshellId:int}/members/{memberId:int}/status")]
    public async Task<IActionResult> UpdateMemberStatusAsync(
        int linkshellId,
        int memberId,
        [FromBody] ActivityUpdateMemberStatusRequest request,
        CancellationToken cancellationToken)
    {
        var appUser = await ResolveAppUserAsync(cancellationToken);
        if (appUser is null)
        {
            return Unauthorized(new
            {
                error = "Sign in with ASP.NET Identity or provide a Discord bearer token to update member status."
            });
        }

        var currentMembership = await GetMembershipAsync(appUser.Id, linkshellId, cancellationToken);
        if (!await CanAsync(currentMembership, r => r.CanManageMembers, cancellationToken))
        {
            return Forbid();
        }

        var status = request.Status?.Trim();
        var match = ManageTeamViewModel.StatusOptions
            .FirstOrDefault(s => string.Equals(s, status, StringComparison.OrdinalIgnoreCase));
        if (match is null)
        {
            return BadRequest(new { error = "Status must be Active, Pending, or Inactive." });
        }

        var targetMembership = await _dbContext.AppUserLinkshells
            .FirstOrDefaultAsync(link => link.Id == memberId && link.LinkshellId == linkshellId, cancellationToken);
        if (targetMembership is null)
        {
            return NotFound(new { error = "The selected member was not found." });
        }

        targetMembership.Status = match;
        await _dbContext.SaveChangesAsync(cancellationToken);
        return Ok(new { success = true });
    }

    // Officer override of a member's active-credit "Count" on the roster. Stores
    // the typed number as a manual streak override and recomputes Active/Inactive
    // from the linkshell's attendance threshold. The override (and derived status)
    // sticks until the next event recompute clears it. Gated on CanManageMembers.
    [HttpPost("linkshells/{linkshellId:int}/members/{memberId:int}/active-credit-count")]
    public async Task<IActionResult> SetMemberActiveCreditCountAsync(
        int linkshellId,
        int memberId,
        [FromBody] ActivitySetActiveCreditCountRequest request,
        CancellationToken cancellationToken)
    {
        var appUser = await ResolveAppUserAsync(cancellationToken);
        if (appUser is null)
        {
            return Unauthorized(new { error = "Sign in to update active credit." });
        }

        var currentMembership = await GetMembershipAsync(appUser.Id, linkshellId, cancellationToken);
        if (!await CanAsync(currentMembership, r => r.CanManageMembers, cancellationToken))
        {
            return Forbid();
        }

        var count = request.Count < 0 ? 0 : request.Count;
        var isAbsent = string.Equals(request.StreakType, "absent", StringComparison.OrdinalIgnoreCase);

        var targetMembership = await _dbContext.AppUserLinkshells
            .FirstOrDefaultAsync(link => link.Id == memberId && link.LinkshellId == linkshellId, cancellationToken);
        if (targetMembership is null)
        {
            return NotFound(new { error = "The selected member was not found." });
        }

        var thresholds = await _dbContext.Linkshells
            .Where(l => l.Id == linkshellId)
            .Select(l => new { l.ActiveAfterAttendances, l.InactiveAfterAbsences })
            .FirstOrDefaultAsync(cancellationToken);
        var activeAfter = thresholds?.ActiveAfterAttendances ?? 1;
        if (activeAfter < 1) { activeAfter = 1; }
        var inactiveAfter = thresholds?.InactiveAfterAbsences ?? 1;
        if (inactiveAfter < 1) { inactiveAfter = 1; }

        if (isAbsent)
        {
            // Absence override (red number): drives Inactive once the absence
            // threshold is reached. Clears any credit override (mutually exclusive).
            targetMembership.ManualAbsentStreak = count;
            targetMembership.ManualActiveCreditStreak = null;
            targetMembership.Status = count >= inactiveAfter ? "Inactive" : "Active";
        }
        else
        {
            // Active-credit override (green number): Active once they've hit the
            // attendance requirement, else Inactive. Clears any absence override.
            targetMembership.ManualActiveCreditStreak = count;
            targetMembership.ManualAbsentStreak = null;
            targetMembership.Status = count >= activeAfter ? "Active" : "Inactive";
        }
        await _dbContext.SaveChangesAsync(cancellationToken);
        return Ok(new { success = true });
    }
}
