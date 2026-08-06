using LinkshellManagerDiscordApp.Authorization;
using LinkshellManagerDiscordApp.Models;
using LinkshellManagerDiscordApp.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace LinkshellManagerDiscordApp.Controllers;

// Pricing ONE posted attendance window.
//
// EventAttendanceWindow.DkpAmount is what an officer says this particular window pays each
// attendee, replacing the camp's default (open bonus on window 1, close bonus on the close, and
// nothing in between). It exists because "we sat this window for two hours and the pop was a
// no-show, pay them something" has no other answer — the four linkshell bonuses price the SHAPE of
// a camp, not one bad window inside it.
//
// Two routes over one helper, the same addon/management pair RemoveWindowAttendeeRowAsync already
// establishes: the addon knows the sequence it is posting against, the Activity knows the row id
// its DTO carries.
//
// NOT reachable for Manual Check In camps or Claim/Kill-style windowed events — neither pays off
// this column, and an endpoint that accepts a number nothing will ever pay is worse than no
// endpoint. See HnmCampPricing.HonoursWindowAmount for which camps qualify and why.
public sealed partial class AddonApiController
{
    // A hand-typed rate is a rate, not a jackpot. The cap only exists to turn a fat-fingered
    // "10000" paste into a 400 instead of a linkshell-wide DKP event nobody meant.
    private const double MaxWindowDkp = 10000d;

    // PATCH /api/addon/events/{eventId}/windows/{sequence}/dkp   (in-game addon, token auth)
    [HttpPatch("events/{eventId:int}/windows/{sequence:int}/dkp")]
    [AddonApiAuth]
    public async Task<IActionResult> SetWindowDkpAddonAsync(
        int eventId,
        int sequence,
        [FromBody] AddonSetWindowDkpRequest request,
        CancellationToken cancellationToken)
    {
        var token = AddonApiAuthAttribute.GetToken(HttpContext);

        var window = await _dbContext.EventAttendanceWindows
            .Include(w => w.Event)
            .FirstOrDefaultAsync(
                w => w.EventId == eventId && w.SequenceNumber == sequence, cancellationToken);
        if (window?.Event is null) return NotFound(new { error = "Window not found." });
        if (window.Event.LinkshellId != token.LinkshellId) return Forbid();
        if (!await TokenIssuerCanModerateAsync(token, window.Event.LinkshellId, cancellationToken))
        {
            return Forbid();
        }

        return await SetWindowDkpAsync(window, request.DkpAmount, cancellationToken);
    }

    // PATCH /api/addon/management/attendance-windows/{id}/dkp   (web + Activity, cookie/bearer)
    //
    // By row id rather than (event, sequence), because that is what ActivityAttendanceWindowDto
    // already carries — exactly like the Remove button beside it.
    [HttpPatch("management/attendance-windows/{id:int}/dkp")]
    public async Task<IActionResult> SetWindowDkpManagementAsync(
        int id,
        [FromBody] AddonSetWindowDkpRequest request,
        CancellationToken cancellationToken)
    {
        var appUser = await ResolveManagementUserAsync(cancellationToken);
        if (appUser is null) return Unauthorized(new { error = "Not signed in. Open /Identity/Account/Login on this same host first, or launch the activity inside Discord." });

        var window = await _dbContext.EventAttendanceWindows
            .Include(w => w.Event)
            .FirstOrDefaultAsync(w => w.Id == id, cancellationToken);
        if (window?.Event is null) return NotFound(new { error = "Window not found." });

        var membership = await _dbContext.AppUserLinkshells
            .FirstOrDefaultAsync(
                m => m.AppUserId == appUser.Id && m.LinkshellId == window.Event.LinkshellId,
                cancellationToken);
        if (!CanManageLinkshell(membership)) return Forbid();

        return await SetWindowDkpAsync(window, request.DkpAmount, cancellationToken);
    }

    // The shared body. Callers have already authorized; this owns validation and the write.
    private async Task<IActionResult> SetWindowDkpAsync(
        EventAttendanceWindow window, double? amount, CancellationToken cancellationToken)
    {
        var eventEntity = window.Event!;

        if (!HnmCampPricing.HonoursWindowAmount(eventEntity))
        {
            // Refused rather than stored, so the number can never be shown as if it were going to
            // be paid. Both clients render `error` verbatim, so this text reaches the officer.
            return BadRequest(new
            {
                error = DiscordEventMessageBuilder.IsWd(eventEntity)
                    ? "This camp credits attendance from Check In to Check Out, so a single window has no price of its own."
                    : "Only HNM camps price individual windows. This event pays by the hour, or per window at its own DKP rate."
            });
        }

        // Closed means the roster has already been handed to review, where the amounts are edited
        // per member instead. Re-pricing a window AFTER its Close is posted is allowed on purpose,
        // though — that correction is the whole point of the control.
        if (eventEntity.EndTime is not null)
        {
            return BadRequest(new { error = "Cannot re-price a window on a closed event." });
        }

        double? resolved = null;
        if (amount is { } value)
        {
            if (double.IsNaN(value) || double.IsInfinity(value))
            {
                return BadRequest(new { error = "dkpAmount must be a number." });
            }
            if (value < 0)
            {
                return BadRequest(new { error = "dkpAmount must be non-negative." });
            }
            if (value > MaxWindowDkp)
            {
                return BadRequest(new { error = $"dkpAmount must be {MaxWindowDkp:0} or less." });
            }

            // Snap on WRITE, not just at payout. The finalizer rounds the member's total anyway,
            // but storing off-grid would leave the addon's preview and the Activity's cell showing
            // a number neither of them will ever pay.
            var linkshell = await _dbContext.Linkshells
                .AsNoTracking()
                .FirstOrDefaultAsync(l => l.Id == eventEntity.LinkshellId, cancellationToken);
            resolved = DkpRounding.Round(value, DkpRounding.StepFor(linkshell?.DkpRoundingIncrement));
        }

        window.DkpAmount = resolved;
        await _dbContext.SaveChangesAsync(cancellationToken);

        return Ok(new
        {
            eventId = window.EventId,
            sequenceNumber = window.SequenceNumber,
            dkpAmount = window.DkpAmount
        });
    }

    // null CLEARS the override and puts the window back on the camp's default. That is a real
    // instruction, not a missing field — which is why it is nullable rather than required.
    public sealed record AddonSetWindowDkpRequest(double? DkpAmount);
}
