using LinkshellManagerDiscordApp.Authorization;
using LinkshellManagerDiscordApp.Models;
using LinkshellManagerDiscordApp.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace LinkshellManagerDiscordApp.Controllers;

// Pricing ONE misc post: the misc-post twin of SetWindowDkpAddonAsync.
//
// AttendanceSnapshot.DkpAmount is what an officer says this post pays each person on it,
// replacing the linkshell's Misc post rate. Same rules as a window price: moderators only, live
// Standard HNM camps only (or a post not filed yet, which carries the price to wherever it is
// filed), snapped to the linkshell's DKP grid on write, and null clears it.
//
// Two routes over one body, like WindowDkp: the addon posts with a pairing token, the web and the
// Activity with a cookie or bearer.
public sealed partial class AddonApiController
{
    // PATCH /api/addon/attendance-snapshots/{id}/dkp   (in-game addon, token auth)
    [HttpPatch("attendance-snapshots/{id:int}/dkp")]
    [AddonApiAuth]
    public async Task<IActionResult> SetMiscPostDkpAddonAsync(
        int id,
        [FromBody] AddonSetWindowDkpRequest request,
        CancellationToken cancellationToken)
    {
        var token = AddonApiAuthAttribute.GetToken(HttpContext);

        var snapshot = await _dbContext.AttendanceSnapshots
            .Include(s => s.LinkedEvent)
            .FirstOrDefaultAsync(s => s.Id == id, cancellationToken);
        if (snapshot is null) return NotFound(new { error = "Misc post not found." });
        if (snapshot.LinkshellId != token.LinkshellId) return Forbid();
        if (!await TokenIssuerCanModerateAsync(token, snapshot.LinkshellId, cancellationToken))
        {
            return Forbid();
        }

        return await SetMiscPostDkpAsync(snapshot, request.DkpAmount, cancellationToken);
    }

    // PATCH /api/addon/management/attendance-snapshots/{id}/dkp   (web + Activity, cookie/bearer)
    //
    // The Activity's Linked Snapshots table edits a misc post's DKP in place.
    [HttpPatch("management/attendance-snapshots/{id:int}/dkp")]
    public async Task<IActionResult> SetMiscPostDkpManagementAsync(
        int id,
        [FromBody] AddonSetWindowDkpRequest request,
        CancellationToken cancellationToken)
    {
        var appUser = await ResolveManagementUserAsync(cancellationToken);
        if (appUser is null) return Unauthorized(new { error = "Not signed in. Open /Identity/Account/Login on this same host first, or launch the activity inside Discord." });

        var snapshot = await _dbContext.AttendanceSnapshots
            .Include(s => s.LinkedEvent)
            .FirstOrDefaultAsync(s => s.Id == id, cancellationToken);
        if (snapshot is null) return NotFound(new { error = "Misc post not found." });

        var membership = await _dbContext.AppUserLinkshells
            .FirstOrDefaultAsync(
                m => m.AppUserId == appUser.Id && m.LinkshellId == snapshot.LinkshellId,
                cancellationToken);
        if (!CanManageLinkshell(membership)) return Forbid();

        return await SetMiscPostDkpAsync(snapshot, request.DkpAmount, cancellationToken);
    }

    // The shared body. Callers have already authorized; this owns validation and the write.
    private async Task<IActionResult> SetMiscPostDkpAsync(
        AttendanceSnapshot snapshot, double? amount, CancellationToken cancellationToken)
    {
        // An UNLINKED post can carry its own price before it is filed -- the Unlinked Snapshots
        // row's DKP box. It is filed as Misc wherever it lands (SnapshotFilingService), so the
        // slot test below does not apply to it yet. The price then pays on a live camp
        // (HnmCampPricing.MiscValueFor) or becomes a new event's Misc DKP.
        var unlinked = WindowEventLinkService.IsUnlinked(snapshot);
        var camp = snapshot.LinkedEvent;
        if (!unlinked)
        {
            if (!AttendanceSnapshotSlotKinds.IsMisc(snapshot.SlotKind))
            {
                return BadRequest(new { error = "Only a misc post is priced this way. A window is priced on its own tab." });
            }

            // A filed misc post is priced against the live camp it was filed on. Once that camp
            // ends, End Camp has carried the amount onto the review card, and the correction
            // belongs there.
            if (camp is null)
            {
                return BadRequest(new { error = "This post is on an attendance event. Its Misc DKP is set on that event's card." });
            }
            if (camp.EndTime is not null)
            {
                return BadRequest(new
                {
                    error = "This camp has ended. Change the amount on its review card under "
                          + "Events Pending DKP Post instead.",
                });
            }
            if (!HnmCampPricing.HonoursWindowAmount(camp))
            {
                return BadRequest(new
                {
                    error = DiscordEventMessageBuilder.IsWd(camp)
                        ? "This camp credits attendance from Check In to Check Out, so a misc post has no price of its own."
                        : "Only HNM camps price misc posts."
                });
            }
        }

        var linkshell = await _dbContext.Linkshells
            .AsNoTracking()
            .FirstOrDefaultAsync(l => l.Id == snapshot.LinkshellId, cancellationToken);

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
            // Snapped on write for the same reason a window price is: showing an off-grid number
            // would promise a payout nothing is going to make.
            resolved = DkpRounding.Round(value, DkpRounding.StepFor(linkshell?.DkpRoundingIncrement));
        }

        snapshot.DkpAmount = resolved;
        await _dbContext.SaveChangesAsync(cancellationToken);

        return Ok(new
        {
            snapshotId = snapshot.Id,
            // The post's own price, or null when it is back on the linkshell rate.
            dkpOverride = snapshot.DkpAmount,
            // What it pays each person now, whichever of the two that came from. An unlinked post
            // has no camp yet, so its fallback is the linkshell's Misc post rate itself.
            dkpAmount = unlinked
                ? snapshot.DkpAmount ?? linkshell?.HnmStandardMiscBonus ?? 0d
                : HnmCampPricing.MiscValueFor(camp, linkshell, snapshot.DkpAmount),
        });
    }
}
