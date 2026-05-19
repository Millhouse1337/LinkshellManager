using LinkshellManagerDiscordApp.Authorization;
using LinkshellManagerDiscordApp.Models;
using LinkshellManagerDiscordApp.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace LinkshellManagerDiscordApp.Controllers;

public sealed partial class AddonApiController
{
    public sealed record AddonAttendanceSnapshotEntryDto(
        string? CharacterName,
        string? MainJob,
        int? MainJobLevel,
        string? SubJob,
        int? SubJobLevel,
        string? Zone);

    public sealed record AddonAttendanceSnapshotRequest(
        DateTime? CapturedAtUtc,
        string? CapturedByCharacterName,
        string? UtcOffset,
        IReadOnlyList<AddonAttendanceSnapshotEntryDto>? Entries,
        string? Name);

    [HttpPost("attendance-snapshots")]
    [AddonApiAuth]
    public async Task<IActionResult> PostAttendanceSnapshotAsync(
        [FromBody] AddonAttendanceSnapshotRequest request,
        CancellationToken cancellationToken)
    {
        if (request is null)
        {
            return BadRequest(new { error = "Request body is required." });
        }

        var entries = (request.Entries ?? Array.Empty<AddonAttendanceSnapshotEntryDto>())
            .Where(e => !string.IsNullOrWhiteSpace(e.CharacterName))
            .ToList();

        if (entries.Count == 0)
        {
            return BadRequest(new { error = "Snapshot must contain at least one entry." });
        }
        // FFXI alliance caps at 18 members (3 parties of 6). Reject anything
        // larger as malformed; the addon should never produce >18 rows.
        if (entries.Count > 18)
        {
            return BadRequest(new { error = "Snapshot exceeds the 18-member alliance maximum." });
        }

        var token = AddonApiAuthAttribute.GetToken(HttpContext);
        var nowUtc = DateTime.UtcNow;
        var capturedAt = request.CapturedAtUtc.HasValue
                         && request.CapturedAtUtc.Value > nowUtc.AddDays(-7)
                         && request.CapturedAtUtc.Value < nowUtc.AddMinutes(5)
            ? request.CapturedAtUtc.Value
            : nowUtc;

        // Snapshot posts have no immediate DKP effect. Members with submit
        // permission can create them directly; leaders/officers review,
        // merge, rename, or ignore them on the Window Events page.
        var role = await GetTokenIssuerRoleAsync(token, token.LinkshellId, cancellationToken);
        var canModerate = role?.CanModerateLiveEvent == true;
        var canSubmit = role?.CanSubmitAttendanceForApproval == true;
        if (!canModerate && !canSubmit)
        {
            return Forbid();
        }

        var trimmedName = TruncateString(string.IsNullOrWhiteSpace(request.Name) ? null : request.Name.Trim(), 128);
        var windowEvent = await FindOrCreateWindowEventAsync(
            token.LinkshellId,
            trimmedName,
            capturedAt,
            TruncateString(request.CapturedByCharacterName, 256),
            nowUtc,
            cancellationToken);

        var snapshot = new AttendanceSnapshot
        {
            LinkshellId = token.LinkshellId,
            CapturedAtUtc = capturedAt,
            CapturedByCharacterName = TruncateString(request.CapturedByCharacterName, 256),
            UtcOffset = TruncateString(request.UtcOffset, 8),
            EntryCount = entries.Count,
            CreatedAtUtc = nowUtc,
            Name = trimmedName,
            WindowEventId = windowEvent?.Id,
            SnapshotStatus = AttendanceSnapshotStatuses.Active,
        };

        foreach (var e in entries)
        {
            snapshot.Entries.Add(new AttendanceSnapshotEntry
            {
                CharacterName = TruncateString(e.CharacterName, 256) ?? string.Empty,
                MainJob = TruncateString(e.MainJob, 8),
                MainJobLevel = e.MainJobLevel,
                SubJob = TruncateString(e.SubJob, 8),
                SubJobLevel = e.SubJobLevel,
                Zone = TruncateString(e.Zone, 128),
            });
        }

        _dbContext.AttendanceSnapshots.Add(snapshot);
        await _dbContext.SaveChangesAsync(cancellationToken);

        if (windowEvent is not null)
        {
            var possibleDuplicate = await FindLikelyDuplicateSnapshotAsync(snapshot, cancellationToken);
            if (possibleDuplicate is not null)
            {
                snapshot.SnapshotStatus = AttendanceSnapshotStatuses.PossibleDuplicate;
                snapshot.DuplicateOfSnapshotId = possibleDuplicate.Id;
            }
            windowEvent.FirstCapturedAtUtc = windowEvent.FirstCapturedAtUtc <= snapshot.CapturedAtUtc
                ? windowEvent.FirstCapturedAtUtc
                : snapshot.CapturedAtUtc;
            windowEvent.LastCapturedAtUtc = windowEvent.LastCapturedAtUtc >= snapshot.CapturedAtUtc
                ? windowEvent.LastCapturedAtUtc
                : snapshot.CapturedAtUtc;
            await _dbContext.SaveChangesAsync(cancellationToken);
        }

        // Fire-and-forget post to the linkshell's Discord channel (no-op if
        // no webhook URL is configured). Enqueued after the snapshot is
        // committed so the background worker can reload it; never blocks or
        // fails this addon request if Discord is slow/unreachable.
        await _discordWebhook.EnqueueSnapshotAsync(snapshot.Id, cancellationToken);

        // Sheet sync is officer-initiated on the Window Events page (Post
        // to DKP Sheet button) so the officer can review the combined roster
        // and set DKP + Entry Type before any rows land in the spreadsheet.
        return Ok(new
        {
            snapshotId = snapshot.Id,
            entryCount = snapshot.EntryCount,
            capturedAtUtc = snapshot.CapturedAtUtc,
            linkedEventId = (int?)null,
            windowEventId = snapshot.WindowEventId,
            snapshotStatus = snapshot.SnapshotStatus,
        });
    }

    // Closes a Window Event from the addon's HNM session "End Event" button.
    // Mirrors the cookie-auth WindowEventsController.Close: it only flips the
    // status + stamps ClosedAtUtc. It deliberately does NOT enqueue any sheet
    // sync — posting a Window Event to the DKP sheet stays an explicit,
    // officer-initiated action on the Window Events page.
    [HttpPost("window-events/{windowEventId:int}/close")]
    [AddonApiAuth]
    public async Task<IActionResult> CloseWindowEventAsync(
        int windowEventId, CancellationToken cancellationToken)
    {
        var token = AddonApiAuthAttribute.GetToken(HttpContext);

        var role = await GetTokenIssuerRoleAsync(token, token.LinkshellId, cancellationToken);
        if (role?.CanModerateLiveEvent != true)
        {
            return Forbid();
        }

        var windowEvent = await _dbContext.WindowEvents
            .FirstOrDefaultAsync(
                e => e.Id == windowEventId && e.LinkshellId == token.LinkshellId,
                cancellationToken);
        if (windowEvent is null)
        {
            return NotFound(new { error = "Window Event not found." });
        }

        if (windowEvent.Status != WindowEventStatuses.Closed)
        {
            windowEvent.Status = WindowEventStatuses.Closed;
            windowEvent.ClosedAtUtc = DateTime.UtcNow;
            await _dbContext.SaveChangesAsync(cancellationToken);
        }

        return Ok(new { closed = true, windowEventId = windowEvent.Id });
    }

    private async Task<WindowEvent?> FindOrCreateWindowEventAsync(
        int linkshellId,
        string? name,
        DateTime capturedAtUtc,
        string? capturedByCharacterName,
        DateTime nowUtc,
        CancellationToken cancellationToken)
    {
        var normalized = NormalizeWindowEventName(name);
        if (normalized is null)
        {
            return null;
        }

        var staleCutoff = capturedAtUtc.AddHours(-21);
        var existing = await _dbContext.WindowEvents
            .Where(item =>
                item.LinkshellId == linkshellId &&
                item.Status == WindowEventStatuses.Open &&
                item.NormalizedName == normalized &&
                item.LastCapturedAtUtc >= staleCutoff)
            .OrderByDescending(item => item.LastCapturedAtUtc)
            .FirstOrDefaultAsync(cancellationToken);

        if (existing is not null)
        {
            return existing;
        }

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
            // Pre-select the camp from the monster name so officers don't
            // have to set it manually on every "/lsm now <monster>" event.
            EntryType = WindowEventEntryTypes.FromMonsterName(name),
        };
        _dbContext.WindowEvents.Add(windowEvent);
        await _dbContext.SaveChangesAsync(cancellationToken);
        return windowEvent;
    }

    private async Task<AttendanceSnapshot?> FindLikelyDuplicateSnapshotAsync(
        AttendanceSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        if (!snapshot.WindowEventId.HasValue || snapshot.Entries.Count == 0)
        {
            return null;
        }

        var names = snapshot.Entries
            .Select(e => NormalizeWindowEventName(e.CharacterName))
            .Where(n => n is not null)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (names.Count == 0)
        {
            return null;
        }

        var fromUtc = snapshot.CapturedAtUtc.AddMinutes(-8);
        var toUtc = snapshot.CapturedAtUtc.AddMinutes(8);
        var candidates = await _dbContext.AttendanceSnapshots
            .Include(item => item.Entries)
            .Where(item =>
                item.Id != snapshot.Id &&
                item.WindowEventId == snapshot.WindowEventId &&
                item.SnapshotStatus != AttendanceSnapshotStatuses.Ignored &&
                item.SnapshotStatus != AttendanceSnapshotStatuses.Duplicate &&
                item.CapturedAtUtc >= fromUtc &&
                item.CapturedAtUtc <= toUtc)
            .ToListAsync(cancellationToken);

        AttendanceSnapshot? best = null;
        var bestOverlap = 0d;
        foreach (var candidate in candidates)
        {
            var candidateNames = candidate.Entries
                .Select(e => NormalizeWindowEventName(e.CharacterName))
                .Where(n => n is not null)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (candidateNames.Count == 0) continue;

            var overlap = names.Count(n => candidateNames.Contains(n!));
            var denominator = Math.Min(names.Count, candidateNames.Count);
            var ratio = denominator == 0 ? 0 : overlap / (double)denominator;
            if (ratio > bestOverlap)
            {
                bestOverlap = ratio;
                best = candidate;
            }
        }

        return bestOverlap >= 0.75 ? best : null;
    }

    private static string? NormalizeWindowEventName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var parts = value.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return string.Join(' ', parts).ToUpperInvariant();
    }

    private static string? TruncateString(string? value, int max)
    {
        if (string.IsNullOrEmpty(value)) return value;
        return value.Length <= max ? value : value.Substring(0, max);
    }
}
