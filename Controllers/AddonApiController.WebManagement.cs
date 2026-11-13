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
    // Cookie-authenticated remove: the Activity's Attendance Windows card calls
    // this with the AppUserEventWindow row id (exposed via its DTO).
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
        if (!await CanEditLiveCampAsync(membership, cancellationToken)) return Forbid();

        await RemoveWindowAttendeeRowAsync(attendee, cancellationToken);
        return Ok(new { removedId = attendee.Id });
    }

    // Cookie-authenticated delete of a WHOLE posted window: the x on a live camp's window panel in
    // the Activity. The same guards and cleanup as the addon's undo (DeleteAttendanceWindowAsync:
    // live camps only, Close before Open on an Open/Close camp), at the same bar as removing one
    // attendee from it.
    [HttpDelete("management/windows/{windowId:int}")]
    public async Task<IActionResult> DeleteWindowManagementAsync(
        int windowId, CancellationToken cancellationToken)
    {
        var appUser = await ResolveManagementUserAsync(cancellationToken);
        if (appUser is null) return Unauthorized(new { error = "Not signed in. Open /Identity/Account/Login on this same host first, or launch the activity inside Discord." });

        var attendanceWindow = await _dbContext.EventAttendanceWindows
            .Include(w => w.Event)
            .Include(w => w.Attendees)
            .FirstOrDefaultAsync(w => w.Id == windowId, cancellationToken);
        if (attendanceWindow?.Event is null) return NotFound(new { error = "Window not found." });

        var membership = await _dbContext.AppUserLinkshells
            .FirstOrDefaultAsync(
                m => m.AppUserId == appUser.Id && m.LinkshellId == attendanceWindow.Event.LinkshellId,
                cancellationToken);
        if (!await CanEditLiveCampAsync(membership, cancellationToken)) return Forbid();

        return await DeleteAttendanceWindowAsync(attendanceWindow, cancellationToken);
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

        if (!await CanUseAddonAsync(membership, cancellationToken))
        {
            return Forbid();
        }

        var code = await _auth.CreatePairingCodeAsync(
            request.LinkshellId, appUser.Id, cancellationToken);

        return Ok(new
        {
            code,
            expiresInMinutes = 10
        });
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

        if (!await CanUseAddonAsync(membership, cancellationToken))
        {
            return Forbid();
        }

        // The viewer's own pairings only. `linkshellId` above still gates ACCESS to the card (you
        // must be able to use the addon on the linkshell you are viewing), it just no longer widens
        // the listing to everyone else's pairings on it -- see ListForUserAsync.
        var tokens = await _auth.ListForUserAsync(appUser.Id, cancellationToken);

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
                    createdAt = group.Min(t => t.CreatedAt),
                    lastUsedAt = group.Max(t => t.LastUsedAt),
                    issuedToAppUserId = representative.IssuedToAppUserId,
                    // Always true now that the listing is the viewer's own pairings only. Kept on
                    // the wire because the app reads it, and an older build that lost the field
                    // would read the absence as "somebody else's" and tag every row.
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
    // is no longer what authorizes the revoke: the token's own linkshell (or the
    // viewer owning it) is what decides.
    //
    // Your own pairing is always yours to disconnect. Anyone else's needs "Manage
    // members" on that token's linkshell -- the rule (and which batch siblings go
    // with it) lives in AddonApiAuthService.RevokeAsync so it can be unit tested. The
    // super-admin override is part of that rule (LinkshellPermissions), and only ever
    // lifts the caller inside a linkshell they have joined.
    [HttpPost("management/tokens/{tokenId:int}/revoke")]
    public async Task<IActionResult> RevokeTokenAsync(
        int tokenId,
        CancellationToken cancellationToken)
    {
        var appUser = await ResolveManagementUserAsync(cancellationToken);
        if (appUser is null) return Unauthorized(new { error = "Not signed in. Open /Identity/Account/Login on this same host first, or launch the activity inside Discord." });

        var result = await _auth.RevokeAsync(tokenId, appUser.Id, cancellationToken);
        return result switch
        {
            AddonApiAuthService.RevokeResult.NotFound => NotFound(new { error = "Token not found." }),
            AddonApiAuthService.RevokeResult.Forbidden => Forbid(),
            _ => Ok(new { success = true })
        };
    }
}
