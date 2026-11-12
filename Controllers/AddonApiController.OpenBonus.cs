using LinkshellManagerDiscordApp.Models;
using LinkshellManagerDiscordApp.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace LinkshellManagerDiscordApp.Controllers;

// The camp's OPEN BONUS — what the Activity's "Open bonus" cell edits on window 1.
//
// Window 1 pays its regular amount plus this bonus (HnmStandardCampFinalizer.OpenWindowValue). A camp
// has one open, so the bonus is a property of the camp rather than of a window: this writes
// Event.HnmOpenBonusOverride, the same per-camp override the create/edit form's "Change DKP" section
// writes, with the linkshell's HnmStandardOpenBonus as the fallback. Twin of the close bonus
// (AddonApiController.CloseBonus).
//
// Management route only: the addon has no control for it.
public sealed partial class AddonApiController
{
    // Same ceiling and the same reasoning as MaxWindowDkp.
    private const double MaxOpenBonus = 10000d;

    // PATCH /api/addon/management/events/{eventId}/open-bonus   (web + Activity, cookie/bearer)
    [HttpPatch("management/events/{eventId:int}/open-bonus")]
    public async Task<IActionResult> SetCampOpenBonusManagementAsync(
        int eventId,
        [FromBody] AddonSetOpenBonusRequest request,
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
        if (!await CanEditLiveCampAsync(membership, cancellationToken)) return Forbid();

        // Only the camps whose open is paid per window. A Manual Check In camp pays its open off
        // the check-in range, and its own settings card is where that amount lives.
        if (!HnmCampPricing.HonoursWindowAmount(eventEntity))
        {
            return BadRequest(new { error = "Only HNM camps that pay per window have an open bonus." });
        }

        // Same guard as the close bonus: once the camp is closed the roster has been handed to
        // review, where the amounts are edited per member instead.
        if (eventEntity.EndTime is not null)
        {
            return BadRequest(new { error = "Cannot change the open bonus on a closed event." });
        }

        var linkshell = await _dbContext.Linkshells
            .AsNoTracking()
            .FirstOrDefaultAsync(l => l.Id == eventEntity.LinkshellId, cancellationToken);

        double? resolved = null;
        if (request.OpenBonus is { } value)
        {
            if (double.IsNaN(value) || double.IsInfinity(value))
            {
                return BadRequest(new { error = "openBonus must be a number." });
            }
            if (value < 0)
            {
                return BadRequest(new { error = "openBonus must be non-negative." });
            }
            if (value > MaxOpenBonus)
            {
                return BadRequest(new { error = $"openBonus must be {MaxOpenBonus:0} or less." });
            }

            // Snapped on WRITE, matching SetWindowDkpAsync.
            resolved = DkpRounding.Round(value, DkpRounding.StepFor(linkshell?.DkpRoundingIncrement));
        }

        eventEntity.HnmOpenBonusOverride = resolved;
        await _dbContext.SaveChangesAsync(cancellationToken);

        // The RESOLVED figure comes back: clearing the override leaves the camp on the linkshell
        // default, and the cell has to show what the camp now pays rather than going blank.
        return Ok(new
        {
            id = eventEntity.Id,
            openBonusOverride = eventEntity.HnmOpenBonusOverride,
            openBonus = HnmCampPricing.OpenBonusFor(eventEntity, linkshell),
        });
    }

    // null CLEARS the per-camp override and puts the camp back on the linkshell's open bonus.
    public sealed record AddonSetOpenBonusRequest(double? OpenBonus);
}
