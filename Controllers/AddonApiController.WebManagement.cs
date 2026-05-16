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

        var tokens = await _auth.ListActiveAsync(linkshellId, cancellationToken);
        return Ok(new
        {
            tokens = tokens.Select(t => new
            {
                id = t.Id,
                prefix = t.TokenPrefix,
                label = t.Label,
                createdAt = t.CreatedAt,
                lastUsedAt = t.LastUsedAt,
                issuedToAppUserId = t.IssuedToAppUserId
            })
        });
    }

    [HttpPost("management/tokens/{tokenId:int}/revoke")]
    public async Task<IActionResult> RevokeTokenAsync(
        int tokenId,
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

        var revoked = await _auth.RevokeAsync(tokenId, linkshellId, cancellationToken);
        if (!revoked)
        {
            return NotFound(new { error = "Token not found." });
        }

        return Ok(new { success = true });
    }
}
