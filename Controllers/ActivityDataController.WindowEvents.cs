using LinkshellManagerDiscordApp.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace LinkshellManagerDiscordApp.Controllers;

public sealed partial class ActivityDataController
{
    public sealed record ActivityWindowEventsResponse(
        IReadOnlyList<ActivityWindowEventDto> OpenEvents,
        IReadOnlyList<ActivityWindowEventDto> ClosedEvents,
        IReadOnlyList<ActivityWindowSnapshotDto> UnlinkedSnapshots,
        bool CanManage);

    public sealed record ActivityWindowEventDto(
        int Id,
        int LinkshellId,
        string? Name,
        string Status,
        DateTime FirstCapturedAtUtc,
        DateTime LastCapturedAtUtc,
        string? CreatedByCharacterName,
        int SnapshotCount,
        int ActiveSnapshotCount,
        int DuplicateSnapshotCount,
        int IgnoredSnapshotCount,
        int CombinedMemberCount,
        IReadOnlyList<ActivityWindowSnapshotDto> Snapshots,
        IReadOnlyList<ActivityWindowCombinedMemberDto> CombinedMembers);

    public sealed record ActivityWindowSnapshotDto(
        int Id,
        int? WindowEventId,
        string? Name,
        string SnapshotStatus,
        int? DuplicateOfSnapshotId,
        DateTime CapturedAtUtc,
        string? CapturedByCharacterName,
        string? PrimaryZone,
        int EntryCount,
        IReadOnlyList<ActivityWindowSnapshotEntryDto> Entries);

    public sealed record ActivityWindowSnapshotEntryDto(
        string CharacterName,
        string? MainJob,
        int? MainJobLevel,
        string? SubJob,
        int? SubJobLevel,
        string? Zone);

    public sealed record ActivityWindowCombinedMemberDto(
        string CharacterName,
        string? MainJob,
        int? MainJobLevel,
        string? SubJob,
        int? SubJobLevel,
        string? Zone,
        int SnapshotCount);

    public sealed record ActivityAttachWindowSnapshotRequest(int? WindowEventId, string? Name);
    public sealed record ActivityWindowEventRenameRequest(string? Name);
    public sealed record ActivityWindowSnapshotStatusRequest(string Status);

    [HttpGet("window-events")]
    public async Task<IActionResult> GetWindowEventsAsync([FromQuery] int linkshellId, CancellationToken cancellationToken)
    {
        var appUser = await ResolveAppUserAsync(cancellationToken);
        if (appUser is null) return Unauthorized(new { error = "Sign in to load Window Events." });
        if (linkshellId <= 0) return BadRequest(new { error = "A linkshell selection is required." });

        var membership = await GetMembershipAsync(appUser.Id, linkshellId, cancellationToken);
        if (membership is null) return Forbid();
        var canManage = await CanAsync(membership, r => r.CanModerateLiveEvent || r.CanManageEvents, cancellationToken);

        var openEvents = await _dbContext.WindowEvents
            .AsNoTracking()
            .Where(e => e.LinkshellId == linkshellId && e.Status == WindowEventStatuses.Open)
            .OrderByDescending(e => e.LastCapturedAtUtc)
            .Include(e => e.Snapshots).ThenInclude(s => s.Entries)
            .ToListAsync(cancellationToken);

        var closedEvents = await _dbContext.WindowEvents
            .AsNoTracking()
            .Where(e => e.LinkshellId == linkshellId && e.Status == WindowEventStatuses.Closed)
            .OrderByDescending(e => e.LastCapturedAtUtc)
            .Take(25)
            .Include(e => e.Snapshots).ThenInclude(s => s.Entries)
            .ToListAsync(cancellationToken);

        var unlinkedSnapshots = await _dbContext.AttendanceSnapshots
            .AsNoTracking()
            .Where(s => s.LinkshellId == linkshellId && s.WindowEventId == null && s.SnapshotStatus != AttendanceSnapshotStatuses.Ignored)
            .OrderByDescending(s => s.CapturedAtUtc)
            .Take(100)
            .Include(s => s.Entries)
            .ToListAsync(cancellationToken);

        return Ok(new ActivityWindowEventsResponse(
            openEvents.Select(MapActivityWindowEvent).ToList(),
            closedEvents.Select(MapActivityWindowEvent).ToList(),
            unlinkedSnapshots.Select(MapActivityWindowSnapshot).ToList(),
            canManage));
    }

    [HttpPost("window-events/{windowEventId:int}/rename")]
    public async Task<IActionResult> RenameWindowEventAsync(
        int windowEventId,
        [FromBody] ActivityWindowEventRenameRequest request,
        CancellationToken cancellationToken)
    {
        var windowEvent = await LoadManageableWindowEventAsync(windowEventId, cancellationToken);
        if (windowEvent.Result is not null) return windowEvent.Result;

        var name = TrimToNull(request.Name, 128);
        if (name is null) return BadRequest(new { error = "Window Event name is required." });

        windowEvent.Value!.Name = name;
        windowEvent.Value.NormalizedName = NormalizeWindowName(name);
        await _dbContext.SaveChangesAsync(cancellationToken);
        return Ok(new { success = true });
    }

    [HttpPost("window-events/{windowEventId:int}/close")]
    public async Task<IActionResult> CloseWindowEventAsync(int windowEventId, CancellationToken cancellationToken)
    {
        var windowEvent = await LoadManageableWindowEventAsync(windowEventId, cancellationToken);
        if (windowEvent.Result is not null) return windowEvent.Result;

        windowEvent.Value!.Status = WindowEventStatuses.Closed;
        windowEvent.Value.ClosedAtUtc = DateTime.UtcNow;
        await _dbContext.SaveChangesAsync(cancellationToken);
        return Ok(new { success = true });
    }

    [HttpPost("window-events/{windowEventId:int}/reopen")]
    public async Task<IActionResult> ReopenWindowEventAsync(int windowEventId, CancellationToken cancellationToken)
    {
        var windowEvent = await LoadManageableWindowEventAsync(windowEventId, cancellationToken);
        if (windowEvent.Result is not null) return windowEvent.Result;

        windowEvent.Value!.Status = WindowEventStatuses.Open;
        windowEvent.Value.ClosedAtUtc = null;
        await _dbContext.SaveChangesAsync(cancellationToken);
        return Ok(new { success = true });
    }

    [HttpPost("window-events/snapshots/{snapshotId:int}/attach")]
    public async Task<IActionResult> AttachWindowSnapshotAsync(
        int snapshotId,
        [FromBody] ActivityAttachWindowSnapshotRequest request,
        CancellationToken cancellationToken)
    {
        var snapshot = await _dbContext.AttendanceSnapshots
            .Include(s => s.Entries)
            .FirstOrDefaultAsync(s => s.Id == snapshotId, cancellationToken);
        if (snapshot is null) return NotFound(new { error = "Snapshot not found." });

        var manageResult = await RequireWindowEventManagerAsync(snapshot.LinkshellId, cancellationToken);
        if (manageResult is not null) return manageResult;

        WindowEvent? windowEvent = null;
        if (request.WindowEventId.HasValue)
        {
            windowEvent = await _dbContext.WindowEvents
                .FirstOrDefaultAsync(e => e.Id == request.WindowEventId.Value && e.LinkshellId == snapshot.LinkshellId, cancellationToken);
        }
        else
        {
            var name = TrimToNull(request.Name, 128);
            if (name is null) return BadRequest(new { error = "Choose an existing Window Event or enter a name." });
            windowEvent = await FindOrCreateActivityWindowEventAsync(
                snapshot.LinkshellId,
                name,
                snapshot.CapturedAtUtc,
                snapshot.CapturedByCharacterName,
                DateTime.UtcNow,
                cancellationToken);
            snapshot.Name ??= name;
        }

        if (windowEvent is null) return NotFound(new { error = "Window Event not found." });

        snapshot.WindowEventId = windowEvent.Id;
        snapshot.SnapshotStatus = AttendanceSnapshotStatuses.Active;
        snapshot.DuplicateOfSnapshotId = null;
        windowEvent.FirstCapturedAtUtc = windowEvent.FirstCapturedAtUtc <= snapshot.CapturedAtUtc
            ? windowEvent.FirstCapturedAtUtc
            : snapshot.CapturedAtUtc;
        windowEvent.LastCapturedAtUtc = windowEvent.LastCapturedAtUtc >= snapshot.CapturedAtUtc
            ? windowEvent.LastCapturedAtUtc
            : snapshot.CapturedAtUtc;
        await _dbContext.SaveChangesAsync(cancellationToken);
        await MarkActivitySnapshotDuplicateAsync(snapshot.Id, cancellationToken);
        return Ok(new { success = true });
    }

    [HttpPost("window-events/snapshots/{snapshotId:int}/status")]
    public async Task<IActionResult> SetWindowSnapshotStatusAsync(
        int snapshotId,
        [FromBody] ActivityWindowSnapshotStatusRequest request,
        CancellationToken cancellationToken)
    {
        var snapshot = await _dbContext.AttendanceSnapshots
            .FirstOrDefaultAsync(s => s.Id == snapshotId, cancellationToken);
        if (snapshot is null) return NotFound(new { error = "Snapshot not found." });

        var manageResult = await RequireWindowEventManagerAsync(snapshot.LinkshellId, cancellationToken);
        if (manageResult is not null) return manageResult;

        var status = request.Status switch
        {
            AttendanceSnapshotStatuses.Active => AttendanceSnapshotStatuses.Active,
            AttendanceSnapshotStatuses.PossibleDuplicate => AttendanceSnapshotStatuses.PossibleDuplicate,
            AttendanceSnapshotStatuses.Duplicate => AttendanceSnapshotStatuses.Duplicate,
            AttendanceSnapshotStatuses.Ignored => AttendanceSnapshotStatuses.Ignored,
            _ => null
        };
        if (status is null) return BadRequest(new { error = "Unsupported snapshot status." });

        snapshot.SnapshotStatus = status;
        if (status is AttendanceSnapshotStatuses.Active or AttendanceSnapshotStatuses.Ignored)
        {
            snapshot.DuplicateOfSnapshotId = null;
        }
        await _dbContext.SaveChangesAsync(cancellationToken);
        return Ok(new { success = true });
    }

    private async Task<(WindowEvent? Value, IActionResult? Result)> LoadManageableWindowEventAsync(
        int windowEventId,
        CancellationToken cancellationToken)
    {
        var windowEvent = await _dbContext.WindowEvents
            .FirstOrDefaultAsync(e => e.Id == windowEventId, cancellationToken);
        if (windowEvent is null) return (null, NotFound(new { error = "Window Event not found." }));

        var manageResult = await RequireWindowEventManagerAsync(windowEvent.LinkshellId, cancellationToken);
        return manageResult is null ? (windowEvent, null) : (null, manageResult);
    }

    private async Task<IActionResult?> RequireWindowEventManagerAsync(int linkshellId, CancellationToken cancellationToken)
    {
        var appUser = await ResolveAppUserAsync(cancellationToken);
        if (appUser is null) return Unauthorized(new { error = "Sign in to manage Window Events." });

        var membership = await GetMembershipAsync(appUser.Id, linkshellId, cancellationToken);
        if (!await CanAsync(membership, r => r.CanModerateLiveEvent || r.CanManageEvents, cancellationToken))
        {
            return Forbid();
        }
        return null;
    }

    private async Task<WindowEvent> FindOrCreateActivityWindowEventAsync(
        int linkshellId,
        string name,
        DateTime capturedAtUtc,
        string? capturedByCharacterName,
        DateTime nowUtc,
        CancellationToken cancellationToken)
    {
        var normalized = NormalizeWindowName(name)!;
        var staleCutoff = capturedAtUtc.AddHours(-24);
        var existing = await _dbContext.WindowEvents
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
        _dbContext.WindowEvents.Add(windowEvent);
        await _dbContext.SaveChangesAsync(cancellationToken);
        return windowEvent;
    }

    private async Task MarkActivitySnapshotDuplicateAsync(int snapshotId, CancellationToken cancellationToken)
    {
        var snapshot = await _dbContext.AttendanceSnapshots
            .Include(s => s.Entries)
            .FirstOrDefaultAsync(s => s.Id == snapshotId, cancellationToken);
        if (snapshot is null || !snapshot.WindowEventId.HasValue || snapshot.Entries.Count == 0) return;

        var names = snapshot.Entries
            .Select(e => NormalizeWindowName(e.CharacterName))
            .Where(n => n is not null)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var fromUtc = snapshot.CapturedAtUtc.AddMinutes(-15);
        var toUtc = snapshot.CapturedAtUtc.AddMinutes(15);
        var candidates = await _dbContext.AttendanceSnapshots
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
                .Select(e => NormalizeWindowName(e.CharacterName))
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
            await _dbContext.SaveChangesAsync(cancellationToken);
        }
    }

    private static ActivityWindowEventDto MapActivityWindowEvent(WindowEvent item)
    {
        var snapshots = item.Snapshots
            .OrderByDescending(s => s.CapturedAtUtc)
            .Select(MapActivityWindowSnapshot)
            .ToList();
        var combined = BuildActivityCombinedMembers(item.Snapshots);
        return new ActivityWindowEventDto(
            item.Id,
            item.LinkshellId,
            item.Name,
            item.Status,
            item.FirstCapturedAtUtc,
            item.LastCapturedAtUtc,
            item.CreatedByCharacterName,
            snapshots.Count,
            snapshots.Count(s => s.SnapshotStatus == AttendanceSnapshotStatuses.Active),
            snapshots.Count(s => s.SnapshotStatus == AttendanceSnapshotStatuses.PossibleDuplicate || s.SnapshotStatus == AttendanceSnapshotStatuses.Duplicate),
            snapshots.Count(s => s.SnapshotStatus == AttendanceSnapshotStatuses.Ignored),
            combined.Count,
            snapshots,
            combined);
    }

    private static ActivityWindowSnapshotDto MapActivityWindowSnapshot(AttendanceSnapshot snapshot)
    {
        var entries = snapshot.Entries
            .OrderBy(e => e.CharacterName, StringComparer.OrdinalIgnoreCase)
            .Select(e => new ActivityWindowSnapshotEntryDto(
                e.CharacterName,
                e.MainJob,
                e.MainJobLevel,
                e.SubJob,
                e.SubJobLevel,
                e.Zone))
            .ToList();

        var primaryZone = entries
            .Where(e => !string.IsNullOrWhiteSpace(e.Zone))
            .GroupBy(e => e.Zone!, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(g => g.Count())
            .ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.Key)
            .FirstOrDefault();

        return new ActivityWindowSnapshotDto(
            snapshot.Id,
            snapshot.WindowEventId,
            snapshot.Name,
            snapshot.SnapshotStatus,
            snapshot.DuplicateOfSnapshotId,
            snapshot.CapturedAtUtc,
            snapshot.CapturedByCharacterName,
            primaryZone,
            snapshot.EntryCount,
            entries);
    }

    private static List<ActivityWindowCombinedMemberDto> BuildActivityCombinedMembers(IEnumerable<AttendanceSnapshot> snapshots)
    {
        return snapshots
            .Where(s => s.SnapshotStatus == AttendanceSnapshotStatuses.Active)
            .SelectMany(s => s.Entries.Select(e => new { Snapshot = s, Entry = e }))
            .GroupBy(x => x.Entry.CharacterName.Trim(), StringComparer.OrdinalIgnoreCase)
            .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
            .Select(g =>
            {
                var latest = g.OrderByDescending(x => x.Snapshot.CapturedAtUtc).First().Entry;
                return new ActivityWindowCombinedMemberDto(
                    g.Key,
                    latest.MainJob,
                    latest.MainJobLevel,
                    latest.SubJob,
                    latest.SubJobLevel,
                    latest.Zone,
                    g.Select(x => x.Snapshot.Id).Distinct().Count());
            })
            .ToList();
    }

    private static string? TrimToNull(string? value, int maxLength)
    {
        var trimmed = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        if (trimmed is { Length: > 0 } && trimmed.Length > maxLength) trimmed = trimmed[..maxLength];
        return trimmed;
    }

    private static string? NormalizeWindowName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var parts = value.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return string.Join(' ', parts).ToUpperInvariant();
    }
}
