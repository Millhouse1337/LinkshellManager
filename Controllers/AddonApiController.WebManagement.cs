using LinkshellManagerDiscordApp.Authorization;
using LinkshellManagerDiscordApp.Data;
using LinkshellManagerDiscordApp.Models;
using LinkshellManagerDiscordApp.Services;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace LinkshellManagerDiscordApp.Controllers;

public sealed partial class AddonApiController
{
    // Cookie-authenticated remove: the web /Event/Start page and the Discord
    // Activity's Attendance Windows card both call this with the AppUserEventWindow
    // row id (exposed via their respective view models / DTOs).
    [HttpDelete("management/window-attendees/{id:int}")]
    public async Task<IActionResult> RemoveWindowAttendeeManagementAsync(
        int id, CancellationToken cancellationToken)
    {
        var appUser = await ResolveManagementUserAsync(cancellationToken);
        if (appUser is null) return Unauthorized(new { error = "Not signed in. Open /Identity/Account/Login on this same host first, or launch the activity inside Discord." });

        var attendee = await _dbContext.AppUserEventWindows
            .Include(a => a.EventAttendanceWindow)
                .ThenInclude(w => w!.Event)
            .FirstOrDefaultAsync(a => a.Id == id, cancellationToken);

        if (attendee is null) return NotFound(new { error = "Attendee not found." });
        var linkshellId = attendee.EventAttendanceWindow?.Event?.LinkshellId;
        if (linkshellId is null) return NotFound(new { error = "Window event not found." });

        var membership = await _dbContext.AppUserLinkshells
            .FirstOrDefaultAsync(
                m => m.AppUserId == appUser.Id && m.LinkshellId == linkshellId.Value,
                cancellationToken);
        if (!CanManageLinkshell(membership)) return Forbid();

        await RemoveWindowAttendeeRowAsync(attendee, cancellationToken);
        return Ok(new { removedId = attendee.Id });
    }

    [HttpPost("management/pairing-code")]
    public async Task<IActionResult> CreatePairingCodeAsync(
        [FromBody] CreatePairingCodeRequest request,
        CancellationToken cancellationToken)
    {
        if (request.LinkshellId <= 0)
        {
            return BadRequest(new { error = "Linkshell is required." });
        }

        // Global kill-switch: don't hand out new pairing codes while the addon
        // is disabled. The redeem path (/pair) is blocked too, so a code issued
        // just before a shutdown still can't be used.
        if (await _globalSettings.IsAddonGloballyDisabledAsync(cancellationToken))
        {
            return BadRequest(new { error = "The addon is currently disabled by the server administrator." });
        }

        var appUser = await ResolveManagementUserAsync(cancellationToken);
        if (appUser is null) return Unauthorized(new { error = "Not signed in. Open /Identity/Account/Login on this same host first, or launch the activity inside Discord." });

        var membership = await _dbContext.AppUserLinkshells
            .FirstOrDefaultAsync(
                m => m.AppUserId == appUser.Id && m.LinkshellId == request.LinkshellId,
                cancellationToken);

        if (!CanManageLinkshell(membership))
        {
            return Forbid();
        }

        var code = await _auth.CreatePairingCodeAsync(
            request.LinkshellId, appUser.Id, request.Label, cancellationToken);

        return Ok(new
        {
            code,
            expiresInMinutes = 10
        });
    }

    // Poll target for the pairing modal. Returns whether the code has been
    // redeemed in-game yet (consumed), expired, or is still waiting. The web
    // modal polls this every couple seconds so it can auto-close the moment
    // the addon runs /lsm link <code>. Consumed codes are cleaned up lazily
    // on the next CreatePairingCode call, so "not found" after a known-issued
    // code is treated the same as expired (the modal stops polling).
    [HttpGet("management/pairing-code/status")]
    public async Task<IActionResult> PairingCodeStatusAsync(
        [FromQuery] int linkshellId,
        [FromQuery] string? code,
        CancellationToken cancellationToken)
    {
        if (linkshellId <= 0)
        {
            return BadRequest(new { error = "Linkshell is required." });
        }
        if (string.IsNullOrWhiteSpace(code))
        {
            return BadRequest(new { error = "Code is required." });
        }

        var appUser = await ResolveManagementUserAsync(cancellationToken);
        if (appUser is null) return Unauthorized(new { error = "Not signed in. Open /Identity/Account/Login on this same host first, or launch the activity inside Discord." });

        var membership = await _dbContext.AppUserLinkshells
            .FirstOrDefaultAsync(
                m => m.AppUserId == appUser.Id && m.LinkshellId == linkshellId,
                cancellationToken);
        if (!CanManageLinkshell(membership)) return Forbid();

        var normalized = code.Trim().ToUpperInvariant();
        var pairing = await _dbContext.AddonPairingCodes
            .AsNoTracking()
            .FirstOrDefaultAsync(
                c => c.Code == normalized && c.LinkshellId == linkshellId,
                cancellationToken);

        if (pairing is null)
        {
            // Either never existed or already consumed + cleaned. From the
            // modal's perspective there's nothing left to wait on.
            return Ok(new { found = false, consumed = false, expired = true });
        }

        var consumed = pairing.ConsumedAt is not null;
        var expired = !consumed && pairing.ExpiresAt < DateTime.UtcNow;
        return Ok(new { found = true, consumed, expired });
    }

    [HttpGet("management/tokens")]
    public async Task<IActionResult> ListTokensAsync(
        [FromQuery] int linkshellId,
        CancellationToken cancellationToken)
    {
        if (linkshellId <= 0)
        {
            return BadRequest(new { error = "Linkshell is required." });
        }

        var appUser = await ResolveManagementUserAsync(cancellationToken);
        if (appUser is null) return Unauthorized(new { error = "Not signed in. Open /Identity/Account/Login on this same host first, or launch the activity inside Discord." });

        var membership = await _dbContext.AppUserLinkshells
            .FirstOrDefaultAsync(
                m => m.AppUserId == appUser.Id && m.LinkshellId == linkshellId,
                cancellationToken);

        if (!CanManageLinkshell(membership))
        {
            return Forbid();
        }

        var tokens = await _auth.ListVisibleAsync(linkshellId, appUser.Id, cancellationToken);

        // A pairing code mints one token per linkshell, so collapse each batch
        // back into the single pairing the user actually performed -- otherwise
        // someone in four linkshells sees the same pairing listed four times.
        // Tokens predating PairingBatchId (null) stand alone, as they always did.
        var pairings = tokens
            .GroupBy(t => t.PairingBatchId?.ToString() ?? $"token:{t.Id}")
            .Select(group =>
            {
                // Revoke authorizes against the row's own linkshell, so hand back
                // the token bound to the linkshell being viewed when the batch
                // covers it; otherwise the oldest, which is the redeemed one.
                var representative = group.FirstOrDefault(t => t.LinkshellId == linkshellId)
                                     ?? group.OrderBy(t => t.Id).First();
                return new
                {
                    id = representative.Id,
                    linkshellId = representative.LinkshellId,
                    prefix = representative.TokenPrefix,
                    label = representative.Label,
                    createdAt = group.Min(t => t.CreatedAt),
                    lastUsedAt = group.Max(t => t.LastUsedAt),
                    issuedToAppUserId = representative.IssuedToAppUserId,
                    // Lets the UI separate "your pairing" (shown on every one of
                    // your linkshells) from another member's pairing on this one.
                    mine = representative.IssuedToAppUserId == appUser.Id,
                    linkshells = group
                        .Select(t => t.Linkshell?.LinkshellName ?? $"#{t.LinkshellId}")
                        .Distinct()
                        .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                        .ToList()
                };
            })
            .OrderByDescending(p => p.createdAt)
            .ToList();

        return Ok(new { tokens = pairings });
    }

    // linkshellId is accepted for backwards compatibility with older callers but
    // is no longer what authorizes the revoke: the listing now surfaces pairings
    // bound to any of the viewer's linkshells, so the token's own linkshell (or
    // the viewer owning it) is what decides.
    [HttpPost("management/tokens/{tokenId:int}/revoke")]
    public async Task<IActionResult> RevokeTokenAsync(
        int tokenId,
        CancellationToken cancellationToken)
    {
        var appUser = await ResolveManagementUserAsync(cancellationToken);
        if (appUser is null) return Unauthorized(new { error = "Not signed in. Open /Identity/Account/Login on this same host first, or launch the activity inside Discord." });

        var token = await _dbContext.AddonApiTokens
            .AsNoTracking()
            .FirstOrDefaultAsync(t => t.Id == tokenId, cancellationToken);

        if (token is null)
        {
            return NotFound(new { error = "Token not found." });
        }

        // Your own pairing is always yours to disconnect. Anyone else's needs
        // manage rights on the linkshell that token is bound to.
        if (token.IssuedToAppUserId != appUser.Id)
        {
            var membership = await _dbContext.AppUserLinkshells
                .FirstOrDefaultAsync(
                    m => m.AppUserId == appUser.Id && m.LinkshellId == token.LinkshellId,
                    cancellationToken);

            if (!CanManageLinkshell(membership))
            {
                return Forbid();
            }
        }

        var revoked = await _auth.RevokeAsync(tokenId, token.LinkshellId, cancellationToken);
        if (!revoked)
        {
            return NotFound(new { error = "Token not found." });
        }

        return Ok(new { success = true });
    }
}
