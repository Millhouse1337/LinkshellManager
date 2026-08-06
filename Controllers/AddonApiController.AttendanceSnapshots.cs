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
        string? Name,
        // The window the officer explicitly chose (Open = 1, Close = 2 on a 2-post camp).
        // Null keeps the derived value, which is what /lsm now and older addons send.
        //
        // The derivation reads a time grid anchored at the camp's FIRST post, so an officer
        // who only reaches camp for the kill has their Close stamped as window 1 — there is
        // no elapsed time for the grid to measure. An explicit choice is the only way to
        // record a Close that had no Open.
        int? WindowNumber = null);

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
        // Party/alliance snapshots cap at 18 (3 parties of 6), but zone- and
        // linkshell-scope captures read the FFXI entity list directly and can
        // legitimately include every linkshell member the client can see in
        // the zone -- well past 18. Keep an upper bound as a sanity guard
        // against a malformed/runaway scan, but high enough to fit a full
        // linkshell turnout in one zone.
        const int MaxSnapshotEntries = 64;
        if (entries.Count > MaxSnapshotEntries)
        {
            return BadRequest(new { error = $"Snapshot exceeds the {MaxSnapshotEntries}-entry maximum." });
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

        // Which spawn window this capture lands in, off the event's fixed grid. Null for a camp with
        // no cadence (Sky gods, farm NMs, ad-hoc posts) — those have no windows to name.
        var snapshotWindow = windowEvent is null
            ? null
            : HnmConfig.SnapshotWindowNumber(windowEvent.Name, windowEvent.WindowGridAnchorUtc, capturedAt);

        // An explicitly chosen window WINS over the grid. The grid measures elapsed time from the
        // camp's first post, which is exactly the wrong answer for the officer who arrived only in
        // time for the kill: no time has elapsed for them, so their Close derives as window 1.
        // Clamped to the camp's real window count so a bad client can't invent one.
        if (windowEvent is not null && request.WindowNumber is int chosenWindow)
        {
            var windowCap = Math.Max(1, HnmConfig.EffectiveWindowCount(windowEvent.Name));
            snapshotWindow = Math.Clamp(chosenWindow, 1, windowCap);
        }

        // Several officers scanning the same camp within a couple of minutes are capturing ONE
        // roster, not competing ones. Fold this post into that snapshot instead of adding another
        // row — see HnmConfig.SnapshotMergeWindow for why this replaced duplicate flagging.
        var mergeTarget = windowEvent is null
            ? null
            : await FindMergeTargetSnapshotAsync(windowEvent, capturedAt, snapshotWindow, cancellationToken);

        var snapshot = mergeTarget ?? new AttendanceSnapshot
        {
            LinkshellId = token.LinkshellId,
            CapturedAtUtc = capturedAt,
            CapturedByCharacterName = TruncateString(request.CapturedByCharacterName, 256),
            UtcOffset = TruncateString(request.UtcOffset, 8),
            CreatedAtUtc = nowUtc,
            Name = trimmedName,
            WindowEventId = windowEvent?.Id,
            WindowNumber = snapshotWindow,
            SnapshotStatus = AttendanceSnapshotStatuses.Active,
        };

        // On a fold the target's own CapturedAtUtc is deliberately NOT moved forward. It anchors the
        // merge window, so a steady drip of posts every 2 minutes eventually starts a fresh snapshot
        // instead of chaining into one that grows all camp long.
        var newerThanTarget = mergeTarget is null || capturedAt >= mergeTarget.CapturedAtUtc;
        var existingByName = snapshot.Entries
            .GroupBy(e => NormalizeWindowEventName(e.CharacterName) ?? string.Empty, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        foreach (var e in entries)
        {
            var key = NormalizeWindowEventName(e.CharacterName) ?? string.Empty;
            if (existingByName.TryGetValue(key, out var already))
            {
                // Same character seen again. Keep the LATEST reading of their job/zone, matching how
                // the combined roster picks a member's row, and leave it alone for an older post.
                if (newerThanTarget)
                {
                    already.MainJob = TruncateString(e.MainJob, 8);
                    already.MainJobLevel = e.MainJobLevel;
                    already.SubJob = TruncateString(e.SubJob, 8);
                    already.SubJobLevel = e.SubJobLevel;
                    already.Zone = TruncateString(e.Zone, 128);
                }
                continue;
            }

            var added = new AttendanceSnapshotEntry
            {
                CharacterName = TruncateString(e.CharacterName, 256) ?? string.Empty,
                MainJob = TruncateString(e.MainJob, 8),
                MainJobLevel = e.MainJobLevel,
                SubJob = TruncateString(e.SubJob, 8),
                SubJobLevel = e.SubJobLevel,
                Zone = TruncateString(e.Zone, 128),
            };
            snapshot.Entries.Add(added);
            existingByName[key] = added;
        }

        snapshot.EntryCount = snapshot.Entries.Count;
        if (mergeTarget is null)
        {
            _dbContext.AttendanceSnapshots.Add(snapshot);
        }
        await _dbContext.SaveChangesAsync(cancellationToken);

        if (windowEvent is not null)
        {
            // Snapshots are never auto-flagged any more: everything posted stays Active and feeds
            // the combined roster, which already dedupes by character and pays per Window Event
            // rather than per snapshot. Officers can still mark a row Duplicate/Ignored by hand.
            windowEvent.FirstCapturedAtUtc = windowEvent.FirstCapturedAtUtc <= capturedAt
                ? windowEvent.FirstCapturedAtUtc
                : capturedAt;
            windowEvent.LastCapturedAtUtc = windowEvent.LastCapturedAtUtc >= capturedAt
                ? windowEvent.LastCapturedAtUtc
                : capturedAt;
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
            // Echoed so the addon files its local tab under the window the server actually
            // stored, rather than under the one it asked for — a merge into an existing
            // snapshot keeps that snapshot's window, which may not be the requested one.
            windowNumber = snapshot.WindowNumber,
            snapshotStatus = snapshot.SnapshotStatus,
            // True when this post was absorbed into an existing snapshot rather than creating one,
            // so the addon can say "added to the current roster" instead of "posted".
            merged = mergeTarget is not null,
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
            // Window 1 opens here, and this never moves again — every snapshot's window number is
            // measured off it. The first `/lsm now` post IS the camp start for a snapshot-native
            // event, so it doubles as the grid anchor.
            WindowAnchorAtUtc = capturedAtUtc,
            CreatedByCharacterName = capturedByCharacterName,
            // Pre-select the camp from the monster name so officers don't
            // have to set it manually on every "/lsm now <monster>" event.
            EntryType = WindowEventEntryTypes.FromMonsterName(name),
        };
        _dbContext.WindowEvents.Add(windowEvent);
        await _dbContext.SaveChangesAsync(cancellationToken);
        return windowEvent;
    }

    // The snapshot an incoming post should be folded into: the most recent ACTIVE one on the same
    // Window Event captured within HnmConfig.SnapshotMergeWindow of it. Null starts a new snapshot.
    //
    // Purely time-based, with no roster-similarity test at all. That's the point — the old
    // duplicate check demanded 75% name overlap, so the case that matters most (two officers each
    // scanning their OWN alliance, barely overlapping) looked like two unrelated captures while two
    // scans of one alliance looked like a mistake. Officers scanning together inside the merge
    // window are building one roster no matter how much their lists overlap.
    //
    // Checked symmetrically (±window) so an addon retry carrying an older CapturedAtUtc still lands
    // in the right place instead of opening a snapshot behind the one it belongs to.
    //
    // A fold also requires the SAME spawn window. The merge window is deliberately shorter than any
    // real spawn window, but it can still straddle a boundary — two posts 2 minutes apart at 9:59
    // and 10:01 are in windows 6 and 7 of a 10-minute camp. Folding those would put window 7's
    // members under a snapshot labelled "Window 6", and a window label that lies is worse than an
    // extra row. So the boundary always wins over the merge.
    private async Task<AttendanceSnapshot?> FindMergeTargetSnapshotAsync(
        WindowEvent windowEvent,
        DateTime capturedAtUtc,
        int? snapshotWindow,
        CancellationToken cancellationToken)
    {
        var mergeWindow = HnmConfig.SnapshotMergeWindow(windowEvent.Name);
        var fromUtc = capturedAtUtc - mergeWindow;
        var toUtc = capturedAtUtc + mergeWindow;

        var candidates = _dbContext.AttendanceSnapshots
            .Include(item => item.Entries)
            .Where(item =>
                item.WindowEventId == windowEvent.Id &&
                item.SnapshotStatus == AttendanceSnapshotStatuses.Active &&
                item.CapturedAtUtc >= fromUtc &&
                item.CapturedAtUtc <= toUtc);

        // Branch rather than comparing two nullables: `x.WindowNumber == null`-valued parameter
        // translates to `= NULL` in SQL, which matches nothing.
        candidates = snapshotWindow.HasValue
            ? candidates.Where(item => item.WindowNumber == snapshotWindow.Value)
            : candidates.Where(item => item.WindowNumber == null);

        return await candidates
            .OrderByDescending(item => item.CapturedAtUtc)
            .FirstOrDefaultAsync(cancellationToken);
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
