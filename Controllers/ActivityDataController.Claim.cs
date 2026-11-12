using LinkshellManagerDiscordApp.Models;
using LinkshellManagerDiscordApp.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace LinkshellManagerDiscordApp.Controllers;

// "Claim your DKP": when someone the DKP import created as an unclaimed
// PLACEHOLDER (carrying a seeded balance) appears in the app, these endpoints let
// them associate that imported data to their real account. The Discord-id case is
// handled silently on first launch (DiscordIdentityService promotes the
// placeholder in place, when an officer confirmed that Discord id); these cover
// the name-matched case (a request an officer approves) and the officer "link
// this placeholder to an account / Discord id" fallback.
public sealed partial class ActivityDataController
{
    // Placeholders whose character name (main/alt) matches the signed-in user's
    // names — i.e. likely "them", imported before they joined.
    [HttpGet("claim/candidates")]
    public async Task<IActionResult> GetClaimCandidatesAsync(
        [FromServices] PlaceholderClaimRequestService claimRequests,
        CancellationToken cancellationToken)
    {
        var appUser = await ResolveAppUserAsync(cancellationToken);
        if (appUser is null)
        {
            return Unauthorized(new { error = "Sign in to view claimable DKP." });
        }
        if (appUser.IsPlaceholder)
        {
            return Ok(new { candidates = Array.Empty<object>() });
        }

        var names = NameKeys(appUser).ToList();
        if (names.Count == 0)
        {
            return Ok(new { candidates = Array.Empty<object>() });
        }

        var placeholderMemberships = await _dbContext.AppUserLinkshells
            .AsNoTracking()
            .Include(m => m.AppUser)
            .Include(m => m.Linkshell)
            .Where(m => m.AppUserId != null
                        && m.AppUser != null
                        && m.AppUser.IsPlaceholder
                        && ((m.CharacterName != null && names.Contains(m.CharacterName.ToLower()))
                            || (m.AppUser.CharacterName != null && names.Contains(m.AppUser.CharacterName.ToLower()))
                            || (m.AppUser.AltCharacterName1 != null && names.Contains(m.AppUser.AltCharacterName1.ToLower()))
                            || (m.AppUser.AltCharacterName2 != null && names.Contains(m.AppUser.AltCharacterName2.ToLower()))))
            .ToListAsync(cancellationToken);

        if (placeholderMemberships.Count == 0)
        {
            return Ok(new { candidates = Array.Empty<object>() });
        }

        // Respect the per-linkshell guild lock on BOTH paths. Claiming joins the
        // linkshell, so unlike plain viewing the website does not get a pass: on
        // the cookie path, locked linkshells without a verifiable guild membership
        // are excluded — the officer link fallback still covers those.
        var lockPairs = placeholderMemberships
            .Select(m => (m.LinkshellId, m.Linkshell?.LockToDiscordGuild == true ? m.Linkshell?.DiscordGuildId : null))
            .Distinct()
            .ToList();
        var accessible = await FilterAccessibleLinkshellIdsAsync(lockPairs, cancellationToken, lockAppliesOnWebsite: true);
        var requested = await claimRequests.PendingForAccountAsync(appUser.Id, cancellationToken);

        var candidates = placeholderMemberships
            .Where(m => accessible.Contains(m.LinkshellId))
            .Select(m =>
            {
                var step = DkpRounding.StepFor(m.Linkshell?.DkpRoundingIncrement);
                return new
                {
                    placeholderAppUserId = m.AppUserId,
                    linkshellId = m.LinkshellId,
                    linkshellName = string.IsNullOrWhiteSpace(m.Linkshell?.LinkshellName) ? "Linkshell" : m.Linkshell!.LinkshellName,
                    characterName = m.CharacterName ?? m.AppUser?.CharacterName ?? "Unknown",
                    currentDkp = DkpRounding.Round(m.LinkshellDkp ?? 0, step),
                    totalDkp = DkpRounding.Round(m.SeededDkpEarned, step),
                    totalSpent = DkpRounding.Round(m.SeededDkpSpent, step),
                    // Already asked: waiting for an officer to approve.
                    requested = requested.Contains((m.AppUserId!, m.LinkshellId)),
                };
            })
            .ToList();

        return Ok(new { candidates });
    }

    // The signed-in user says "that's me" for a name-matched placeholder. That only ASKS: an
    // officer who can manage members approves it (claim-requests below), because character names
    // are public and a matching name proves nothing. An outsider could otherwise set their profile
    // name to an imported member's and walk off with that member's DKP and roster spot.
    [HttpPost("claim")]
    public async Task<IActionResult> ClaimAsync(
        [FromBody] ActivityClaimRequest request,
        [FromServices] PlaceholderClaimRequestService claimRequests,
        CancellationToken cancellationToken)
    {
        var appUser = await ResolveAppUserAsync(cancellationToken);
        if (appUser is null)
        {
            return Unauthorized(new { error = "Sign in to claim DKP." });
        }
        if (appUser.IsPlaceholder)
        {
            return Forbid();
        }
        if (request is null || string.IsNullOrWhiteSpace(request.PlaceholderAppUserId))
        {
            return BadRequest(new { error = "Missing placeholder." });
        }

        var placeholderMembership = await _dbContext.AppUserLinkshells
            .Include(m => m.AppUser)
            .Include(m => m.Linkshell)
            .FirstOrDefaultAsync(
                m => m.AppUserId == request.PlaceholderAppUserId && m.LinkshellId == request.LinkshellId,
                cancellationToken);
        if (placeholderMembership?.AppUser is null || !placeholderMembership.AppUser.IsPlaceholder)
        {
            return BadRequest(new { error = "That member is not an unclaimed placeholder." });
        }

        // The same guild lock the candidate list applies. Claiming JOINS the linkshell, so the
        // website gets no pass: without this, anyone who learned a placeholder's id could join a
        // locked linkshell from outside its Discord server.
        var lockGuildId = placeholderMembership.Linkshell?.LockToDiscordGuild == true
            ? placeholderMembership.Linkshell.DiscordGuildId
            : null;
        var accessible = await FilterAccessibleLinkshellIdsAsync(
            new[] { (request.LinkshellId, lockGuildId) }, cancellationToken, lockAppliesOnWebsite: true);
        if (!accessible.Contains(request.LinkshellId))
        {
            return Forbid();
        }

        // A user may only ask for a placeholder that goes by one of their own character names.
        // Never trust the client's chosen id blindly.
        var myNames = NameKeys(appUser);
        var phNames = NameKeys(placeholderMembership.AppUser, placeholderMembership.CharacterName);
        if (!phNames.Overlaps(myNames))
        {
            return Forbid();
        }

        await claimRequests.RequestForAccountAsync(
            request.LinkshellId, placeholderMembership.AppUserId!, appUser.Id,
            appUser.CharacterName ?? appUser.UserName, cancellationToken);
        return Ok(new { success = true, pending = true });
    }

    // Officers: the open "that's me" requests for this linkshell's imported members.
    [HttpGet("linkshells/{linkshellId:int}/claim-requests")]
    public async Task<IActionResult> GetClaimRequestsAsync(
        int linkshellId,
        [FromServices] PlaceholderClaimRequestService claimRequests,
        CancellationToken cancellationToken)
    {
        var officer = await ResolveClaimRequestOfficerAsync(linkshellId, cancellationToken);
        if (officer.Error is not null)
        {
            return officer.Error;
        }
        var requests = await claimRequests.ListPendingAsync(linkshellId, cancellationToken);
        return Ok(new
        {
            requests = requests.Select(r => new
            {
                id = r.Id,
                membershipId = r.MembershipId,
                placeholderName = r.PlaceholderName,
                placeholderDkp = r.PlaceholderDkp,
                requesterName = r.RequesterName,
                requesterKind = r.RequesterKind,
                createdAtUtc = r.CreatedAtUtc,
            }),
        });
    }

    [HttpPost("linkshells/{linkshellId:int}/claim-requests/{requestId:int}/approve")]
    public async Task<IActionResult> ApproveClaimRequestAsync(
        int linkshellId,
        int requestId,
        [FromServices] PlaceholderClaimRequestService claimRequests,
        CancellationToken cancellationToken)
    {
        var officer = await ResolveClaimRequestOfficerAsync(linkshellId, cancellationToken);
        if (officer.Error is not null)
        {
            return officer.Error;
        }
        var result = await claimRequests.ApproveAsync(linkshellId, requestId, officer.AppUserId!, cancellationToken);
        return result.Success ? Ok(new { success = true }) : BadRequest(new { error = result.Error });
    }

    [HttpPost("linkshells/{linkshellId:int}/claim-requests/{requestId:int}/deny")]
    public async Task<IActionResult> DenyClaimRequestAsync(
        int linkshellId,
        int requestId,
        [FromServices] PlaceholderClaimRequestService claimRequests,
        CancellationToken cancellationToken)
    {
        var officer = await ResolveClaimRequestOfficerAsync(linkshellId, cancellationToken);
        if (officer.Error is not null)
        {
            return officer.Error;
        }
        var result = await claimRequests.DenyAsync(linkshellId, requestId, officer.AppUserId!, cancellationToken);
        return result.Success ? Ok(new { success = true }) : BadRequest(new { error = result.Error });
    }

    // Claim requests hand over a member's DKP, so they are for officers who can manage members.
    private async Task<(string? AppUserId, IActionResult? Error)> ResolveClaimRequestOfficerAsync(
        int linkshellId, CancellationToken cancellationToken)
    {
        var appUser = await ResolveAppUserAsync(cancellationToken);
        if (appUser is null)
        {
            return (null, Unauthorized(new { error = "Sign in to manage members." }));
        }
        var membership = await GetMembershipAsync(appUser.Id, linkshellId, cancellationToken);
        if (!await CanAsync(membership, r => r.CanManageMembers, cancellationToken))
        {
            return (null, Forbid());
        }
        return (appUser.Id, null);
    }

    // Officer fallback: link an unclaimed placeholder either to a Discord id (so
    // the next launch auto-claims it) or directly to an existing account (merge now).
    [HttpPost("linkshells/{linkshellId:int}/members/{membershipId:int}/link")]
    public async Task<IActionResult> LinkPlaceholderAsync(
        int linkshellId,
        int membershipId,
        [FromBody] ActivityLinkPlaceholderRequest request,
        [FromServices] PlaceholderClaimService claimService,
        CancellationToken cancellationToken)
    {
        var appUser = await ResolveAppUserAsync(cancellationToken);
        if (appUser is null)
        {
            return Unauthorized(new { error = "Sign in to manage members." });
        }
        var membership = await GetMembershipAsync(appUser.Id, linkshellId, cancellationToken);
        if (!await CanAsync(membership, r => r.CanManageMembers, cancellationToken))
        {
            return Forbid();
        }

        var target = await _dbContext.AppUserLinkshells
            .Include(m => m.AppUser)
            .FirstOrDefaultAsync(m => m.Id == membershipId && m.LinkshellId == linkshellId, cancellationToken);
        if (target?.AppUser is null || !target.AppUser.IsPlaceholder || target.AppUserId is null)
        {
            return BadRequest(new { error = "That member is not an unclaimed placeholder." });
        }

        if (request is not null && !string.IsNullOrWhiteSpace(request.DiscordUserId))
        {
            target.DiscordUserId = request.DiscordUserId.Trim();
            // An officer set it, so that Discord user's first LSM launch may take the placeholder over.
            target.DiscordIdConfirmed = true;
            await _dbContext.SaveChangesAsync(cancellationToken);
            return Ok(new { success = true, linked = "discord" });
        }

        if (request is not null && !string.IsNullOrWhiteSpace(request.TargetAppUserId))
        {
            var result = await claimService.ClaimPlaceholderAsync(
                target.AppUserId, request.TargetAppUserId.Trim(), linkshellId, cancellationToken);
            if (!result.Success)
            {
                return BadRequest(new { error = result.Error });
            }
            return Ok(new { success = true, linked = "account" });
        }

        return BadRequest(new { error = "Provide a Discord ID or a target account." });
    }

    // Lowercased set of a user's character names (main + alts), plus an optional
    // extra (a membership's own CharacterName).
    private static HashSet<string> NameKeys(AppUser user, string? extra = null)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Add(string? n) { if (!string.IsNullOrWhiteSpace(n)) { set.Add(n.Trim().ToLowerInvariant()); } }
        Add(user.CharacterName);
        Add(user.AltCharacterName1);
        Add(user.AltCharacterName2);
        Add(extra);
        return set;
    }
}

public sealed record ActivityClaimRequest(string? PlaceholderAppUserId, int LinkshellId);

public sealed record ActivityLinkPlaceholderRequest(string? TargetAppUserId, string? DiscordUserId);
