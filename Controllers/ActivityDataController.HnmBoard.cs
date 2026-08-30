using LinkshellManagerDiscordApp.Models;
using LinkshellManagerDiscordApp.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace LinkshellManagerDiscordApp.Controllers;

public sealed partial class ActivityDataController
{
    // Logs (or edits) the monster's Time of Death from an HNM signup board's "Post ToD" /
    // "Edit ToD" button. It records the ToD (which drives the recurring-board re-post),
    // moves the event's StartTime to the predicted repop, wipes the board's signups, marks
    // it "defeated / awaiting re-post", and replaces the Discord board message with a
    // defeated note. The HnmRecurringBoardBackgroundService later clears the flag and
    // re-posts THIS same board LeadHours before the pop (one card that cycles).
    [HttpPost("events/{eventId:int}/post-tod")]
    public async Task<IActionResult> PostBoardTodAsync(
        int eventId,
        [FromBody] ActivityPostBoardTodRequest request,
        CancellationToken cancellationToken)
    {
        var appUser = await ResolveAppUserAsync(cancellationToken);
        if (appUser is null)
        {
            return Unauthorized(new { error = "Sign in to log a Time of Death." });
        }

        var eventEntity = await _dbContext.Events
            .FirstOrDefaultAsync(e => e.Id == eventId, cancellationToken);
        if (eventEntity is null)
        {
            return NotFound(new { error = "The event was not found." });
        }

        var membership = await GetMembershipAsync(appUser.Id, eventEntity.LinkshellId, cancellationToken);
        if (!await CanAsync(membership, r => r.CanManageEvents, cancellationToken))
        {
            return Forbid();
        }

        var isHnm = string.Equals((eventEntity.EventType ?? string.Empty).Trim(), "HNM", StringComparison.OrdinalIgnoreCase);
        if (!isHnm)
        {
            return BadRequest(new { error = "Time of Death can only be posted from an HNM signup board." });
        }

        var monsterName = eventEntity.AssignedMonsterName?.Trim();
        if (string.IsNullOrWhiteSpace(monsterName))
        {
            return BadRequest(new { error = "This HNM board has no monster assigned." });
        }

        // A blank time means the camp ended without anyone seeing it die (the window closed, or
        // another linkshell took it): record no ToD and no repop rather than inventing "now".
        // Only a non-blank value that won't parse is an error — a ToD is never silently guessed.
        DateTime? todTimeUtc = null;
        if (!string.IsNullOrWhiteSpace(request.TimeLocal))
        {
            if (!TryConvertUserTimeZoneToUtc(request.TimeLocal, appUser.TimeZone, out todTimeUtc) || !todTimeUtc.HasValue)
            {
                return BadRequest(new { error = "Enter a valid Time of Death using your local time." });
            }
        }

        var cooldown = string.IsNullOrWhiteSpace(request.Cooldown)
            ? await GetDefaultTodCooldownAsync(_monsterTimings, eventEntity.LinkshellId, monsterName, cancellationToken)
            : request.Cooldown.Trim();
        if (!IsAcceptableTodCooldown(cooldown))
        {
            return BadRequest(new { error = "Enter a valid cooldown (e.g. 22 Hour, 72 Hour, or a positive number of hours)." });
        }

        var interval = request.Interval?.Trim();
        if (string.IsNullOrWhiteSpace(interval))
        {
            interval = null;
        }
        else if (!IsAcceptableTodInterval(interval))
        {
            // Was a strict preset check while the cooldown beside it already accepted free text,
            // so End Camp rejected any interval a linkshell had actually configured.
            return BadRequest(new { error = "Enter a valid interval (a positive number of hours or minutes)." });
        }

        // Hand the validated inputs to the shared pop service, which logs (or edits) the ToD,
        // re-points the board to the next repop, tears the camp down, and stages its roster as a
        // pending review row in the Event System page's attendance sections (both modes). Identical to the Discord
        // "🏁 Pop / End Camp" path, which calls the same service.
        var popService = HttpContext.RequestServices.GetRequiredService<HnmCampPopService>();
        var result = await popService.PopAsync(new HnmCampPopService.Request(
            EventId: eventId,
            TodTimeUtc: todTimeUtc,
            Cooldown: cooldown,
            Interval: interval,
            DayNumber: request.DayNumber,
            Claimed: request.Claim,
            Killed: request.Killed,
            PopWindow: request.PopWindow,
            AdditionalSeconds: Math.Max(0, request.AdditionalSeconds),
            Hq: request.Hq,
            Repost: request.Repost,
            RepostLeadHours: request.RepostLeadHours,
            ImagePath: SanitizeUploadedImagePath(request.ImagePath)), cancellationToken);

        if (!result.Success)
        {
            return BadRequest(new { error = result.Error ?? "Couldn't post the Time of Death." });
        }

        return Ok(new
        {
            success = true,
            repopTimeUtc = result.RepopTimeUtc,
            repostAtUtc = result.RepostAtUtc
        });
    }
}
