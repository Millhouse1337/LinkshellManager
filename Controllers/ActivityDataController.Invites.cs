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
    [HttpGet("players/search")]
    public async Task<IActionResult> SearchPlayersAsync(
        [FromQuery] string? query,
        [FromQuery] int linkshellId,
        CancellationToken cancellationToken)
    {
        if (linkshellId <= 0)
        {
            return BadRequest(new { error = "A linkshell selection is required." });
        }

        if (string.IsNullOrWhiteSpace(query) || query.Trim().Length < 2)
        {
            return Ok(Array.Empty<ActivityUserSearchResultDto>());
        }

        var appUser = await ResolveAppUserAsync(cancellationToken);
        if (appUser is null)
        {
            return Unauthorized(new
            {
                error = "Sign in with ASP.NET Identity or provide a Discord bearer token to search players."
            });
        }

        var membership = await GetMembershipAsync(appUser.Id, linkshellId, cancellationToken);
        if (!await CanAsync(membership, r => r.CanManageInvites, cancellationToken))
        {
            return Forbid();
        }

        var normalizedQuery = query?.Trim();
        var existingMemberIds = await _dbContext.AppUserLinkshells
            .Where(link => link.LinkshellId == linkshellId && link.AppUserId != null)
            .Select(link => link.AppUserId!)
            .ToListAsync(cancellationToken);

        var pendingInviteIds = await _dbContext.Invites
            .Where(invite =>
                invite.LinkshellId == linkshellId &&
                (invite.Status == PendingInviteStatus || invite.Status == PendingJoinRequestStatus))
            .Select(invite => invite.AppUserId)
            .ToListAsync(cancellationToken);

        // When the linkshell is locked to a Discord server, only surface people
        // who are actually in that server (null = unlocked or bot unavailable).
        var guildEligibleIds = await _inviteCandidates.TryGetGuildEligibleAppUserIdsAsync(
            membership?.Linkshell?.DiscordGuildId, cancellationToken);

        var eligible = _dbContext.Users
            .Where(user =>
                user.Id != appUser.Id &&
                !existingMemberIds.Contains(user.Id) &&
                !pendingInviteIds.Contains(user.Id) &&
                (
                    (user.CharacterName != null && EF.Functions.ILike(user.CharacterName, $"%{normalizedQuery}%")) ||
                    EF.Functions.ILike(user.UserName!, $"%{normalizedQuery}%")
                ));

        if (guildEligibleIds is not null)
        {
            eligible = eligible.Where(user => guildEligibleIds.Contains(user.Id));
        }

        var results = await eligible
            .OrderBy(user => user.CharacterName ?? user.UserName)
            .Take(10)
            .Select(user => new ActivityUserSearchResultDto(
                user.Id,
                user.CharacterName ?? user.UserName ?? "Unknown member",
                user.UserName,
                user.PrimaryLinkshellName))
            .ToListAsync(cancellationToken);

        return Ok(results);
    }

    // Paginated, searchable, filterable browse of every LSM user eligible to be
    // invited to this linkshell (i.e. anyone who has used the app at least once),
    // minus current members, pending invites/join-requests, and the caller.
    // Lets leaders pick from a list instead of having to type a name. No Discord
    // bot required — these are users already in our database.
    [HttpGet("players/browse")]
    public async Task<IActionResult> BrowsePlayersAsync(
        [FromQuery] int linkshellId,
        [FromQuery] string? query,
        [FromQuery] string? filter,
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
        if (!await CanAsync(membership, r => r.CanManageMembers, cancellationToken))
        {
            return Forbid();
        }

        var result = await _inviteCandidates.BrowseAsync(
            linkshellId,
            appUser.Id,
            membership?.Linkshell?.DiscordGuildId,
            query,
            filter,
            page,
            pageSize,
            cancellationToken);

        // Map to the Activity's DTO so the JSON contract the Angular client reads
        // (id / displayName / userName / primaryLinkshellName) is unchanged.
        var items = result.Items
            .Select(candidate => new ActivityUserSearchResultDto(
                candidate.Id,
                candidate.DisplayName,
                candidate.UserName,
                candidate.PrimaryLinkshellName))
            .ToList();

        return Ok(new { items, total = result.Total, page = result.Page, pageSize = result.PageSize });
    }

    [HttpPost("invites/participants")]
    public async Task<IActionResult> GetParticipantInviteCandidatesAsync(
        [FromBody] ActivityParticipantInviteCandidatesRequest request,
        CancellationToken cancellationToken)
    {
        if (request.LinkshellId <= 0)
        {
            return BadRequest(new { error = "A linkshell selection is required." });
        }

        var appUser = await ResolveAppUserAsync(cancellationToken);
        if (appUser is null)
        {
            return Unauthorized(new
            {
                error = "Sign in with ASP.NET Identity or provide a Discord bearer token to load connected participant invites."
            });
        }

        var membership = await GetMembershipAsync(appUser.Id, request.LinkshellId, cancellationToken);
        if (!await CanAsync(membership, r => r.CanManageInvites, cancellationToken))
        {
            return Forbid();
        }

        var normalizedDiscordUserIds = (request.DiscordUserIds ?? Array.Empty<string>())
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id.Trim())
            .Distinct(StringComparer.Ordinal)
            .Take(25)
            .ToList();

        if (normalizedDiscordUserIds.Count == 0)
        {
            return Ok(Array.Empty<ActivityParticipantInviteCandidateDto>());
        }

        var existingMemberIds = await _dbContext.AppUserLinkshells
            .Where(link => link.LinkshellId == request.LinkshellId && link.AppUserId != null)
            .Select(link => link.AppUserId!)
            .ToListAsync(cancellationToken);

        var pendingInviteIds = await _dbContext.Invites
            .Where(invite =>
                invite.LinkshellId == request.LinkshellId &&
                (invite.Status == PendingInviteStatus || invite.Status == PendingJoinRequestStatus))
            .Select(invite => invite.AppUserId)
            .ToListAsync(cancellationToken);

        var candidates = await _dbContext.DiscordActivityUsers
            .Include(discordUser => discordUser.IdentityUser)
            .Where(discordUser =>
                normalizedDiscordUserIds.Contains(discordUser.DiscordUserId) &&
                discordUser.IdentityUserId != null &&
                discordUser.IdentityUserId != appUser.Id &&
                !existingMemberIds.Contains(discordUser.IdentityUserId) &&
                !pendingInviteIds.Contains(discordUser.IdentityUserId))
            .OrderBy(discordUser => discordUser.IdentityUser!.CharacterName ?? discordUser.IdentityUser!.UserName)
            .AsNoTracking()
            .ToListAsync(cancellationToken);

        var results = candidates
            .GroupBy(discordUser => discordUser.IdentityUserId, StringComparer.Ordinal)
            .Select(group => group.First())
            .Select(discordUser => new ActivityParticipantInviteCandidateDto(
                discordUser.IdentityUserId!,
                discordUser.DiscordUserId,
                discordUser.IdentityUser?.CharacterName ??
                discordUser.IdentityUser?.UserName ??
                discordUser.GlobalName ??
                discordUser.Username,
                discordUser.IdentityUser?.UserName,
                discordUser.IdentityUser?.PrimaryLinkshellName))
            .ToList();

        return Ok(results);
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

        _dbContext.Invites.Add(new Invite
        {
            AppUserId = request.AppUserId,
            LinkshellId = linkshellId,
            Status = PendingInviteStatus
        });

        await _dbContext.SaveChangesAsync(cancellationToken);
        return Ok(new { success = true });
    }

    // Lists members of the linkshell's locked Discord server who can be invited
    // straight from the roster — including people who have never used LSM. Those
    // are sent a Discord-keyed invite and auto-join on first sign-in.
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
        if (!await CanAsync(membership, r => r.CanManageMembers, cancellationToken))
        {
            return Forbid();
        }

        var guildId = membership?.Linkshell?.DiscordGuildId;
        if (string.IsNullOrWhiteSpace(guildId))
        {
            // Not locked to a server — nothing to pull a roster from.
            return Ok(Array.Empty<ActivityDiscordRosterCandidateDto>());
        }

        var members = await _discordIdentityService.TryGetGuildMembersAsync(guildId, cancellationToken);
        if (members is null || members.Count == 0)
        {
            return Ok(Array.Empty<ActivityDiscordRosterCandidateDto>());
        }

        var rosterIds = members.Select(member => member.Id).ToList();

        // Build the set of Discord IDs to hide: anyone already in the linkshell
        // or already holding a pending invite/join request for it.
        var memberAppUserIds = await _dbContext.AppUserLinkshells
            .Where(link => link.LinkshellId == linkshellId && link.AppUserId != null)
            .Select(link => link.AppUserId!)
            .ToListAsync(cancellationToken);

        var pendingInvites = await _dbContext.Invites
            .Where(invite => invite.LinkshellId == linkshellId &&
                             (invite.Status == PendingInviteStatus || invite.Status == PendingJoinRequestStatus))
            .Select(invite => new { invite.AppUserId, invite.DiscordUserId })
            .ToListAsync(cancellationToken);

        var excludedAppUserIds = memberAppUserIds
            .Concat(pendingInvites.Where(i => i.AppUserId != null).Select(i => i.AppUserId!))
            .Distinct()
            .ToList();

        var excludedDiscordIds = await _dbContext.DiscordActivityUsers
            .Where(discordUser => discordUser.IdentityUserId != null &&
                                  excludedAppUserIds.Contains(discordUser.IdentityUserId))
            .Select(discordUser => discordUser.DiscordUserId)
            .ToListAsync(cancellationToken);

        var excluded = new HashSet<string>(excludedDiscordIds, StringComparer.Ordinal);
        foreach (var pending in pendingInvites)
        {
            if (!string.IsNullOrWhiteSpace(pending.DiscordUserId))
            {
                excluded.Add(pending.DiscordUserId);
            }
        }

        // Which roster members already have an LSM account (drives a normal vs
        // Discord-keyed invite, and a small badge in the UI).
        var existingAccountIds = await _dbContext.DiscordActivityUsers
            .Where(discordUser => discordUser.IdentityUserId != null && rosterIds.Contains(discordUser.DiscordUserId))
            .Select(discordUser => discordUser.DiscordUserId)
            .ToListAsync(cancellationToken);
        var hasAccount = new HashSet<string>(existingAccountIds, StringComparer.Ordinal);

        var candidates = members
            .Where(member => !excluded.Contains(member.Id))
            .OrderBy(member => member.GlobalName ?? member.Username)
            .Take(500)
            .Select(member => new ActivityDiscordRosterCandidateDto(
                member.Id,
                string.IsNullOrWhiteSpace(member.GlobalName) ? member.Username : member.GlobalName!,
                DiscordIdentityService.BuildAvatarUrl(member.Id, member.Avatar),
                hasAccount.Contains(member.Id)))
            .ToList();

        return Ok(candidates);
    }

    // Invites a member of the linkshell's locked Discord server. If they already
    // have an LSM account, this is a normal pending invite they accept; if not,
    // it's a Discord-keyed invite that auto-joins them on their first sign-in.
    [HttpPost("linkshells/{linkshellId:int}/invites/discord")]
    public async Task<IActionResult> SendDiscordInviteAsync(
        int linkshellId,
        [FromBody] ActivityDiscordInviteRequest request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.DiscordUserId))
        {
            return BadRequest(new { error = "A Discord user is required." });
        }
        var discordUserId = request.DiscordUserId.Trim();

        var appUser = await ResolveAppUserAsync(cancellationToken);
        if (appUser is null)
        {
            return Unauthorized(new
            {
                error = "Sign in with ASP.NET Identity or provide a Discord bearer token to send invites."
            });
        }

        var membership = await GetMembershipAsync(appUser.Id, linkshellId, cancellationToken);
        if (!await CanAsync(membership, r => r.CanManageMembers, cancellationToken))
        {
            return Forbid();
        }

        var guildId = membership?.Linkshell?.DiscordGuildId;
        if (string.IsNullOrWhiteSpace(guildId))
        {
            return BadRequest(new { error = "This linkshell isn't locked to a Discord server." });
        }

        // Verify the target is actually in the server (don't trust a raw ID from
        // the client) and grab their display name for the invite snapshot.
        var members = await _discordIdentityService.TryGetGuildMembersAsync(guildId, cancellationToken);
        if (members is null)
        {
            return BadRequest(new { error = "Couldn't read the linkshell's Discord server. Make sure the bot is in it." });
        }

        var target = members.FirstOrDefault(member => member.Id == discordUserId);
        if (target is null)
        {
            return BadRequest(new { error = "That person isn't a member of the linkshell's Discord server." });
        }

        var displayName = string.IsNullOrWhiteSpace(target.GlobalName) ? target.Username : target.GlobalName!;

        // Already an LSM user? Send a normal invite they accept.
        var existingDiscordUser = await _dbContext.DiscordActivityUsers
            .FirstOrDefaultAsync(
                discordUser => discordUser.DiscordUserId == discordUserId && discordUser.IdentityUserId != null,
                cancellationToken);

        if (existingDiscordUser is not null)
        {
            var targetAppUserId = existingDiscordUser.IdentityUserId!;

            var alreadyMember = await _dbContext.AppUserLinkshells
                .AnyAsync(link => link.LinkshellId == linkshellId && link.AppUserId == targetAppUserId, cancellationToken);
            if (alreadyMember)
            {
                return BadRequest(new { error = "That player is already a member of this linkshell." });
            }

            var existingInvite = await _dbContext.Invites
                .AnyAsync(invite => invite.LinkshellId == linkshellId &&
                                    invite.AppUserId == targetAppUserId &&
                                    (invite.Status == PendingInviteStatus || invite.Status == PendingJoinRequestStatus),
                    cancellationToken);
            if (existingInvite)
            {
                return BadRequest(new { error = "A pending invite or join request already exists for that player." });
            }

            _dbContext.Invites.Add(new Invite
            {
                AppUserId = targetAppUserId,
                LinkshellId = linkshellId,
                Status = PendingInviteStatus
            });
        }
        else
        {
            var existingDiscordInvite = await _dbContext.Invites
                .AnyAsync(invite => invite.LinkshellId == linkshellId &&
                                    invite.DiscordUserId == discordUserId &&
                                    invite.Status == PendingInviteStatus,
                    cancellationToken);
            if (existingDiscordInvite)
            {
                return BadRequest(new { error = "That person has already been invited." });
            }

            _dbContext.Invites.Add(new Invite
            {
                AppUserId = null,
                LinkshellId = linkshellId,
                Status = PendingInviteStatus,
                DiscordUserId = discordUserId,
                DiscordDisplayName = displayName
            });
        }

        await _dbContext.SaveChangesAsync(cancellationToken);
        return Ok(new { success = true });
    }

    [HttpGet("linkshells/search")]
    public async Task<IActionResult> SearchLinkshellsAsync(
        [FromQuery] string? query,
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

        var normalizedQuery = query?.Trim();
        var existingMembershipIds = await _dbContext.AppUserLinkshells
            .Where(link => link.AppUserId == appUser.Id)
            .Select(link => link.LinkshellId)
            .ToListAsync(cancellationToken);

        var pendingRequestIds = await _dbContext.Invites
            .Where(invite => invite.AppUserId == appUser.Id && invite.Status == PendingJoinRequestStatus)
            .Select(invite => invite.LinkshellId)
            .ToListAsync(cancellationToken);

        var memberCounts = await _dbContext.AppUserLinkshells
            .GroupBy(link => link.LinkshellId)
            .Select(group => new { LinkshellId = group.Key, Count = group.Count() })
            .ToDictionaryAsync(item => item.LinkshellId, item => item.Count, cancellationToken);

        var results = await _dbContext.Linkshells
            .Where(linkshell =>
                linkshell.Status == "Active" &&
                !existingMembershipIds.Contains(linkshell.Id) &&
                !pendingRequestIds.Contains(linkshell.Id) &&
                linkshell.LinkshellName != null &&
                (string.IsNullOrWhiteSpace(normalizedQuery) ||
                 EF.Functions.ILike(linkshell.LinkshellName, $"%{normalizedQuery}%")))
            .OrderBy(linkshell => linkshell.LinkshellName)
            .Take(10)
            .AsNoTracking()
            .ToListAsync(cancellationToken);

        return Ok(results.Select(linkshell => new ActivityLinkshellSearchResultDto(
            linkshell.Id,
            linkshell.LinkshellName ?? "Unknown linkshell",
            linkshell.Details,
            memberCounts.GetValueOrDefault(linkshell.Id, 0),
            linkshell.Status)).ToList());
    }

    [HttpPost("linkshells/{linkshellId:int}/join-request")]
    public async Task<IActionResult> RequestJoinLinkshellAsync(int linkshellId, CancellationToken cancellationToken)
    {
        var appUser = await ResolveAppUserAsync(cancellationToken);
        if (appUser is null)
        {
            return Unauthorized(new
            {
                error = "Sign in with ASP.NET Identity or provide a Discord bearer token to request linkshell access."
            });
        }

        var linkshell = await _dbContext.Linkshells
            .AsNoTracking()
            .FirstOrDefaultAsync(item => item.Id == linkshellId && item.Status == "Active", cancellationToken);

        if (linkshell is null)
        {
            return NotFound(new { error = "The selected linkshell was not found." });
        }

        var existingMembership = await _dbContext.AppUserLinkshells
            .AnyAsync(link => link.LinkshellId == linkshellId && link.AppUserId == appUser.Id, cancellationToken);

        if (existingMembership)
        {
            return BadRequest(new { error = "You already belong to that linkshell." });
        }

        var existingRequest = await _dbContext.Invites
            .AnyAsync(invite =>
                invite.LinkshellId == linkshellId &&
                invite.AppUserId == appUser.Id &&
                invite.Status == PendingJoinRequestStatus,
                cancellationToken);

        if (existingRequest)
        {
            return BadRequest(new { error = "A join request is already pending for that linkshell." });
        }

        _dbContext.Invites.Add(new Invite
        {
            AppUserId = appUser.Id,
            LinkshellId = linkshellId,
            Status = PendingJoinRequestStatus
        });

        await _dbContext.SaveChangesAsync(cancellationToken);
        return Ok(new { success = true });
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
            _dbContext.AppUserLinkshells.Add(new AppUserLinkshell
            {
                AppUserId = appUser.Id,
                LinkshellId = invite.LinkshellId,
                LinkshellDkp = 0,
                DateJoined = DateTime.UtcNow,
                CharacterName = appUser.CharacterName ?? appUser.UserName,
                Rank = LinkshellRanks.Member,
                Status = "Active"
            });
        }

        appUser.PrimaryLinkshellId ??= invite.LinkshellId;
        appUser.PrimaryLinkshellName ??= invite.Linkshell?.LinkshellName;

        _dbContext.Invites.Remove(invite);
        await _dbContext.SaveChangesAsync(cancellationToken);
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

    [HttpPost("join-requests/{inviteId:int}/approve")]
    public async Task<IActionResult> ApproveJoinRequestAsync(int inviteId, CancellationToken cancellationToken)
    {
        var appUser = await ResolveAppUserAsync(cancellationToken);
        if (appUser is null)
        {
            return Unauthorized(new
            {
                error = "Sign in with ASP.NET Identity or provide a Discord bearer token to approve join requests."
            });
        }

        var invite = await _dbContext.Invites
            .Include(item => item.Linkshell)
            .Include(item => item.AppUser)
            .FirstOrDefaultAsync(item => item.Id == inviteId && item.Status == PendingJoinRequestStatus, cancellationToken);

        if (invite is null)
        {
            return NotFound(new { error = "The selected join request was not found." });
        }

        var membership = await GetMembershipAsync(appUser.Id, invite.LinkshellId, cancellationToken);
        if (!await CanAsync(membership, r => r.CanManageInvites, cancellationToken))
        {
            return Forbid();
        }

        var existingMembership = await _dbContext.AppUserLinkshells
            .AnyAsync(link => link.LinkshellId == invite.LinkshellId && link.AppUserId == invite.AppUserId, cancellationToken);

        if (!existingMembership)
        {
            _dbContext.AppUserLinkshells.Add(new AppUserLinkshell
            {
                AppUserId = invite.AppUserId,
                LinkshellId = invite.LinkshellId,
                LinkshellDkp = 0,
                DateJoined = DateTime.UtcNow,
                CharacterName = invite.AppUser?.CharacterName ?? invite.AppUser?.UserName,
                Rank = LinkshellRanks.Member,
                Status = "Active"
            });
        }

        if (invite.AppUser is not null)
        {
            invite.AppUser.PrimaryLinkshellId ??= invite.LinkshellId;
            invite.AppUser.PrimaryLinkshellName ??= invite.Linkshell?.LinkshellName;
            await _userManager.UpdateAsync(invite.AppUser);
        }

        _dbContext.Invites.Remove(invite);
        await _dbContext.SaveChangesAsync(cancellationToken);
        return Ok(new { success = true });
    }

    [HttpPost("join-requests/{inviteId:int}/decline")]
    public async Task<IActionResult> DeclineJoinRequestAsync(int inviteId, CancellationToken cancellationToken)
    {
        var appUser = await ResolveAppUserAsync(cancellationToken);
        if (appUser is null)
        {
            return Unauthorized(new
            {
                error = "Sign in with ASP.NET Identity or provide a Discord bearer token to decline join requests."
            });
        }

        var invite = await _dbContext.Invites
            .FirstOrDefaultAsync(item => item.Id == inviteId && item.Status == PendingJoinRequestStatus, cancellationToken);

        if (invite is null)
        {
            return NotFound(new { error = "The selected join request was not found." });
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
}
