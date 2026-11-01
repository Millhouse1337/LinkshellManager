using LinkshellManagerDiscordApp.Models;
using LinkshellManagerDiscordApp.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace LinkshellManagerDiscordApp.Controllers;

// The camp's CLOSE BONUS — what the Activity's "Closing window" cell edits on the ticked window.
//
// A camp has one close, so its close bonus is a property of the camp rather than of a window. This
// writes Event.HnmCloseBonusOverride: the same per-camp override the create/edit form's "Change
// DKP" section writes, with the linkshell's HnmStandardCloseBonus as the fallback. The cell and that
// setting are two views of ONE number, the same arrangement the Kill tab's box has with the kill
// bonus (AddonApiController.KillBonus).
//
// Management route only: the addon has no control for it.
public sealed partial class AddonApiController
{
    // Same ceiling and the same reasoning as MaxWindowDkp.
    private const double MaxCloseBonus = 10000d;

    // PATCH /api/addon/management/events/{eventId}/close-bonus   (web + Activity, cookie/bearer)
    [HttpPatch("management/events/{eventId:int}/close-bonus")]
    public async Task<IActionResult> SetCampCloseBonusManagementAsync(
        int eventId,
        [FromBody] AddonSetCloseBonusRequest request,
        CancellationToken cancellationToken)
    {
        var appUser = await ResolveManagementUserAsync(cancellationToken);
        if (appUser is null) return Unauthorized(new { error = "Not signed in. Open /Identity/Account/Login on this same host first, or launch the activity inside Discord." });

        var eventEntity = await _dbContext.Events
            .FirstOrDefaultAsync(evt => evt.Id == eventId, cancellationToken);
        if (eventEntity is null) return NotFound(new { error = "Event not found." });

        var membership = await _dbContext.AppUserLinkshells
            .FirstOrDefaultAsync(
                m => m.AppUserId == appUser.Id && m.LinkshellId == eventEntity.LinkshellId,
                cancellationToken);
        if (!CanManageLinkshell(membership)) return Forbid();

        // Only the camps whose close is paid per window. A Manual Check In camp pays its close off
        // the check-in range, and its own settings card is where that amount lives.
        if (!HnmCampPricing.HonoursWindowAmount(eventEntity))
        {
            return BadRequest(new { error = "Only HNM camps that pay per window have a closing window bonus." });
        }

        // Same guard the window re-price carries: once the camp is closed the roster has been
        // handed to review, where the amounts are edited per member instead.
        if (eventEntity.EndTime is not null)
        {
            return BadRequest(new { error = "Cannot change the close bonus on a closed event." });
        }

        var linkshell = await _dbContext.Linkshells
            .AsNoTracking()
            .FirstOrDefaultAsync(l => l.Id == eventEntity.LinkshellId, cancellationToken);

        double? resolved = null;
        if (request.CloseBonus is { } value)
        {
            if (double.IsNaN(value) || double.IsInfinity(value))
            {
                return BadRequest(new { error = "closeBonus must be a number." });
            }
            if (value < 0)
            {
                return BadRequest(new { error = "closeBonus must be non-negative." });
            }
            if (value > MaxCloseBonus)
            {
                return BadRequest(new { error = $"closeBonus must be {MaxCloseBonus:0} or less." });
            }

            // Snapped on WRITE, matching SetWindowDkpAsync.
            resolved = DkpRounding.Round(value, DkpRounding.StepFor(linkshell?.DkpRoundingIncrement));
        }

        eventEntity.HnmCloseBonusOverride = resolved;
        await _dbContext.SaveChangesAsync(cancellationToken);

        // The RESOLVED figure comes back: clearing the override leaves the camp on the linkshell
        // default, and the cell has to show what the camp now pays rather than going blank.
        return Ok(new
        {
            id = eventEntity.Id,
            closeBonusOverride = eventEntity.HnmCloseBonusOverride,
            closeBonus = HnmCampPricing.CloseBonusFor(eventEntity, linkshell),
        });
    }

    // null CLEARS the per-camp override and puts the camp back on the linkshell's close bonus.
    public sealed record AddonSetCloseBonusRequest(double? CloseBonus);
}
