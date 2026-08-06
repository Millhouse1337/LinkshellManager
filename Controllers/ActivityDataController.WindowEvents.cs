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
        bool CanManage,
        IReadOnlyList<string> EntryTypeOptions,
        IReadOnlyList<string> RosterCharacterNames);

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
        IReadOnlyList<ActivityWindowCombinedMemberDto> CombinedMembers,
        double? DkpAmount,
        string? EntryType,
        DateTime? PostedToSheetUtc,
        // Non-null when this row came from ending an HNM camp rather than an addon "/lsm now"
        // capture. Drives the "Camp" tag so officers can tell the two apart — a camp row already
        // carries per-member amounts, a snapshot row doesn't.
        int? SourceEventId);

    public sealed record ActivityWindowEventMemberDkpInput(string? CharacterName, double? DkpAmount);

    public sealed record ActivityWindowEventDkpRequest(
        double? DkpAmount,
        string? EntryType,
        IReadOnlyList<ActivityWindowEventMemberDkpInput>? MemberDkp);

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
        int Id,
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
        int SnapshotCount,
        double? DkpAmountOverride,
        double? EffectiveDkpAmount);

    // WindowEventId attaches to an existing attendance event; Name find-or-creates one.
    // LinkedEventId is orthogonal to both: it records WHICH CAMP the snapshot belongs to,
    // so the camp's own card can show it. Sent when the officer picks a live camp from the
    // unlinked-snapshot dropdown, which does both at once — the name groups it for payroll,
    // the link puts it on the camp.
    // CreateNew forces a brand-new attendance event instead of folding into an open one of the
    // same name — what the "Create New Event" button means, as opposed to the dropdown's attach.
    public sealed record ActivityAttachWindowSnapshotRequest(
        int? WindowEventId, string? Name, int? LinkedEventId = null, bool CreateNew = false);
    public sealed record ActivityWindowEventRenameRequest(string? Name);
    public sealed record ActivityWindowSnapshotStatusRequest(string Status);
    public sealed record ActivityAddSnapshotEntryRequest(
        string? CharacterName,
        string? MainJob,
        int? MainJobLevel,
        string? SubJob,
        int? SubJobLevel,
        string? Zone);

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
            .Include(e => e.MemberDkpOverrides)
            .ToListAsync(cancellationToken);

        var closedEvents = await _dbContext.WindowEvents
            .AsNoTracking()
            .Where(e => e.LinkshellId == linkshellId && e.Status == WindowEventStatuses.Closed)
            .OrderByDescending(e => e.LastCapturedAtUtc)
            .Take(25)
            .Include(e => e.Snapshots).ThenInclude(s => s.Entries)
            .Include(e => e.MemberDkpOverrides)
            .ToListAsync(cancellationToken);

        var unlinkedSnapshots = await _dbContext.AttendanceSnapshots
            .AsNoTracking()
            .Where(s => s.LinkshellId == linkshellId && s.WindowEventId == null && s.SnapshotStatus != AttendanceSnapshotStatuses.Ignored)
            .OrderByDescending(s => s.CapturedAtUtc)
            .Take(100)
            .Include(s => s.Entries)
            .ToListAsync(cancellationToken);

        // Roster character names (typeahead for the "Add a character by name…"
        // input on snapshot editors). Only fetched for managers since they're
        // the only ones who can edit a snapshot's roster.
        var rosterCharacterNames = canManage
            ? await _dbContext.AppUserLinkshells
                .AsNoTracking()
                .Where(link => link.LinkshellId == linkshellId
                               && link.CharacterName != null
                               && link.CharacterName != "")
                .Select(link => link.CharacterName!)
                .Distinct()
                .OrderBy(name => name)
                .ToListAsync(cancellationToken)
            : new List<string>();

        return Ok(new ActivityWindowEventsResponse(
            openEvents.Select(MapActivityWindowEvent).ToList(),
            closedEvents.Select(MapActivityWindowEvent).ToList(),
            unlinkedSnapshots.Select(MapActivityWindowSnapshot).ToList(),
            canManage,
            WindowEventEntryTypes.All,
            rosterCharacterNames));
    }

    // Sets DKP + Entry Type on an UNposted event without pushing rows (draft).
    // Ports WindowEventsController.SaveDetails including per-character DKP
    // overrides so the Activity matches web parity.
    [HttpPost("window-events/{windowEventId:int}/save-dkp")]
    public async Task<IActionResult> SaveWindowEventDkpAsync(
        int windowEventId,
        [FromBody] ActivityWindowEventDkpRequest request,
        CancellationToken cancellationToken)
    {
        var windowEvent = await LoadManageableWindowEventAsync(
            windowEventId, cancellationToken, includeMemberDkpOverrides: true);
        if (windowEvent.Result is not null) return windowEvent.Result;

        if (windowEvent.Value!.PostedToSheetAt.HasValue)
        {
            return BadRequest(new { error = "This event is already posted. Use Update to change DKP or Entry Type." });
        }
        if (ValidateWindowEventDkp(request) is { } error) return BadRequest(new { error });

        // Both fields are gone from the UI; keep whatever the event already carries when the
        // client omits them — see WindowEventDkp.Resolve / WindowEventEntryTypes.Resolve.
        var resolvedDkp = WindowEventDkp.Resolve(request.DkpAmount, windowEvent.Value.DkpAmount);
        windowEvent.Value.DkpAmount = resolvedDkp;
        windowEvent.Value.EntryType = WindowEventEntryTypes.Resolve(
            request.EntryType, windowEvent.Value.EntryType, windowEvent.Value.Name);
        ApplyActivityMemberDkpOverrides(windowEvent.Value, resolvedDkp, request.MemberDkp);
        await _dbContext.SaveChangesAsync(cancellationToken);
        return Ok(new { success = true });
    }

    // Publishes a window event: sets DKP + Entry Type (and any per-character
    // overrides), marks it posted, and materializes the per-member SnapshotEarned
    // DKP ledger. Ports WindowEventsController.PostToSheet.
    [HttpPost("window-events/{windowEventId:int}/post")]
    public async Task<IActionResult> PostWindowEventToSheetAsync(
        int windowEventId,
        [FromBody] ActivityWindowEventDkpRequest request,
        CancellationToken cancellationToken)
    {
        var windowEvent = await LoadManageableWindowEventAsync(
            windowEventId, cancellationToken, includeMemberDkpOverrides: true);
        if (windowEvent.Result is not null) return windowEvent.Result;

        if (windowEvent.Value!.PostedToSheetAt.HasValue)
        {
            return BadRequest(new { error = "This event has already been published." });
        }
        if (ValidateWindowEventDkp(request) is { } error) return BadRequest(new { error });

        // Both fields are gone from the UI; keep whatever the event already carries when the
        // client omits them — see WindowEventDkp.Resolve / WindowEventEntryTypes.Resolve.
        var resolvedDkp = WindowEventDkp.Resolve(request.DkpAmount, windowEvent.Value.DkpAmount);
        windowEvent.Value.DkpAmount = resolvedDkp;
        windowEvent.Value.EntryType = WindowEventEntryTypes.Resolve(
            request.EntryType, windowEvent.Value.EntryType, windowEvent.Value.Name);
        ApplyActivityMemberDkpOverrides(windowEvent.Value, resolvedDkp, request.MemberDkp);
        windowEvent.Value.PostedToSheetAt = DateTime.UtcNow;
        await _dbContext.SaveChangesAsync(cancellationToken);

        // Publish = credit DKP. Materialize the per-member SnapshotEarned ledger
        // rows now that the event is marked posted (idempotent).
        await _windowEventDkpLedger.EnsurePostedWindowEventLedgerEntriesAsync(windowEvent.Value.Id, cancellationToken);
        return Ok(new { success = true });
    }

    // Edits a published event's DKP + Entry Type (and overrides) and reconciles
    // the existing ledger entries + per-member LinkshellDkp totals by the delta.
    [HttpPost("window-events/{windowEventId:int}/edit-posted")]
    public async Task<IActionResult> EditPostedWindowEventAsync(
        int windowEventId,
        [FromBody] ActivityWindowEventDkpRequest request,
        CancellationToken cancellationToken)
    {
        var windowEvent = await LoadManageableWindowEventAsync(
            windowEventId, cancellationToken, includeMemberDkpOverrides: true);
        if (windowEvent.Result is not null) return windowEvent.Result;

        if (!windowEvent.Value!.PostedToSheetAt.HasValue)
        {
            return BadRequest(new { error = "This event hasn't been published yet. Use Post to publish it." });
        }
        if (ValidateWindowEventDkp(request) is { } error) return BadRequest(new { error });

        // Both fields are gone from the UI; keep whatever the event already carries when the
        // client omits them — see WindowEventDkp.Resolve / WindowEventEntryTypes.Resolve.
        var resolvedDkp = WindowEventDkp.Resolve(request.DkpAmount, windowEvent.Value.DkpAmount);
        windowEvent.Value.DkpAmount = resolvedDkp;
        windowEvent.Value.EntryType = WindowEventEntryTypes.Resolve(
            request.EntryType, windowEvent.Value.EntryType, windowEvent.Value.Name);
        ApplyActivityMemberDkpOverrides(windowEvent.Value, resolvedDkp, request.MemberDkp);
        await _dbContext.SaveChangesAsync(cancellationToken);

        await _windowEventDkpLedger.ReconcilePostedWindowEventLedgerAsync(windowEvent.Value.Id, cancellationToken);
        return Ok(new { success = true });
    }

    // Permanently deletes a Window Event. Linked snapshots become unlinked
    // (FK is SetNull) rather than destroyed. Mirrors WindowEventsController.Delete.
    [HttpPost("window-events/{windowEventId:int}/delete")]
    public async Task<IActionResult> DeleteWindowEventAsync(
        int windowEventId,
        CancellationToken cancellationToken)
    {
        var windowEvent = await LoadManageableWindowEventAsync(windowEventId, cancellationToken);
        if (windowEvent.Result is not null) return windowEvent.Result;

        _dbContext.WindowEvents.Remove(windowEvent.Value!);
        await _dbContext.SaveChangesAsync(cancellationToken);
        return Ok(new { success = true });
    }

    // Hard-deletes a snapshot (and its entries). Mirrors
    // WindowEventsController.DeleteSnapshot.
    [HttpPost("window-events/snapshots/{snapshotId:int}/delete")]
    public async Task<IActionResult> DeleteWindowSnapshotAsync(
        int snapshotId,
        CancellationToken cancellationToken)
    {
        var snapshot = await _dbContext.AttendanceSnapshots
            .Include(s => s.Entries)
            .FirstOrDefaultAsync(s => s.Id == snapshotId, cancellationToken);
        if (snapshot is null) return NotFound(new { error = "Snapshot not found." });

        var manageResult = await RequireWindowEventManagerAsync(snapshot.LinkshellId, cancellationToken);
        if (manageResult is not null) return manageResult;

        if (snapshot.Entries.Count > 0)
        {
            _dbContext.AttendanceSnapshotEntries.RemoveRange(snapshot.Entries);
        }
        _dbContext.AttendanceSnapshots.Remove(snapshot);
        await _dbContext.SaveChangesAsync(cancellationToken);
        return Ok(new { success = true });
    }

    // Removes one person from a snapshot (officer correction). Mirrors
    // WindowEventsController.DeleteSnapshotEntry.
    [HttpPost("window-events/snapshots/{snapshotId:int}/entries/{entryId:int}/delete")]
    public async Task<IActionResult> DeleteSnapshotEntryAsync(
        int snapshotId,
        int entryId,
        CancellationToken cancellationToken)
    {
        var snapshot = await _dbContext.AttendanceSnapshots
            .Include(s => s.Entries)
            .FirstOrDefaultAsync(s => s.Id == snapshotId, cancellationToken);
        if (snapshot is null) return NotFound(new { error = "Snapshot not found." });

        var manageResult = await RequireWindowEventManagerAsync(snapshot.LinkshellId, cancellationToken);
        if (manageResult is not null) return manageResult;

        var entry = snapshot.Entries.FirstOrDefault(e => e.Id == entryId);
        if (entry is null) return NotFound(new { error = "Entry not found." });

        _dbContext.AttendanceSnapshotEntries.Remove(entry);
        snapshot.Entries.Remove(entry);
        snapshot.EntryCount = snapshot.Entries.Count;
        await _dbContext.SaveChangesAsync(cancellationToken);
        return Ok(new { success = true });
    }

    // Adds a person the addon missed. Mirrors
    // WindowEventsController.AddSnapshotEntry (alliance cap = 18, name de-dupe).
    [HttpPost("window-events/snapshots/{snapshotId:int}/entries")]
    public async Task<IActionResult> AddSnapshotEntryAsync(
        int snapshotId,
        [FromBody] ActivityAddSnapshotEntryRequest request,
        CancellationToken cancellationToken)
    {
        var snapshot = await _dbContext.AttendanceSnapshots
            .Include(s => s.Entries)
            .FirstOrDefaultAsync(s => s.Id == snapshotId, cancellationToken);
        if (snapshot is null) return NotFound(new { error = "Snapshot not found." });

        var manageResult = await RequireWindowEventManagerAsync(snapshot.LinkshellId, cancellationToken);
        if (manageResult is not null) return manageResult;

        var name = TrimToNull(request.CharacterName, 256);
        if (name is null) return BadRequest(new { error = "Character name is required to add a person." });

        if (snapshot.Entries.Count >= 18)
        {
            return BadRequest(new { error = "Snapshot already has the 18-member alliance maximum." });
        }
        if (snapshot.Entries.Any(e =>
                string.Equals(e.CharacterName.Trim(), name, StringComparison.OrdinalIgnoreCase)))
        {
            return BadRequest(new { error = $"{name} is already in this snapshot." });
        }

        snapshot.Entries.Add(new AttendanceSnapshotEntry
        {
            SnapshotId = snapshot.Id,
            CharacterName = name,
            MainJob = TrimToNull(request.MainJob, 8),
            MainJobLevel = request.MainJobLevel,
            SubJob = TrimToNull(request.SubJob, 8),
            SubJobLevel = request.SubJobLevel,
            Zone = TrimToNull(request.Zone, 128),
            AddedManually = true, // typed in by an officer, not scanned — sorts last and renders tinted
        });
        snapshot.EntryCount = snapshot.Entries.Count;
        await _dbContext.SaveChangesAsync(cancellationToken);
        return Ok(new { success = true });
    }

    private static string? ValidateWindowEventDkp(ActivityWindowEventDkpRequest request)
    {
        // A NEGATIVE amount is still a client bug worth rejecting; a MISSING one is now normal —
        // the Default DKP input is gone (DKP is set per snapshot), so callers run it through
        // WindowEventDkp.Resolve, which keeps the stored baseline.
        if (request.DkpAmount is { } dkp && dkp < 0)
        {
            return "DKP amount must be zero or greater.";
        }
        // Entry Type is deliberately NOT validated either: it's auto-tagged from the monster at
        // creation, and each caller runs it through WindowEventEntryTypes.Resolve, which always
        // produces a valid tag.
        if (request.MemberDkp is not null)
        {
            foreach (var input in request.MemberDkp)
            {
                if (input.DkpAmount.HasValue && input.DkpAmount.Value < 0)
                {
                    return $"Per-character DKP for \"{input.CharacterName}\" must be zero or greater.";
                }
            }
        }
        return null;
    }

    // Mirrors WindowEventsController.ApplyMemberDkpOverrides: characters whose
    // value matches the event default (or is blank) have their override row
    // removed; differing values get upserted.
    private static void ApplyActivityMemberDkpOverrides(
        WindowEvent windowEvent,
        double defaultDkpAmount,
        IReadOnlyList<ActivityWindowEventMemberDkpInput>? inputs)
    {
        if (inputs is null) return;

        var existingByName = windowEvent.MemberDkpOverrides
            .Where(o => !string.IsNullOrWhiteSpace(o.CharacterName))
            .GroupBy(o => o.CharacterName.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        foreach (var input in inputs)
        {
            var name = input.CharacterName?.Trim();
            if (string.IsNullOrWhiteSpace(name)) continue;

            existingByName.TryGetValue(name, out var existing);

            if (!input.DkpAmount.HasValue ||
                Math.Abs(input.DkpAmount.Value - defaultDkpAmount) < 0.0001)
            {
                if (existing is not null)
                {
                    windowEvent.MemberDkpOverrides.Remove(existing);
                }
                continue;
            }

            if (existing is null)
            {
                windowEvent.MemberDkpOverrides.Add(new WindowEventMemberDkp
                {
                    WindowEventId = windowEvent.Id,
                    CharacterName = name,
                    DkpAmount = input.DkpAmount.Value,
                });
            }
            else if (Math.Abs(existing.DkpAmount - input.DkpAmount.Value) > 0.0001)
            {
                existing.DkpAmount = input.DkpAmount.Value;
            }
        }
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
                cancellationToken,
                forceNew: request.CreateNew);
            snapshot.Name ??= name;
        }

        if (windowEvent is null) return NotFound(new { error = "Window Event not found." });

        // Camp association, when the officer picked one. Verified against the snapshot's own
        // linkshell because it arrives from the client; an id that doesn't belong is dropped
        // rather than rejected, so a stale pick still completes the attach it was really for.
        if (request.LinkedEventId is int linkedEventId && linkedEventId > 0)
        {
            var campOwned = await _dbContext.Events
                .AsNoTracking()
                .AnyAsync(e => e.Id == linkedEventId && e.LinkshellId == snapshot.LinkshellId, cancellationToken);
            if (campOwned)
            {
                snapshot.LinkedEventId = linkedEventId;
            }
        }

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
        CancellationToken cancellationToken,
        bool includeMemberDkpOverrides = false)
    {
        var query = _dbContext.WindowEvents.AsQueryable();
        if (includeMemberDkpOverrides)
        {
            query = query.Include(e => e.MemberDkpOverrides);
        }
        var windowEvent = await query.FirstOrDefaultAsync(e => e.Id == windowEventId, cancellationToken);
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

    // `forceNew` skips the reuse lookup and always mints a fresh event.
    //
    // Reuse is right when the name is a routing hint — an addon post, or an officer typing the
    // monster to file this snapshot with the rest of that camp's. It is WRONG when the officer
    // pressed a button that says "Create New Event": silently folding into a 20-hour-old event
    // of the same name is the opposite of what they asked for, and on a repeat camp the same
    // monster name comes round often.
    private async Task<WindowEvent> FindOrCreateActivityWindowEventAsync(
        int linkshellId,
        string name,
        DateTime capturedAtUtc,
        string? capturedByCharacterName,
        DateTime nowUtc,
        CancellationToken cancellationToken,
        bool forceNew = false)
    {
        var normalized = NormalizeWindowName(name)!;
        if (!forceNew)
        {
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
            // have to set it manually on every newly created event.
            EntryType = WindowEventEntryTypes.FromMonsterName(name),
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
        var overrides = item.MemberDkpOverrides
            .Where(o => !string.IsNullOrWhiteSpace(o.CharacterName))
            .GroupBy(o => o.CharacterName.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().DkpAmount, StringComparer.OrdinalIgnoreCase);
        var combined = BuildActivityCombinedMembers(item.Snapshots, overrides, item.DkpAmount);
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
            combined,
            item.DkpAmount,
            item.EntryType,
            item.PostedToSheetAt,
            item.SourceEventId);
    }

    private static ActivityWindowSnapshotDto MapActivityWindowSnapshot(AttendanceSnapshot snapshot)
    {
        var entries = snapshot.Entries
            .OrderBy(e => e.CharacterName, StringComparer.OrdinalIgnoreCase)
            .Select(e => new ActivityWindowSnapshotEntryDto(
                e.Id,
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

    private static List<ActivityWindowCombinedMemberDto> BuildActivityCombinedMembers(
        IEnumerable<AttendanceSnapshot> snapshots,
        IDictionary<string, double>? memberDkpOverrides = null,
        double? defaultDkpAmount = null)
    {
        return snapshots
            .Where(s => s.SnapshotStatus == AttendanceSnapshotStatuses.Active)
            .SelectMany(s => s.Entries.Select(e => new { Snapshot = s, Entry = e }))
            .GroupBy(x => x.Entry.CharacterName.Trim(), StringComparer.OrdinalIgnoreCase)
            .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
            .Select(g =>
            {
                var latest = g.OrderByDescending(x => x.Snapshot.CapturedAtUtc).First().Entry;
                double? overrideAmount = null;
                if (memberDkpOverrides is not null && memberDkpOverrides.TryGetValue(g.Key, out var found))
                {
                    overrideAmount = found;
                }
                return new ActivityWindowCombinedMemberDto(
                    g.Key,
                    latest.MainJob,
                    latest.MainJobLevel,
                    latest.SubJob,
                    latest.SubJobLevel,
                    latest.Zone,
                    g.Select(x => x.Snapshot.Id).Distinct().Count(),
                    overrideAmount,
                    overrideAmount ?? defaultDkpAmount);
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
