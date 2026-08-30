using LinkshellManagerDiscordApp.Authorization;
using LinkshellManagerDiscordApp.Models;
using LinkshellManagerDiscordApp.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace LinkshellManagerDiscordApp.Controllers;

// Marking ONE posted window as the camp's close.
//
// The close used to be DERIVED — "the highest sequence posted so far" — and that guess is what put
// the close bonus on every window of every camp. The derivation itself was only ever wrong in a
// harmless way at payout (the last answer is the one that pays); what made it expensive is that
// HnmCampPricing.DefaultWindowValue QUOTED it to the addon before each post, and the addon wrote
// the quote back as EventAttendanceWindow.DkpAmount — an explicit price that replaces the computed
// one. Every window ended up frozen holding a close bonus it had briefly appeared to earn.
//
// So the close is now something an officer states, once, with a checkbox. It is the one fact about
// a camp's shape that cannot be read off the clock: only the person there knows which window they
// intend to close out on, and on a camp where the pop never came they may close on a window that
// isn't the last one posted.
//
// Two routes over one helper — the same addon/management pair SetWindowDkpAsync established: the
// addon knows the sequence it is posting against, the Activity and the web page know the row id
// their DTO carries.
public sealed partial class AddonApiController
{
    // PATCH /api/addon/events/{eventId}/windows/{sequence}/closing   (in-game addon, token auth)
    [HttpPatch("events/{eventId:int}/windows/{sequence:int}/closing")]
    [AddonApiAuth]
    public async Task<IActionResult> SetClosingWindowAddonAsync(
        int eventId,
        int sequence,
        [FromBody] AddonSetClosingWindowRequest request,
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

        return await SetClosingWindowAsync(window, request.IsClosingWindow, cancellationToken);
    }

    // PATCH /api/addon/management/attendance-windows/{id}/closing   (web + Activity, cookie/bearer)
    [HttpPatch("management/attendance-windows/{id:int}/closing")]
    public async Task<IActionResult> SetClosingWindowManagementAsync(
        int id,
        [FromBody] AddonSetClosingWindowRequest request,
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

        return await SetClosingWindowAsync(window, request.IsClosingWindow, cancellationToken);
    }

    // The shared body. Callers have already authorized; this owns validation and the write.
    private async Task<IActionResult> SetClosingWindowAsync(
        EventAttendanceWindow window, bool isClosing, CancellationToken cancellationToken)
    {
        var eventEntity = window.Event!;

        // Same gate as the per-window price, and for the same reason: HnmStandardCampFinalizer is
        // the only finalizer that reads a window's own pricing, so on any other camp this flag
        // would be a checkbox that changes nothing. Refusing beats storing a mark nothing honours.
        if (!HnmCampPricing.HonoursWindowAmount(eventEntity))
        {
            return BadRequest(new
            {
                error = DiscordEventMessageBuilder.IsWd(eventEntity)
                    ? "This camp credits attendance from Check In to Check Out, so no single window closes it out."
                    : "Only HNM camps have a closing window. This event pays by the hour, or per window at its own DKP rate."
            });
        }

        if (eventEntity.EndTime is not null)
        {
            return BadRequest(new { error = "Cannot change the closing window on a closed event." });
        }

        // A kill roster is filed AFTER the close and pays through the kill bonus. Letting it be
        // marked would move the close bonus off the window that earned it and onto the people who
        // only turned up for the fight — see EventAttendanceWindow.IsKillWindow.
        if (isClosing && window.IsKillWindow)
        {
            return BadRequest(new { error = "A Post Kill roster cannot be the closing window." });
        }

        // Load the whole camp's windows: marking one has to unmark the others, and clearing an
        // explicit price needs the close bonus resolved off the camp.
        var siblings = await _dbContext.EventAttendanceWindows
            .Where(w => w.EventId == window.EventId)
            .ToListAsync(cancellationToken);

        foreach (var sibling in siblings)
        {
            // Exactly one close per camp, enforced here rather than trusted to callers. Two marked
            // windows would make ResolveCloseWindow's answer depend on row order.
            sibling.IsClosingWindow = isClosing && sibling.Id == window.Id;
        }

        if (isClosing)
        {
            // THE point of the checkbox. The window was posted at whatever it was worth at the
            // time — the open on window 1, the regular rate elsewhere — and the addon stamped that
            // as an explicit DkpAmount. An explicit amount REPLACES the computed value, so leaving
            // it in place would tick the box and pay nothing extra.
            //
            // Clearing it is what lets the close bonus through: the window falls back to the camp's
            // computed price, and WindowValue prices a marked close at the close bonus. That is the
            // rule as stated — once a window is the closing window, the linkshell's close DKP
            // becomes its value.
            //
            // With ONE exception, and it is WindowValue's, not this endpoint's: ticking sequence 1
            // still prices as the OPEN, because a window never pays two amounts and sequence 1 is
            // the open by definition. A camp that opened and closed in a single roster read is the
            // officer's to settle — they add the close from the review page, where the amounts are
            // editable. The tick is still recorded and still honoured; it just has nothing left to
            // change on window 1.
            //
            // Destructive to a hand-typed price, deliberately. "This is the close" is the more
            // specific statement of the two, and the officer can re-price the window afterwards if
            // they really did mean a one-off number.
            window.DkpAmount = null;
        }

        await _dbContext.SaveChangesAsync(cancellationToken);

        var linkshell = await _dbContext.Linkshells
            .AsNoTracking()
            .FirstOrDefaultAsync(l => l.Id == eventEntity.LinkshellId, cancellationToken);
        var closeWindow = HnmStandardCampFinalizer.ResolveCloseWindow(
            siblings, eventEntity.HnmWindowNumber);

        return Ok(new
        {
            eventId = window.EventId,
            sequenceNumber = window.SequenceNumber,
            isClosingWindow = window.IsClosingWindow,
            dkpAmount = window.DkpAmount,
            // Echoed so both clients can repaint the whole column without a refetch: unmarking
            // hands the close back to the derived fallback, which may land on a different row.
            closeWindow,
            dkpValue = HnmCampPricing.WindowValueFor(
                eventEntity, linkshell, window.SequenceNumber, closeWindow,
                window.DkpAmount, window.IsKillWindow)
        });
    }

    public sealed record AddonSetClosingWindowRequest(bool IsClosingWindow);
}
