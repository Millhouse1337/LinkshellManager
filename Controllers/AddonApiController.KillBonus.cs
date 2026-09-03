using LinkshellManagerDiscordApp.Authorization;
using LinkshellManagerDiscordApp.Models;
using LinkshellManagerDiscordApp.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace LinkshellManagerDiscordApp.Controllers;

// The camp's KILL BONUS — what the Kill tab's "Kill DKP" box edits, in the addon and in the
// Activity alike.
//
// Deliberately NOT a window price, and the distinction is the whole reason this file exists. A
// kill roster is worth 0 as a window and pays through the kill bonus at End Camp instead
// (HnmStandardCampFinalizer.WindowValue returns 0 for one; ComputeMemberDkp adds the bonus for
// anyone on the roster). A control on the Kill tab that wrote EventAttendanceWindow.DkpAmount
// would stack a window payment ON TOP of that bonus and pay the people who turned up for the kill
// twice for one appearance — which is exactly why AddonApiController.WindowDkp refuses a price on
// a kill window, and continues to.
//
// So this writes Event.HnmKillBonusOverride: the same per-camp override the create/edit form's
// "Change DKP" section writes, resolved by HnmCampPricing with the linkshell's
// HnmStandardKillBonus / WdKillBonus as the fallback. The box on the Kill tab and that setting are
// two views of ONE number, which is what makes "Kill DKP" mean what it says.
//
// Two routes and one body, mirroring WindowDkp: the addon posts with a pairing token, the web and
// the Activity with a cookie or bearer.
public sealed partial class AddonApiController
{
    // Same ceiling and the same reasoning as MaxWindowDkp: the cap exists to turn a fat-fingered
    // paste into a 400 rather than a linkshell-wide DKP event nobody meant.
    private const double MaxKillBonus = 10000d;

    // PATCH /api/addon/events/{eventId}/kill-bonus   (in-game addon, token auth)
    [HttpPatch("events/{eventId:int}/kill-bonus")]
    [AddonApiAuth]
    public async Task<IActionResult> SetCampKillBonusAddonAsync(
        int eventId,
        [FromBody] AddonSetKillBonusRequest request,
        CancellationToken cancellationToken)
    {
        var token = AddonApiAuthAttribute.GetToken(HttpContext);

        var eventEntity = await _dbContext.Events
            .FirstOrDefaultAsync(evt => evt.Id == eventId, cancellationToken);
        if (eventEntity is null) return NotFound(new { error = "Event not found." });
        if (eventEntity.LinkshellId != token.LinkshellId) return Forbid();
        if (!await TokenIssuerCanModerateAsync(token, eventEntity.LinkshellId, cancellationToken))
        {
            return Forbid();
        }

        return await SetCampKillBonusAsync(eventEntity, request.KillBonus, cancellationToken);
    }

    // PATCH /api/addon/management/events/{eventId}/kill-bonus   (web + Activity, cookie/bearer)
    [HttpPatch("management/events/{eventId:int}/kill-bonus")]
    public async Task<IActionResult> SetCampKillBonusManagementAsync(
        int eventId,
        [FromBody] AddonSetKillBonusRequest request,
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

        return await SetCampKillBonusAsync(eventEntity, request.KillBonus, cancellationToken);
    }

    // The shared body. Callers have already authorized; this owns validation and the write.
    private async Task<IActionResult> SetCampKillBonusAsync(
        Event eventEntity, double? killBonus, CancellationToken cancellationToken)
    {
        // Same guard the window re-price carries: once the camp is closed the roster has been
        // handed to review, where the amounts are edited per member instead.
        if (eventEntity.EndTime is not null)
        {
            return BadRequest(new { error = "Cannot change the kill bonus on a closed event." });
        }

        var linkshell = await _dbContext.Linkshells
            .AsNoTracking()
            .FirstOrDefaultAsync(l => l.Id == eventEntity.LinkshellId, cancellationToken);

        double? resolved = null;
        if (killBonus is { } value)
        {
            if (double.IsNaN(value) || double.IsInfinity(value))
            {
                return BadRequest(new { error = "killBonus must be a number." });
            }
            if (value < 0)
            {
                return BadRequest(new { error = "killBonus must be non-negative." });
            }
            if (value > MaxKillBonus)
            {
                return BadRequest(new { error = $"killBonus must be {MaxKillBonus:0} or less." });
            }

            // Snapped on WRITE, matching SetWindowDkpAsync: the finalizer rounds the member's
            // total anyway, but storing off-grid would leave every box showing a number nothing
            // is ever going to pay.
            resolved = DkpRounding.Round(value, DkpRounding.StepFor(linkshell?.DkpRoundingIncrement));
        }

        eventEntity.HnmKillBonusOverride = resolved;
        await _dbContext.SaveChangesAsync(cancellationToken);

        // The RESOLVED figure comes back, not the stored override: clearing the override leaves
        // the camp on the linkshell default, and the box has to show what the camp now pays rather
        // than going blank. Same ungated read the addon events list sends as killBonus.
        var (_, effective) = HnmCampPricing.OutcomeBonuses(eventEntity, linkshell);

        return Ok(new
        {
            id = eventEntity.Id,
            killBonusOverride = eventEntity.HnmKillBonusOverride,
            killBonus = effective,
        });
    }

    // null CLEARS the per-camp override and puts the camp back on the linkshell's kill bonus.
    // A real instruction, not a missing field — hence nullable rather than required.
    public sealed record AddonSetKillBonusRequest(double? KillBonus);
}
