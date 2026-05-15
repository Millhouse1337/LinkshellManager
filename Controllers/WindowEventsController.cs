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
public sealed class WindowEventsController : Controller
{
    private const int MaxUnlinkedSnapshots = 100;
    private const int MaxClosedEvents = 25;

    private readonly ApplicationDbContext _db;
    private readonly UserManager<AppUser> _userManager;
    private readonly TimeZoneConversionService _timeZones;
    private readonly SheetSyncQueue _sheetSync;

    public WindowEventsController(
        ApplicationDbContext db,
        UserManager<AppUser> userManager,
        TimeZoneConversionService timeZones,
        SheetSyncQueue sheetSync)
    {
        _db = db;
        _userManager = userManager;
        _timeZones = timeZones;
        _sheetSync = sheetSync;
    }

    [HttpGet("/linkshells/{linkshellId:int}/window-events")]
    public async Task<IActionResult> Index(int linkshellId, CancellationToken cancellationToken)
    {
        var user = await _userManager.GetUserAsync(User);
        if (user is null) return Challenge();

        var membership = await _db.AppUserLinkshells
            .AsNoTracking()
            .FirstOrDefaultAsync(link => link.AppUserId == user.Id && link.LinkshellId == linkshellId, cancellationToken);
        if (membership is null) return Forbid();

        var linkshell = await _db.Linkshells
            .AsNoTracking()
            .Where(l => l.Id == linkshellId)
            .Select(l => new { l.LinkshellName })
            .FirstOrDefaultAsync(cancellationToken);
        if (linkshell is null) return NotFound();

        var canManage = IsLeaderOrOfficer(membership);
        var zone = _timeZones.Resolve(user.TimeZone);

        var openEvents = await _db.WindowEvents
            .AsNoTracking()
            .Where(e => e.LinkshellId == linkshellId && e.Status == WindowEventStatuses.Open)
            .OrderByDescending(e => e.LastCapturedAtUtc)
            .Include(e => e.Snapshots).ThenInclude(s => s.Entries)
            .ToListAsync(cancellationToken);

        var closedEvents = await _db.WindowEvents
            .AsNoTracking()
            .Where(e => e.LinkshellId == linkshellId && e.Status == WindowEventStatuses.Closed)
            .OrderByDescending(e => e.LastCapturedAtUtc)
            .Take(MaxClosedEvents)
            .Include(e => e.Snapshots).ThenInclude(s => s.Entries)
            .ToListAsync(cancellationToken);

        var unlinked = await _db.AttendanceSnapshots
            .AsNoTracking()
            .Where(s => s.LinkshellId == linkshellId && s.WindowEventId == null && s.SnapshotStatus != AttendanceSnapshotStatuses.Ignored)
            .OrderByDescending(s => s.CapturedAtUtc)
            .Take(MaxUnlinkedSnapshots)
            .Include(s => s.Entries)
            .ToListAsync(cancellationToken);

        var vm = new WindowEventsViewModel
        {
            LinkshellId = linkshellId,
            LinkshellName = linkshell.LinkshellName,
            CanManage = canManage,
            OpenEvents = openEvents.Select(e => MapWindowEvent(e, zone)).ToList(),
            ClosedEvents = closedEvents.Select(e => MapWindowEvent(e, zone)).ToList(),
            UnlinkedSnapshots = unlinked.Select(s => MapSnapshot(s, zone)).ToList(),
        };

        return View(vm);
    }

    [HttpPost("/linkshells/{linkshellId:int}/window-events/{windowEventId:int}/rename")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Rename(int linkshellId, int windowEventId, [FromForm] string? name, CancellationToken cancellationToken)
    {
        if (await RequireOfficerAsync(linkshellId, cancellationToken) is { } reject) return reject;

        var windowEvent = await _db.WindowEvents
            .FirstOrDefaultAsync(e => e.Id == windowEventId && e.LinkshellId == linkshellId, cancellationToken);
        if (windowEvent is null) return NotFound();

        var trimmed = TrimToNull(name, 128);
        if (trimmed is null)
        {
            TempData["WindowEventError"] = "Window Event name is required.";
            return RedirectToAction(nameof(Index), new { linkshellId });
        }

        windowEvent.Name = trimmed;
        windowEvent.NormalizedName = NormalizeName(trimmed);
        await _db.SaveChangesAsync(cancellationToken);
        return RedirectToAction(nameof(Index), new { linkshellId });
    }

    [HttpPost("/linkshells/{linkshellId:int}/window-events/{windowEventId:int}/close")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Close(int linkshellId, int windowEventId, CancellationToken cancellationToken)
    {
        if (await RequireOfficerAsync(linkshellId, cancellationToken) is { } reject) return reject;

        var windowEvent = await _db.WindowEvents
            .FirstOrDefaultAsync(e => e.Id == windowEventId && e.LinkshellId == linkshellId, cancellationToken);
        if (windowEvent is null) return NotFound();

        windowEvent.Status = WindowEventStatuses.Closed;
        windowEvent.ClosedAtUtc = DateTime.UtcNow;
        await _db.SaveChangesAsync(cancellationToken);
        return RedirectToAction(nameof(Index), new { linkshellId });
    }

    [HttpPost("/linkshells/{linkshellId:int}/window-events/{windowEventId:int}/reopen")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Reopen(int linkshellId, int windowEventId, CancellationToken cancellationToken)
    {
        if (await RequireOfficerAsync(linkshellId, cancellationToken) is { } reject) return reject;

        var windowEvent = await _db.WindowEvents
            .FirstOrDefaultAsync(e => e.Id == windowEventId && e.LinkshellId == linkshellId, cancellationToken);
        if (windowEvent is null) return NotFound();

        windowEvent.Status = WindowEventStatuses.Open;
        windowEvent.ClosedAtUtc = null;
        await _db.SaveChangesAsync(cancellationToken);
        return RedirectToAction(nameof(Index), new { linkshellId });
    }

    // Persists DKP + Entry Type on the Window Event and enqueues the AttInput
    // append job. Both values are required because the downstream sheet
    // formulas pivot on column K (Entry Type) and column J (DKP); pushing
    // either blank would either skip rows entirely or credit zero.
    [HttpPost("/linkshells/{linkshellId:int}/window-events/{windowEventId:int}/post-to-sheet")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> PostToSheet(
        int linkshellId,
        int windowEventId,
        [FromForm] double? dkpAmount,
        [FromForm] string? entryType,
        CancellationToken cancellationToken)
    {
        if (await RequireOfficerAsync(linkshellId, cancellationToken) is { } reject) return reject;

        var windowEvent = await _db.WindowEvents
            .FirstOrDefaultAsync(e => e.Id == windowEventId && e.LinkshellId == linkshellId, cancellationToken);
        if (windowEvent is null) return NotFound();

        if (!dkpAmount.HasValue || dkpAmount.Value < 0)
        {
            TempData["WindowEventError"] = "DKP amount is required and must be zero or greater.";
            return RedirectToAction(nameof(Index), new { linkshellId });
        }
        if (!WindowEventEntryTypes.IsValid(entryType))
        {
            TempData["WindowEventError"] =
                $"Entry Type must be one of: {string.Join(", ", WindowEventEntryTypes.All)}.";
            return RedirectToAction(nameof(Index), new { linkshellId });
        }
        if (windowEvent.PostedToSheetAt.HasValue)
        {
            TempData["WindowEventError"] = "This Window Event has already been posted to the DKP sheet.";
            return RedirectToAction(nameof(Index), new { linkshellId });
        }

        windowEvent.DkpAmount = dkpAmount.Value;
        windowEvent.EntryType = entryType;
        await _db.SaveChangesAsync(cancellationToken);

        await _sheetSync.EnqueueWindowEventPostAsync(windowEvent.Id, cancellationToken);

        TempData["WindowEventStatus"] = $"Posting \"{windowEvent.Name}\" to the DKP sheet...";
        return RedirectToAction(nameof(Index), new { linkshellId });
    }

    // Removes the Window Event row. Linked snapshots are unlinked (the FK uses
    // OnDelete SetNull) rather than destroyed so officers can re-attach or
    // ignore them from the Unlinked Snapshots list afterwards. Sheet rows that
    // were already appended remain in the spreadsheet -- AttInput append is a
    // one-way push, not a mirror.
    [HttpPost("/linkshells/{linkshellId:int}/window-events/{windowEventId:int}/delete")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Delete(int linkshellId, int windowEventId, CancellationToken cancellationToken)
    {
        if (await RequireOfficerAsync(linkshellId, cancellationToken) is { } reject) return reject;

        var windowEvent = await _db.WindowEvents
            .FirstOrDefaultAsync(e => e.Id == windowEventId && e.LinkshellId == linkshellId, cancellationToken);
        if (windowEvent is null) return NotFound();

        _db.WindowEvents.Remove(windowEvent);
        await _db.SaveChangesAsync(cancellationToken);
        return RedirectToAction(nameof(Index), new { linkshellId });
    }

    [HttpPost("/linkshells/{linkshellId:int}/window-events/snapshots/{snapshotId:int}/attach")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> AttachSnapshot(
        int linkshellId,
        int snapshotId,
        [FromForm] int? windowEventId,
        [FromForm] string? name,
        CancellationToken cancellationToken)
    {
        if (await RequireOfficerAsync(linkshellId, cancellationToken) is { } reject) return reject;

        var snapshot = await _db.AttendanceSnapshots
            .Include(s => s.Entries)
            .FirstOrDefaultAsync(s => s.Id == snapshotId && s.LinkshellId == linkshellId, cancellationToken);
        if (snapshot is null) return NotFound();

        WindowEvent? windowEvent = null;
        if (windowEventId.HasValue)
        {
            windowEvent = await _db.WindowEvents
                .FirstOrDefaultAsync(e => e.Id == windowEventId.Value && e.LinkshellId == linkshellId, cancellationToken);
        }
        else
        {
            var trimmed = TrimToNull(name, 128);
            if (trimmed is null)
            {
                TempData["WindowEventError"] = "Choose an existing Window Event or enter a name.";
                return RedirectToAction(nameof(Index), new { linkshellId });
            }
            windowEvent = await FindOrCreateOpenEventAsync(
                linkshellId,
                trimmed,
                snapshot.CapturedAtUtc,
                snapshot.CapturedByCharacterName,
                DateTime.UtcNow,
                cancellationToken);
            snapshot.Name ??= trimmed;
        }

        if (windowEvent is null) return NotFound();

        snapshot.WindowEventId = windowEvent.Id;
        snapshot.SnapshotStatus = AttendanceSnapshotStatuses.Active;
        snapshot.DuplicateOfSnapshotId = null;
        windowEvent.FirstCapturedAtUtc = Min(windowEvent.FirstCapturedAtUtc, snapshot.CapturedAtUtc);
        windowEvent.LastCapturedAtUtc = Max(windowEvent.LastCapturedAtUtc, snapshot.CapturedAtUtc);

        await _db.SaveChangesAsync(cancellationToken);
        await MarkLikelyDuplicateAsync(snapshot.Id, cancellationToken);

        // Sheet sync is officer-initiated via the Post to DKP Sheet button on
        // the Window Event card -- attaching a snapshot no longer auto-pushes
        // rows so the user has a chance to fill in DKP + Entry Type first.
        return RedirectToAction(nameof(Index), new { linkshellId });
    }

    [HttpPost("/linkshells/{linkshellId:int}/window-events/snapshots/{snapshotId:int}/status")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SetSnapshotStatus(
        int linkshellId,
        int snapshotId,
        [FromForm] string status,
        CancellationToken cancellationToken)
    {
        if (await RequireOfficerAsync(linkshellId, cancellationToken) is { } reject) return reject;

        var normalized = status switch
        {
            AttendanceSnapshotStatuses.Active => AttendanceSnapshotStatuses.Active,
            AttendanceSnapshotStatuses.Duplicate => AttendanceSnapshotStatuses.Duplicate,
            AttendanceSnapshotStatuses.Ignored => AttendanceSnapshotStatuses.Ignored,
            AttendanceSnapshotStatuses.PossibleDuplicate => AttendanceSnapshotStatuses.PossibleDuplicate,
            _ => null
        };
        if (normalized is null) return BadRequest("Unsupported snapshot status.");

        var snapshot = await _db.AttendanceSnapshots
            .FirstOrDefaultAsync(s => s.Id == snapshotId && s.LinkshellId == linkshellId, cancellationToken);
        if (snapshot is null) return NotFound();

        snapshot.SnapshotStatus = normalized;
        if (normalized == AttendanceSnapshotStatuses.Active || normalized == AttendanceSnapshotStatuses.Ignored)
        {
            snapshot.DuplicateOfSnapshotId = null;
        }
        await _db.SaveChangesAsync(cancellationToken);

        // Sheet sync is officer-initiated via Post to DKP Sheet on the parent
        // Window Event card. Flipping a snapshot's status no longer pushes
        // rows directly so the officer controls when the AttInput append fires.
        return RedirectToAction(nameof(Index), new { linkshellId });
    }

    private async Task<WindowEvent> FindOrCreateOpenEventAsync(
        int linkshellId,
        string name,
        DateTime capturedAtUtc,
        string? capturedByCharacterName,
        DateTime nowUtc,
        CancellationToken cancellationToken)
    {
        var normalized = NormalizeName(name)!;
        var staleCutoff = capturedAtUtc.AddHours(-24);
        var existing = await _db.WindowEvents
            .Where(e =>
                e.LinkshellId == linkshellId &&
                e.Status == WindowEventStatuses.Open &&
                e.NormalizedName == normalized &&
                e.LastCapturedAtUtc >= staleCutoff)
            .OrderByDescending(e => e.LastCapturedAtUtc)
            .FirstOrDefaultAsync(cancellationToken);
        if (existing is not null) return existing;

        var windowEvent = new WindowEvent
        {
            LinkshellId = linkshellId,
            Name = name,
            NormalizedName = normalized,
            Status = WindowEventStatuses.Open,
            CreatedAtUtc = nowUtc,
            FirstCapturedAtUtc = capturedAtUtc,
            LastCapturedAtUtc = capturedAtUtc,
            CreatedByCharacterName = capturedByCharacterName,
        };
        _db.WindowEvents.Add(windowEvent);
        await _db.SaveChangesAsync(cancellationToken);
        return windowEvent;
    }

    private async Task MarkLikelyDuplicateAsync(int snapshotId, CancellationToken cancellationToken)
    {
        var snapshot = await _db.AttendanceSnapshots
            .Include(s => s.Entries)
            .FirstOrDefaultAsync(s => s.Id == snapshotId, cancellationToken);
        if (snapshot is null || !snapshot.WindowEventId.HasValue || snapshot.Entries.Count == 0) return;

        var names = snapshot.Entries
            .Select(e => NormalizeName(e.CharacterName))
            .Where(n => n is not null)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (names.Count == 0) return;

        var fromUtc = snapshot.CapturedAtUtc.AddMinutes(-15);
        var toUtc = snapshot.CapturedAtUtc.AddMinutes(15);
        var candidates = await _db.AttendanceSnapshots
            .Include(s => s.Entries)
            .Where(s =>
                s.Id != snapshot.Id &&
                s.WindowEventId == snapshot.WindowEventId &&
                s.SnapshotStatus != AttendanceSnapshotStatuses.Ignored &&
                s.SnapshotStatus != AttendanceSnapshotStatuses.Duplicate &&
                s.CapturedAtUtc >= fromUtc &&
                s.CapturedAtUtc <= toUtc)
            .ToListAsync(cancellationToken);

        AttendanceSnapshot? best = null;
        var bestOverlap = 0d;
        foreach (var candidate in candidates)
        {
            var otherNames = candidate.Entries
                .Select(e => NormalizeName(e.CharacterName))
                .Where(n => n is not null)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var denominator = Math.Min(names.Count, otherNames.Count);
            if (denominator == 0) continue;
            var overlap = names.Count(n => otherNames.Contains(n!)) / (double)denominator;
            if (overlap > bestOverlap)
            {
                bestOverlap = overlap;
                best = candidate;
            }
        }

        if (best is not null && bestOverlap >= 0.75)
        {
            snapshot.SnapshotStatus = AttendanceSnapshotStatuses.PossibleDuplicate;
            snapshot.DuplicateOfSnapshotId = best.Id;
            await _db.SaveChangesAsync(cancellationToken);
        }
    }

    private WindowEventRow MapWindowEvent(WindowEvent item, DateTimeZone userZone)
    {
        var snapshots = item.Snapshots
            .OrderByDescending(s => s.CapturedAtUtc)
            .Select(s => MapSnapshot(s, userZone))
            .ToList();
        var combined = BuildCombinedMembers(item.Snapshots);

        return new WindowEventRow
        {
            Id = item.Id,
            Name = item.Name,
            Status = item.Status,
            FirstCapturedAtUtc = item.FirstCapturedAtUtc,
            LastCapturedAtUtc = item.LastCapturedAtUtc,
            FirstCapturedDisplay = FormatPretty(item.FirstCapturedAtUtc, userZone),
            LastCapturedDisplay = FormatPretty(item.LastCapturedAtUtc, userZone),
            CreatedByCharacterName = item.CreatedByCharacterName,
            SnapshotCount = snapshots.Count,
            ActiveSnapshotCount = snapshots.Count(s => s.SnapshotStatus == AttendanceSnapshotStatuses.Active),
            DuplicateSnapshotCount = snapshots.Count(s =>
                s.SnapshotStatus == AttendanceSnapshotStatuses.PossibleDuplicate ||
                s.SnapshotStatus == AttendanceSnapshotStatuses.Duplicate),
            IgnoredSnapshotCount = snapshots.Count(s => s.SnapshotStatus == AttendanceSnapshotStatuses.Ignored),
            CombinedMemberCount = combined.Count,
            DkpAmount = item.DkpAmount,
            EntryType = item.EntryType,
            PostedToSheetAt = item.PostedToSheetAt,
            PostedToSheetDisplay = item.PostedToSheetAt.HasValue
                ? FormatPretty(item.PostedToSheetAt.Value, userZone)
                : null,
            Snapshots = snapshots,
            CombinedMembers = combined,
        };
    }

    private WindowSnapshotRow MapSnapshot(AttendanceSnapshot snapshot, DateTimeZone userZone)
    {
        var entries = snapshot.Entries
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

        return new WindowSnapshotRow
        {
            Id = snapshot.Id,
            WindowEventId = snapshot.WindowEventId,
            Name = snapshot.Name,
            SnapshotStatus = snapshot.SnapshotStatus,
            DuplicateOfSnapshotId = snapshot.DuplicateOfSnapshotId,
            CapturedAtUtc = snapshot.CapturedAtUtc,
            CapturedAtDisplay = FormatPretty(snapshot.CapturedAtUtc, userZone),
            CapturedByCharacterName = snapshot.CapturedByCharacterName,
            EntryCount = snapshot.EntryCount,
            PrimaryZone = entries
                .Where(e => !string.IsNullOrWhiteSpace(e.Zone))
                .GroupBy(e => e.Zone!, StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(g => g.Count())
                .ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.Key)
                .FirstOrDefault(),
            Entries = entries,
        };
    }

    private static List<WindowCombinedMemberRow> BuildCombinedMembers(IEnumerable<AttendanceSnapshot> snapshots)
    {
        return snapshots
            .Where(s => s.SnapshotStatus == AttendanceSnapshotStatuses.Active)
            .SelectMany(s => s.Entries.Select(e => new { Snapshot = s, Entry = e }))
            .GroupBy(x => x.Entry.CharacterName.Trim(), StringComparer.OrdinalIgnoreCase)
            .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
            .Select(g =>
            {
                var latest = g.OrderByDescending(x => x.Snapshot.CapturedAtUtc).First().Entry;
                return new WindowCombinedMemberRow
                {
                    CharacterName = g.Key,
                    MainJob = latest.MainJob,
                    MainJobLevel = latest.MainJobLevel,
                    SubJob = latest.SubJob,
                    SubJobLevel = latest.SubJobLevel,
                    Zone = latest.Zone,
                    SnapshotCount = g.Select(x => x.Snapshot.Id).Distinct().Count(),
                };
            })
            .ToList();
    }

    private async Task<IActionResult?> RequireOfficerAsync(int linkshellId, CancellationToken cancellationToken)
    {
        var user = await _userManager.GetUserAsync(User);
        if (user is null) return Challenge();
        var membership = await _db.AppUserLinkshells
            .AsNoTracking()
            .FirstOrDefaultAsync(link => link.AppUserId == user.Id && link.LinkshellId == linkshellId, cancellationToken);
        if (membership is null) return Forbid();
        return IsLeaderOrOfficer(membership) ? null : Forbid();
    }

    private static bool IsLeaderOrOfficer(AppUserLinkshell membership)
        => membership.Rank?.Equals("Leader", StringComparison.OrdinalIgnoreCase) == true
           || membership.Rank?.Equals("Officer", StringComparison.OrdinalIgnoreCase) == true;

    private static string? TrimToNull(string? value, int maxLength)
    {
        var trimmed = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        if (trimmed is { Length: > 0 } && trimmed.Length > maxLength) trimmed = trimmed[..maxLength];
        return trimmed;
    }

    private static string? NormalizeName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var parts = value.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return string.Join(' ', parts).ToUpperInvariant();
    }

    private static DateTime Min(DateTime a, DateTime b) => a <= b ? a : b;
    private static DateTime Max(DateTime a, DateTime b) => a >= b ? a : b;

    private static string FormatPretty(DateTime utc, DateTimeZone zone)
    {
        var instant = Instant.FromDateTimeUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc));
        var local = instant.InZone(zone);
        var localDateTime = local.ToDateTimeUnspecified();
        var zoneName = zone.GetZoneInterval(instant).Name;
        var day = localDateTime.Day;
        var suffix = (day % 100) is >= 11 and <= 13
            ? "th"
            : (day % 10) switch { 1 => "st", 2 => "nd", 3 => "rd", _ => "th" };
        var month = localDateTime.ToString("MMMM", CultureInfo.InvariantCulture);
        var time = localDateTime.ToString("h:mm", CultureInfo.InvariantCulture);
        var meridian = localDateTime.ToString("tt", CultureInfo.InvariantCulture).ToLowerInvariant();
        return $"{month} {day}{suffix} {localDateTime.Year} {time}{meridian} {zoneName}";
    }
}
