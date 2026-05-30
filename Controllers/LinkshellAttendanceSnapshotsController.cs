using System.Globalization;
using LinkshellManagerDiscordApp.Data;
using LinkshellManagerDiscordApp.Models;
using LinkshellManagerDiscordApp.Services;
using LinkshellManagerDiscordApp.ViewModels;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NodaTime;

namespace LinkshellManagerDiscordApp.Controllers;

[Authorize]
public sealed class LinkshellAttendanceSnapshotsController : Controller
{
    private const int MaxRecentSnapshots = 100;

    private readonly ApplicationDbContext _db;
    private readonly UserManager<AppUser> _userManager;
    private readonly TimeZoneConversionService _timeZones;

    public LinkshellAttendanceSnapshotsController(
        ApplicationDbContext db,
        UserManager<AppUser> userManager,
        TimeZoneConversionService timeZones)
    {
        _db = db;
        _userManager = userManager;
        _timeZones = timeZones;
    }

    [HttpGet("/linkshells/{linkshellId:int}/attendance-snapshots")]
    public async Task<IActionResult> Index(int linkshellId, CancellationToken cancellationToken)
    {
        await Task.CompletedTask;
        return RedirectToAction("Index", "WindowEvents", new { linkshellId });

#pragma warning disable CS0162
        var user = await _userManager.GetUserAsync(User);
        if (user is null) return Challenge();

        var isMember = await _db.AppUserLinkshells
            .AsNoTracking()
            .AnyAsync(link => link.AppUserId == user.Id && link.LinkshellId == linkshellId, cancellationToken);
        if (!isMember) return Forbid();

        var linkshell = await _db.Linkshells
            .AsNoTracking()
            .Where(l => l.Id == linkshellId)
            .Select(l => new { l.LinkshellName })
            .FirstOrDefaultAsync(cancellationToken);
        if (linkshell is null) return NotFound();

        // Pull the most recent N snapshots with their entries pre-loaded.
        // Each snapshot caps at 18 entries (alliance max), so eager-loading is
        // safe and avoids per-row queries on the view.
        var snapshots = await _db.AttendanceSnapshots
            .AsNoTracking()
            .Where(s => s.LinkshellId == linkshellId)
            .OrderByDescending(s => s.CapturedAtUtc)
            .Take(MaxRecentSnapshots)
            .Include(s => s.Entries)
            .Include(s => s.LinkedEvent)
            .ToListAsync(cancellationToken);

        // Selectable events for the "Link to event" dropdown on unlinked rows.
        // Queued + live only (anything not yet ended); a leader linking a
        // closed event is rare and can re-open via Edit.
        var selectableEvents = await _db.Events
            .AsNoTracking()
            .Where(e => e.LinkshellId == linkshellId && e.EndTime == null)
            .OrderByDescending(e => e.CommencementStartTime ?? DateTime.MinValue)
            .ThenByDescending(e => e.StartTime ?? DateTime.MinValue)
            .Select(e => new SelectableEventOption
            {
                Id = e.Id,
                Name = e.EventName ?? $"Event #{e.Id}",
                IsLive = e.CommencementStartTime != null,
            })
            .ToListAsync(cancellationToken);

        var userTimeZoneId = user.TimeZone;
        var userZone = _timeZones.Resolve(userTimeZoneId);
        var isUserZoneUtc = ReferenceEquals(userZone, DateTimeZone.Utc);

        var rows = snapshots.Select(s =>
        {
            var entries = s.Entries
                .OrderBy(e => e.CharacterName, StringComparer.OrdinalIgnoreCase)
                .Select(e => new AttendanceSnapshotEntryRow
                {
                    CharacterName = e.CharacterName,
                    MainJob = e.MainJob,
                    MainJobLevel = e.MainJobLevel,
                    SubJob = e.SubJob,
                    SubJobLevel = e.SubJobLevel,
                    Zone = e.Zone,
                })
                .ToList();

            // Pick the most common zone among entries as the snapshot's
            // primary zone for the header line. Mixed-zone alliances are rare
            // but the mode (most-frequent) is the most representative value.
            var primaryZone = entries
                .Where(e => !string.IsNullOrWhiteSpace(e.Zone))
                .GroupBy(e => e.Zone!, StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(g => g.Count())
                .ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.Key)
                .FirstOrDefault();

            var capturedUtcDisplay = FormatPretty(s.CapturedAtUtc, "UTC");

            string? capturedLocalDisplay = null;
            if (!isUserZoneUtc)
            {
                var instant = Instant.FromDateTimeUtc(DateTime.SpecifyKind(s.CapturedAtUtc, DateTimeKind.Utc));
                var local = instant.InZone(userZone);
                var localAbbr = userZone.GetZoneInterval(instant).Name;
                capturedLocalDisplay = FormatPretty(local.ToDateTimeUnspecified(), localAbbr);
            }

            return new AttendanceSnapshotRow
            {
                Id = s.Id,
                CapturedAtUtc = s.CapturedAtUtc,
                CapturedByCharacterName = s.CapturedByCharacterName,
                UtcOffset = s.UtcOffset,
                EntryCount = s.EntryCount,
                PrimaryZone = primaryZone,
                Entries = entries,
                CapturedAtUtcDisplay = capturedUtcDisplay,
                CapturedAtLocalDisplay = capturedLocalDisplay,
                Name = s.Name,
                LinkedEventId = s.LinkedEventId,
                LinkedEventName = s.LinkedEvent?.EventName,
                LinkedEventIsLive = s.LinkedEvent != null && s.LinkedEvent.CommencementStartTime != null && s.LinkedEvent.EndTime == null,
            };
        }).ToList();

        // Renaming is gated to leader / officer (rank-based) to keep members
        // from relabeling other people's captures.
        var membership = await _db.AppUserLinkshells
            .AsNoTracking()
            .FirstOrDefaultAsync(link => link.AppUserId == user.Id && link.LinkshellId == linkshellId, cancellationToken);
        var canRename = membership is not null && LinkshellRanks.IsLeaderOrOfficer(membership.Rank);

        var viewModel = new LinkshellAttendanceSnapshotsViewModel
        {
            LinkshellId = linkshellId,
            LinkshellName = linkshell.LinkshellName,
            Snapshots = rows,
            CanRename = canRename,
            CanManageEvents = canRename,
            SelectableEvents = selectableEvents,
        };
        return View(viewModel);
#pragma warning restore CS0162
    }

    [HttpPost("/linkshells/{linkshellId:int}/attendance-snapshots/{snapshotId:int}/rename")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Rename(int linkshellId, int snapshotId, [FromForm] string? name, CancellationToken cancellationToken)
    {
        var user = await _userManager.GetUserAsync(User);
        if (user is null) return Challenge();

        var membership = await _db.AppUserLinkshells
            .AsNoTracking()
            .FirstOrDefaultAsync(link => link.AppUserId == user.Id && link.LinkshellId == linkshellId, cancellationToken);
        if (membership is null) return Forbid();
        var rank = membership.Rank ?? string.Empty;
        if (!LinkshellRanks.IsLeaderOrOfficer(rank))
        {
            return Forbid();
        }

        var snapshot = await _db.AttendanceSnapshots
            .FirstOrDefaultAsync(s => s.Id == snapshotId && s.LinkshellId == linkshellId, cancellationToken);
        if (snapshot is null) return NotFound();

        var trimmed = string.IsNullOrWhiteSpace(name) ? null : name.Trim();
        if (trimmed is { Length: > 128 }) trimmed = trimmed[..128];
        snapshot.Name = trimmed;
        await _db.SaveChangesAsync(cancellationToken);

        return RedirectToAction(nameof(Index), new { linkshellId });
    }

    [HttpPost("/linkshells/{linkshellId:int}/attendance-snapshots/{snapshotId:int}/link")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Link(int linkshellId, int snapshotId, [FromForm] int eventId, CancellationToken cancellationToken)
    {
        if (await RequireOfficerAsync(linkshellId, cancellationToken) is { } reject) return reject;

        var snapshot = await _db.AttendanceSnapshots
            .FirstOrDefaultAsync(s => s.Id == snapshotId && s.LinkshellId == linkshellId, cancellationToken);
        if (snapshot is null) return NotFound();

        // Same-linkshell + not-ended guard. Linking to an ended event would
        // imply attendance for a closed run; reject so a confused officer
        // doesn't accidentally muddy the audit trail.
        var evt = await _db.Events
            .Where(e => e.Id == eventId && e.LinkshellId == linkshellId && e.EndTime == null)
            .Select(e => new { e.Id })
            .FirstOrDefaultAsync(cancellationToken);
        if (evt is null) return NotFound();

        snapshot.LinkedEventId = evt.Id;
        await _db.SaveChangesAsync(cancellationToken);
        return RedirectToAction(nameof(Index), new { linkshellId });
    }

    [HttpPost("/linkshells/{linkshellId:int}/attendance-snapshots/{snapshotId:int}/unlink")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Unlink(int linkshellId, int snapshotId, CancellationToken cancellationToken)
    {
        if (await RequireOfficerAsync(linkshellId, cancellationToken) is { } reject) return reject;

        var snapshot = await _db.AttendanceSnapshots
            .FirstOrDefaultAsync(s => s.Id == snapshotId && s.LinkshellId == linkshellId, cancellationToken);
        if (snapshot is null) return NotFound();

        snapshot.LinkedEventId = null;
        await _db.SaveChangesAsync(cancellationToken);
        return RedirectToAction(nameof(Index), new { linkshellId });
    }

    [HttpPost("/linkshells/{linkshellId:int}/attendance-snapshots/{snapshotId:int}/quick-create-event")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> QuickCreateEvent(int linkshellId, int snapshotId, [FromForm] string? name, [FromForm] string? primaryZone, CancellationToken cancellationToken)
    {
        if (await RequireOfficerAsync(linkshellId, cancellationToken) is { } reject) return reject;

        var user = await _userManager.GetUserAsync(User);
        if (user is null) return Challenge();

        var snapshot = await _db.AttendanceSnapshots
            .FirstOrDefaultAsync(s => s.Id == snapshotId && s.LinkshellId == linkshellId, cancellationToken);
        if (snapshot is null) return NotFound();

        var trimmedName = (name ?? string.Empty).Trim();
        if (string.IsNullOrEmpty(trimmedName)) return BadRequest("Event name is required.");
        if (trimmedName.Length > 64) trimmedName = trimmedName[..64];

        // No per-LS default for event DKP rate lives in the schema (those
        // defaults are addon-side user settings). Pragmatic default = 1 DKP/hr
        // so the quick-created event is immediately usable; the leader can
        // adjust on the Event edit page if needed.
        const int dkpPerHour = 1;

        var nowUtc = DateTime.UtcNow;
        var trimmedZone = string.IsNullOrWhiteSpace(primaryZone) ? null : primaryZone.Trim();
        if (trimmedZone is { Length: > 256 }) trimmedZone = trimmedZone[..256];

        var newEvent = new Event
        {
            LinkshellId = linkshellId,
            EventName = trimmedName,
            EventType = null,
            EventLocation = trimmedZone,
            CreatorUserId = user.Id,
            StartTime = snapshot.CapturedAtUtc,
            CommencementStartTime = null,
            DkpPerHour = dkpPerHour,
            Details = $"Quick-created from Attendance Snapshot #{snapshot.Id}.",
            CreationSource = "SnapshotQuickCreate",
            TimeStamp = nowUtc,
        };
        _db.Events.Add(newEvent);
        await _db.SaveChangesAsync(cancellationToken);

        snapshot.LinkedEventId = newEvent.Id;
        await _db.SaveChangesAsync(cancellationToken);

        return RedirectToAction(nameof(Index), new { linkshellId });
    }

    // Guard helper. Returns null when the caller is a Leader/Officer in the
    // given linkshell, otherwise returns a Challenge / Forbid action result the
    // caller should immediately return. Centralises the same rank gate the
    // Rename action uses so the three new actions stay consistent.
    private async Task<IActionResult?> RequireOfficerAsync(int linkshellId, CancellationToken cancellationToken)
    {
        var user = await _userManager.GetUserAsync(User);
        if (user is null) return Challenge();
        var membership = await _db.AppUserLinkshells
            .AsNoTracking()
            .FirstOrDefaultAsync(link => link.AppUserId == user.Id && link.LinkshellId == linkshellId, cancellationToken);
        if (membership is null) return Forbid();
        var rank = membership.Rank ?? string.Empty;
        if (!LinkshellRanks.IsLeaderOrOfficer(rank))
        {
            return Forbid();
        }
        return null;
    }

    // Renders a DateTime as e.g. "May 14th 2026 5:47pm UTC" -- the format
    // requested for the snapshot list. The .NET stock format strings don't
    // include ordinal suffixes (1st/2nd/3rd/4th), so the day part is built
    // by hand. The lowercase am/pm matches the rest of the human-readable
    // display strings on the page.
    private static string FormatPretty(DateTime dt, string zoneSuffix)
    {
        var day = dt.Day;
        var suffix = (day % 100) is >= 11 and <= 13
            ? "th"
            : (day % 10) switch { 1 => "st", 2 => "nd", 3 => "rd", _ => "th" };
        var month = dt.ToString("MMMM", CultureInfo.InvariantCulture);
        var year = dt.Year.ToString(CultureInfo.InvariantCulture);
        var time = dt.ToString("h:mm", CultureInfo.InvariantCulture);
        var meridian = dt.ToString("tt", CultureInfo.InvariantCulture).ToLowerInvariant();
        return $"{month} {day}{suffix} {year} {time}{meridian} {zoneSuffix}";
    }
}
