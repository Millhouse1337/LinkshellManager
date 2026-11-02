using LinkshellManagerDiscordApp.Authorization;
using LinkshellManagerDiscordApp.Models;
using LinkshellManagerDiscordApp.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace LinkshellManagerDiscordApp.Controllers;

// The addon's Unlinked Posts panel: the captures nobody has filed yet, and the two ways to file
// one -- onto a live camp or open attendance event, or onto a new attendance event made from it.
// Either way it lands as a misc post; there is no post type to choose.
// The in-game twin of the Activity's Unlinked Snapshots row; both file through
// SnapshotFilingService, so the rules cannot drift.
public sealed partial class AddonApiController
{
    // A capture's name and the event or camp to file it on. Name is the POST's name (the label the
    // app shows on it); Make New Event also names the new event after it. An older addon that still
    // sends slotKind / windowNumber is fine: unknown fields are ignored, and the post lands as Misc.
    public sealed record AddonAttachSnapshotRequest(
        int? WindowEventId, int? LinkedEventId, bool CreateNew, string? Name);

    // GET /api/addon/attendance-snapshots/unlinked
    //
    // Same list as the Activity's Unlinked Snapshots (awaiting confirmation first, then newest),
    // plus what each one can be filed on: live HNM camps (started, not ended) and open attendance
    // events. Headers only -- the addon's HTTP is a blocking curl on the render thread.
    [HttpGet("attendance-snapshots/unlinked")]
    [AddonApiAuth]
    public async Task<IActionResult> GetUnlinkedSnapshotsAsync(CancellationToken cancellationToken)
    {
        var token = AddonApiAuthAttribute.GetToken(HttpContext);
        var linkshellId = token.LinkshellId;

        var snapshots = await _dbContext.AttendanceSnapshots
            .AsNoTracking()
            .Where(s => s.LinkshellId == linkshellId && s.WindowEventId == null && s.LinkedEventId == null
                        && s.SnapshotStatus != AttendanceSnapshotStatuses.Ignored)
            .OrderBy(s => s.SnapshotStatus == AttendanceSnapshotStatuses.Pending ? 0 : 1)
            .ThenByDescending(s => s.CapturedAtUtc)
            .Take(25)
            .Select(s => new
            {
                id = s.Id,
                capturedAtUtc = s.CapturedAtUtc,
                capturedByCharacterName = s.CapturedByCharacterName,
                name = s.Name,
                entryCount = s.EntryCount,
                allianceNumber = s.AllianceNumber,
                awaitingVerification = s.SnapshotStatus == AttendanceSnapshotStatuses.Pending,
            })
            .ToListAsync(cancellationToken);

        // EventType is free text ("HNM", "hnm "), so the HNM test runs after the fetch.
        var liveEvents = await _dbContext.Events
            .AsNoTracking()
            .Where(e => e.LinkshellId == linkshellId && e.CommencementStartTime != null && e.EndTime == null)
            .OrderBy(e => e.CommencementStartTime)
            .Select(e => new { e.Id, e.EventName, e.EventType })
            .ToListAsync(cancellationToken);
        var camps = liveEvents
            .Where(e => string.Equals((e.EventType ?? string.Empty).Trim(), "HNM", StringComparison.OrdinalIgnoreCase))
            .Select(e => new { id = e.Id, name = e.EventName })
            .ToList();

        var events = await AttendanceQueryFilters.ApplyLiveFilter(
                _dbContext.WindowEvents.AsNoTracking().Where(e => e.LinkshellId == linkshellId))
            .OrderByDescending(e => e.LastCapturedAtUtc)
            .Select(e => new { id = e.Id, name = e.Name })
            .ToListAsync(cancellationToken);

        return Ok(new { snapshots, camps, events });
    }

    // POST /api/addon/attendance-snapshots/{id}/attach
    //
    // Files one unlinked capture. Same permission as the addon's other attendance writes. A capture
    // someone already filed is refused rather than moved: from the game you cannot see where it
    // went, and moving it would shift who gets paid on a camp you are not looking at.
    [HttpPost("attendance-snapshots/{id:int}/attach")]
    [AddonApiAuth]
    public async Task<IActionResult> AttachUnlinkedSnapshotAsync(
        int id,
        [FromBody] AddonAttachSnapshotRequest request,
        [FromServices] SnapshotFilingService filing,
        CancellationToken cancellationToken)
    {
        var token = AddonApiAuthAttribute.GetToken(HttpContext);

        var snapshot = await _dbContext.AttendanceSnapshots
            .Include(s => s.Entries)
            .FirstOrDefaultAsync(s => s.Id == id, cancellationToken);
        if (snapshot is null) return NotFound(new { error = "Post not found." });
        if (snapshot.LinkshellId != token.LinkshellId) return Forbid();
        if (!await TokenIssuerCanModerateAsync(token, snapshot.LinkshellId, cancellationToken))
        {
            return Forbid();
        }
        if (!WindowEventLinkService.IsUnlinked(snapshot))
        {
            return BadRequest(new { error = "This post is already filed. Change it in the app." });
        }

        // The Message box is the post's name, the same field the app's label shows. A blank box
        // clears it, as it does in the app.
        if (request.Name is not null)
        {
            snapshot.Name = SnapshotFilingService.TrimToNull(request.Name, 128);
        }

        var result = await filing.FileAsync(snapshot, new SnapshotFilingService.Request(
            request.WindowEventId, snapshot.Name, request.LinkedEventId, request.CreateNew), cancellationToken);
        if (result.Error is not null) return StatusCode(result.StatusCode, new { error = result.Error });
        return Ok(new { success = true, filedAgainstCamp = result.FiledAgainstCamp });
    }
}
