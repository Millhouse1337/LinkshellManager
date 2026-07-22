using LinkshellManagerDiscordApp.Models;
using LinkshellManagerDiscordApp.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace LinkshellManagerDiscordApp.Controllers;

// "Claim your DKP": when someone the DKP import created as an unclaimed
// PLACEHOLDER (carrying a seeded balance) appears in the app, these endpoints let
// them associate that imported data to their real account. The Discord-id case is
// handled silently on first launch (DiscordIdentityService promotes the
// placeholder in place); these cover the name-matched, user-confirmed case and
// the officer "link this placeholder to an account / Discord id" fallback.
public sealed partial class ActivityDataController
{
    // Placeholders whose character name (main/alt) matches the signed-in user's
    // names — i.e. likely "them", imported before they joined.
    [HttpGet("claim/candidates")]
    public async Task<IActionResult> GetClaimCandidatesAsync(CancellationToken cancellationToken)
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

        // Respect the per-linkshell guild lock (Activity path). On the cookie/web
        // path, locked linkshells without a verifiable guild membership are
        // excluded — the officer link fallback still covers those.
        var lockPairs = placeholderMemberships
            .Select(m => (m.LinkshellId, m.Linkshell?.LockToDiscordGuild == true ? m.Linkshell?.DiscordGuildId : null))
            .Distinct()
            .ToList();
        var accessible = await FilterAccessibleLinkshellIdsAsync(lockPairs, cancellationToken);

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
                };
            })
            .ToList();

        return Ok(new { candidates });
    }

    // The signed-in user confirms "that's me" for a name-matched placeholder.
    [HttpPost("claim")]
    public async Task<IActionResult> ClaimAsync(
        [FromBody] ActivityClaimRequest request,
        [FromServices] PlaceholderClaimService claimService,
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
            .FirstOrDefaultAsync(
                m => m.AppUserId == request.PlaceholderAppUserId && m.LinkshellId == request.LinkshellId,
                cancellationToken);
        if (placeholderMembership?.AppUser is null || !placeholderMembership.AppUser.IsPlaceholder)
        {
            return BadRequest(new { error = "That member is not an unclaimed placeholder." });
        }

        // Security gate: a user may only claim a placeholder that goes by one of
        // their own character names. Never trust the client's chosen id blindly.
        var myNames = NameKeys(appUser);
        var phNames = NameKeys(placeholderMembership.AppUser, placeholderMembership.CharacterName);
        if (!phNames.Overlaps(myNames))
        {
            return Forbid();
        }

        var result = await claimService.ClaimPlaceholderAsync(
            placeholderMembership.AppUserId!, appUser.Id, request.LinkshellId, cancellationToken);
        if (!result.Success)
        {
            return BadRequest(new { error = result.Error });
        }
        return Ok(new { success = true, claimedLinkshellIds = result.ClaimedLinkshellIds });
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
