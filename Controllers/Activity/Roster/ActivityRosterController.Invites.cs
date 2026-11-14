using System.Globalization;
using System.Net.Http.Headers;
using LinkshellManagerDiscordApp.Data;
using LinkshellManagerDiscordApp.Models;
using LinkshellManagerDiscordApp.Services;
using LinkshellManagerDiscordApp.ViewModels;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.EntityFrameworkCore;
using NodaTime;

namespace LinkshellManagerDiscordApp.Controllers;

public sealed partial class ActivityRosterController
{
    // Paginated, searchable browse of every LSM user eligible to be invited to this
    // linkshell (the invite card's "Entire LSM Server": anyone who has used the app),
    // minus current members, pending invites/join-requests, and the caller.
    // Lets leaders pick from a list instead of having to type a name. No Discord
    // bot required — these are users already in our database.
    [HttpGet("players/browse")]
    public async Task<IActionResult> BrowsePlayersAsync(
        [FromQuery] int linkshellId,
        [FromQuery] string? query,
        [FromQuery] int page,
        [FromQuery] int pageSize,
        CancellationToken cancellationToken)
    {
        if (linkshellId <= 0)
        {
            return BadRequest(new { error = "A linkshell selection is required." });
        }

        var appUser = await ResolveAppUserAsync(cancellationToken);
        if (appUser is null)
        {
            return Unauthorized(new
            {
                error = "Sign in with ASP.NET Identity or provide a Discord bearer token to browse players."
            });
        }

        var membership = await GetMembershipAsync(appUser.Id, linkshellId, cancellationToken);
        if (!await CanAsync(membership, r => r.CanManageInvites, cancellationToken))
        {
            return Forbid();
        }

        // "Not in your Discord server" means the linkshell's server, set in Configurations.
        var result = await _inviteCandidates.BrowseAsync(
            linkshellId,
            appUser.Id,
            membership?.Linkshell?.DiscordGuildId,
            query,
            page,
            pageSize,
            cancellationToken);

        var items = result.Items
            .Select(candidate => new ActivityUserSearchResultDto(
                candidate.Id,
                candidate.DisplayName,
                candidate.UserName,
                candidate.PrimaryLinkshellName,
                candidate.InDiscordServer))
            .ToList();

        return Ok(new { items, total = result.Total, page = result.Page, pageSize = result.PageSize });
    }

    [HttpPost("linkshells/{linkshellId:int}/invites")]
    public async Task<IActionResult> SendInviteAsync(
        int linkshellId,
        [FromBody] ActivitySendInviteRequest request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.AppUserId))
        {
            return BadRequest(new { error = "A target app user is required." });
        }

        var appUser = await ResolveAppUserAsync(cancellationToken);
        if (appUser is null)
        {
            return Unauthorized(new
            {
                error = "Sign in with ASP.NET Identity or provide a Discord bearer token to send invites."
            });
        }

        var membership = await GetMembershipAsync(appUser.Id, linkshellId, cancellationToken);
        if (!await CanAsync(membership, r => r.CanManageInvites, cancellationToken))
        {
            return Forbid();
        }

        var targetUser = await _dbContext.Users.FindAsync(new object?[] { request.AppUserId }, cancellationToken);
        if (targetUser is null)
        {
            return NotFound(new { error = "The selected player was not found." });
        }

        var existingMembership = await _dbContext.AppUserLinkshells
            .AnyAsync(link => link.LinkshellId == linkshellId && link.AppUserId == request.AppUserId, cancellationToken);

        if (existingMembership)
        {
            return BadRequest(new { error = "That player is already a member of the selected linkshell." });
        }

        var existingInvite = await _dbContext.Invites
            .AnyAsync(
                invite => invite.LinkshellId == linkshellId &&
                          invite.AppUserId == request.AppUserId &&
                          (invite.Status == PendingInviteStatus || invite.Status == PendingJoinRequestStatus),
                cancellationToken);

        if (existingInvite)
        {
            return BadRequest(new { error = "A pending invite or join request already exists for that player." });
        }

        // Auto-join: inviting a player adds them straight to the roster — no accept
        // step. (Discord-only people without an LSM account are handled separately
        // in SendDiscordInviteAsync and auto-join on first sign-in.)
        _dbContext.AppUserLinkshells.Add(LinkshellMembership.NewMember(targetUser, linkshellId, DateTime.UtcNow));
        targetUser.PrimaryLinkshellId ??= linkshellId;

        try
        {
            await _dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (UniqueRaceRetry.IsDuplicateMembership(ex))
        {
            // They joined between our check above and this save (another officer, or a Discord
            // sign-up); the one-membership-per-linkshell index said no.
            return BadRequest(new { error = "That player is already a member of the selected linkshell." });
        }
        return Ok(new { success = true });
    }

    // The invite card's "From My Discord Server": members of the linkshell's Discord server
    // (set in Configurations) who can be added to the linkshell — including people who have
    // never used LSM (they get a Discord-keyed invite and auto-join on first sign-in).
    [HttpGet("linkshells/{linkshellId:int}/discord-roster")]
    public async Task<IActionResult> GetDiscordRosterCandidatesAsync(
        int linkshellId,
        CancellationToken cancellationToken)
    {
        var appUser = await ResolveAppUserAsync(cancellationToken);
        if (appUser is null)
        {
            return Unauthorized(new
            {
                error = "Sign in with ASP.NET Identity or provide a Discord bearer token to load the Discord roster."
            });
        }

        var membership = await GetMembershipAsync(appUser.Id, linkshellId, cancellationToken);
        if (!await CanAsync(membership, r => r.CanManageInvites, cancellationToken))
        {
            return Forbid();
        }

        var guildId = membership?.Linkshell?.DiscordGuildId;
        var candidates = await _inviteCandidates.GetDiscordRosterCandidatesAsync(linkshellId, guildId, cancellationToken);

        return Ok(candidates
            .Select(candidate => new ActivityDiscordRosterCandidateDto(
                candidate.DiscordUserId,
                candidate.DisplayName,
                candidate.AvatarUrl ?? string.Empty,
                candidate.HasAccount))
            .ToList());
    }

    // Adds a member of the linkshell's Discord server (set in Configurations). If they
    // already have an LSM account they're added straight to the roster (auto-join); if
    // not, a Discord-keyed invite is stored that auto-joins them on first sign-in.
    [HttpPost("linkshells/{linkshellId:int}/invites/discord")]
    public async Task<IActionResult> SendDiscordInviteAsync(
        int linkshellId,
        [FromBody] ActivityDiscordInviteRequest request,
        CancellationToken cancellationToken)
    {
        var appUser = await ResolveAppUserAsync(cancellationToken);
        if (appUser is null)
        {
            return Unauthorized(new
            {
                error = "Sign in with ASP.NET Identity or provide a Discord bearer token to add members."
            });
        }

        var membership = await GetMembershipAsync(appUser.Id, linkshellId, cancellationToken);
        if (!await CanAsync(membership, r => r.CanManageInvites, cancellationToken))
        {
            return Forbid();
        }

        var result = await _inviteCandidates.AddDiscordMemberAsync(
            linkshellId, membership?.Linkshell?.DiscordGuildId, request.DiscordUserId, cancellationToken);

        return result.Success
            ? Ok(new { success = true })
            : BadRequest(new { error = result.Error });
    }

    // A player looking for a linkshell to ask to join. Only names, descriptions and member counts
    // come back (JoinRequestService.SearchAsync says which linkshells are listed).
    [HttpGet("linkshells/search")]
    public async Task<IActionResult> SearchLinkshellsAsync(
        [FromQuery] string? query,
        [FromServices] JoinRequestService joinRequests,
        CancellationToken cancellationToken)
    {
        var appUser = await ResolveAppUserAsync(cancellationToken);
        if (appUser is null)
        {
            return Unauthorized(new
            {
                error = "Sign in with ASP.NET Identity or provide a Discord bearer token to search linkshells."
            });
        }

        var results = await joinRequests.SearchAsync(appUser.Id, query, GetRequestGuildId(), cancellationToken);
        return Ok(results
            .Select(result => new ActivityLinkshellSearchResultDto(result.Id, result.Name, result.Details, result.MemberCount))
            .ToList());
    }

    [HttpPost("linkshells/{linkshellId:int}/join-request")]
    public async Task<IActionResult> RequestJoinLinkshellAsync(
        int linkshellId,
        [FromBody(EmptyBodyBehavior = EmptyBodyBehavior.Allow)] ActivityJoinRequestInput? request,
        [FromServices] JoinRequestService joinRequests,
        CancellationToken cancellationToken)
    {
        var appUser = await ResolveAppUserAsync(cancellationToken);
        if (appUser is null)
        {
            return Unauthorized(new
            {
                error = "Sign in with ASP.NET Identity or provide a Discord bearer token to request linkshell access."
            });
        }
        if (appUser.IsPlaceholder)
        {
            return Forbid();
        }

        var lockInfo = await _dbContext.Linkshells
            .AsNoTracking()
            .Where(item => item.Id == linkshellId)
            .Select(item => new { item.LockToDiscordGuild, item.DiscordGuildId })
            .FirstOrDefaultAsync(cancellationToken);
        if (lockInfo is null)
        {
            return NotFound(new { error = "That linkshell was not found." });
        }

        // A linkshell locked to its Discord server takes requests only from people in that
        // server. Asking leads to JOINING, so the website gets no pass (like a DKP claim).
        var lockGuildId = lockInfo.LockToDiscordGuild ? lockInfo.DiscordGuildId : null;
        var accessible = await FilterAccessibleLinkshellIdsAsync(
            new[] { (linkshellId, lockGuildId) }, cancellationToken, lockAppliesOnWebsite: true);
        if (!accessible.Contains(linkshellId))
        {
            return StatusCode(StatusCodes.Status403Forbidden, new
            {
                error = "That linkshell only takes requests from its own Discord server."
            });
        }

        var result = await joinRequests.RequestAsync(linkshellId, appUser.Id, request?.Message, cancellationToken);
        return result.Outcome switch
        {
            JoinRequestService.Outcome.Done => Ok(new { success = true }),
            JoinRequestService.Outcome.NotFound => NotFound(new { error = result.Error }),
            _ => BadRequest(new { error = result.Error })
        };
    }

    // The player takes back their own request while it waits.
    [HttpPost("join-requests/{inviteId:int}/cancel")]
    public async Task<IActionResult> CancelJoinRequestAsync(
        int inviteId,
        [FromServices] JoinRequestService joinRequests,
        CancellationToken cancellationToken)
    {
        var appUser = await ResolveAppUserAsync(cancellationToken);
        if (appUser is null)
        {
            return Unauthorized(new
            {
                error = "Sign in with ASP.NET Identity or provide a Discord bearer token to cancel join requests."
            });
        }

        var result = await joinRequests.CancelAsync(inviteId, appUser.Id, cancellationToken);
        return result.Outcome switch
        {
            JoinRequestService.Outcome.Done => Ok(new { success = true }),
            JoinRequestService.Outcome.NotFound => NotFound(new { error = result.Error }),
            _ => BadRequest(new { error = result.Error })
        };
    }

    [HttpPost("invites/{inviteId:int}/revoke")]
    public async Task<IActionResult> RevokeInviteAsync(int inviteId, CancellationToken cancellationToken)
    {
        var appUser = await ResolveAppUserAsync(cancellationToken);
        if (appUser is null)
        {
            return Unauthorized(new
            {
                error = "Sign in with ASP.NET Identity or provide a Discord bearer token to revoke invites."
            });
        }

        var invite = await _dbContext.Invites
            .Include(item => item.Linkshell)
            .FirstOrDefaultAsync(item => item.Id == inviteId && item.Status == PendingInviteStatus, cancellationToken);

        if (invite is null)
        {
            return NotFound(new { error = "The selected invite was not found." });
        }

        var membership = await GetMembershipAsync(appUser.Id, invite.LinkshellId, cancellationToken);
        if (!await CanAsync(membership, r => r.CanManageInvites, cancellationToken))
        {
            return Forbid();
        }

        _dbContext.Invites.Remove(invite);
        await _dbContext.SaveChangesAsync(cancellationToken);

        return Ok(new { success = true });
    }

    [HttpPost("invites/{inviteId:int}/accept")]
    public async Task<IActionResult> AcceptInviteAsync(int inviteId, CancellationToken cancellationToken)
    {
        var appUser = await ResolveAppUserAsync(cancellationToken);
        if (appUser is null)
        {
            return Unauthorized(new
            {
                error = "Sign in with ASP.NET Identity or provide a Discord bearer token to accept invites."
            });
        }

        var invite = await _dbContext.Invites
            .Include(item => item.Linkshell)
            .FirstOrDefaultAsync(item => item.Id == inviteId && item.AppUserId == appUser.Id && item.Status == PendingInviteStatus, cancellationToken);

        if (invite is null)
        {
            return NotFound(new { error = "The selected invite was not found." });
        }

        var existingMembership = await _dbContext.AppUserLinkshells
            .AnyAsync(link => link.LinkshellId == invite.LinkshellId && link.AppUserId == appUser.Id, cancellationToken);

        if (!existingMembership)
        {
            _dbContext.AppUserLinkshells.Add(LinkshellMembership.NewMember(appUser, invite.LinkshellId, DateTime.UtcNow));
        }

        appUser.PrimaryLinkshellId ??= invite.LinkshellId;

        _dbContext.Invites.Remove(invite);
        try
        {
            await _dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (UniqueRaceRetry.IsDuplicateMembership(ex))
        {
            // A second Accept (a double click) joined them a moment earlier and took the invite
            // with it. They are a member, which is what they asked for.
            return Ok(new { success = true });
        }
        await _userManager.UpdateAsync(appUser);

        return Ok(new { success = true });
    }

    [HttpPost("invites/{inviteId:int}/decline")]
    public async Task<IActionResult> DeclineInviteAsync(int inviteId, CancellationToken cancellationToken)
    {
        var appUser = await ResolveAppUserAsync(cancellationToken);
        if (appUser is null)
        {
            return Unauthorized(new
            {
                error = "Sign in with ASP.NET Identity or provide a Discord bearer token to decline invites."
            });
        }

        var invite = await _dbContext.Invites
            .FirstOrDefaultAsync(item => item.Id == inviteId && item.AppUserId == appUser.Id && item.Status == PendingInviteStatus, cancellationToken);

        if (invite is null)
        {
            return NotFound(new { error = "The selected invite was not found." });
        }

        _dbContext.Invites.Remove(invite);
        await _dbContext.SaveChangesAsync(cancellationToken);
        return Ok(new { success = true });
    }

    // Approve: the player joins as a Member, the same way an accepted invite joins them.
    [HttpPost("join-requests/{inviteId:int}/approve")]
    public Task<IActionResult> ApproveJoinRequestAsync(
        int inviteId,
        [FromServices] JoinRequestService joinRequests,
        CancellationToken cancellationToken) =>
        AnswerJoinRequestAsync(inviteId, "approve", joinRequests, joinRequests.ApproveAsync, cancellationToken);

    // Deny. The request stays on the player's list as denied.
    [HttpPost("join-requests/{inviteId:int}/decline")]
    public Task<IActionResult> DeclineJoinRequestAsync(
        int inviteId,
        [FromServices] JoinRequestService joinRequests,
        CancellationToken cancellationToken) =>
        AnswerJoinRequestAsync(inviteId, "decline", joinRequests, joinRequests.DenyAsync, cancellationToken);

    // Who may answer: anyone who may send invites in that linkshell (the invite section's rule).
    private async Task<IActionResult> AnswerJoinRequestAsync(
        int inviteId,
        string verb,
        JoinRequestService joinRequests,
        Func<int, string, CancellationToken, Task<JoinRequestService.Result>> answer,
        CancellationToken cancellationToken)
    {
        var appUser = await ResolveAppUserAsync(cancellationToken);
        if (appUser is null)
        {
            return Unauthorized(new
            {
                error = $"Sign in with ASP.NET Identity or provide a Discord bearer token to {verb} join requests."
            });
        }

        var linkshellId = await joinRequests.LinkshellOfAsync(inviteId, cancellationToken);
        if (linkshellId is null)
        {
            return NotFound(new { error = "The selected join request was not found." });
        }

        var membership = await GetMembershipAsync(appUser.Id, linkshellId.Value, cancellationToken);
        if (!await CanAsync(membership, r => r.CanManageInvites, cancellationToken))
        {
            return Forbid();
        }

        var result = await answer(inviteId, appUser.Id, cancellationToken);
        return result.Outcome switch
        {
            JoinRequestService.Outcome.Done => Ok(new { success = true }),
            // They joined some other way first: what the request asked for has happened.
            JoinRequestService.Outcome.AlreadyMember => Ok(new { success = true, alreadyMember = true }),
            JoinRequestService.Outcome.NotFound => NotFound(new { error = result.Error }),
            _ => BadRequest(new { error = result.Error })
        };
    }
}
