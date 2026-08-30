using LinkshellManagerDiscordApp.Models;
using LinkshellManagerDiscordApp.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace LinkshellManagerDiscordApp.Controllers;

public sealed partial class ActivityDataController
{
    // ClosedEvents is ONE PAGE of the Attendance Archive, not the whole history — the four Closed*
    // fields describe which page, so the Activity can render the same tally and pager the web does.
    // ClosedQuery is echoed back rather than trusted from the client: it is the trimmed query the
    // page was actually built from, which is what the "no results for X" copy has to name.
    public sealed record ActivityWindowEventsResponse(
        IReadOnlyList<ActivityWindowEventDto> OpenEvents,
        IReadOnlyList<ActivityWindowEventDto> ClosedEvents,
        IReadOnlyList<ActivityWindowSnapshotDto> UnlinkedSnapshots,
        bool CanManage,
        IReadOnlyList<string> EntryTypeOptions,
        IReadOnlyList<string> RosterCharacterNames,
        string? ClosedQuery,
        int ClosedPage,
        int ClosedPageSize,
        int ClosedTotalCount,
        // How many unlinked captures exist versus how many are listed. Every /lsm now post lands
        // there now, so the list can genuinely be hiding some, and the panel says so.
        int UnlinkedTotalCount,
        int UnlinkedDisplayCap);

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
        int IgnoredSnapshotCount,
        // Captures still awaiting an officer's Confirm. Non-zero disables Post: those members are
        // missing from the combined roster, so publishing now would short them.
        int PendingSnapshotCount,
        // Alliances contributing to the combined roster, ascending.
        IReadOnlyList<int> AllianceNumbers,
        int CombinedMemberCount,
        IReadOnlyList<ActivityWindowSnapshotDto> Snapshots,
        IReadOnlyList<ActivityWindowCombinedMemberDto> CombinedMembers,
        double? DkpAmount,
        string? EntryType,
        DateTime? PostedToSheetUtc,
        // Non-null when this row came from ending an HNM camp rather than an addon "/lsm now"
        // capture. Drives the "Camp" tag so officers can tell the two apart — a camp row already
        // carries per-member amounts, a snapshot row does not.
        int? SourceEventId,
        // The captures filed as Misc, and how many. Split server-side so the two clients cannot
        // disagree about what counts as Misc.
        int MiscSnapshotCount,
        // Per-member DKP for anyone credited ONLY by Misc posts. Null = same as a window attendee.
        double? MiscDkpAmount,
        // This camp own window grid, for the slot picker. HasWindowGrid false means there are no
        // numbers to offer (Sky gods, farm NMs); Misc is still selectable.
        int WindowCount,
        bool HasWindowGrid);

    public sealed record ActivityWindowEventMemberDkpInput(string? CharacterName, double? DkpAmount);

    public sealed record ActivityWindowEventDkpRequest(
        double? DkpAmount,
        string? EntryType,
        IReadOnlyList<ActivityWindowEventMemberDkpInput>? MemberDkp,
        // Null means "misc pays what a window pays", which is the default.
        double? MiscDkpAmount = null);

    public sealed record ActivityWindowSnapshotDto(
        int Id,
        int? WindowEventId,
        string? Name,
        string SnapshotStatus,
        DateTime CapturedAtUtc,
        string? CapturedByCharacterName,
        string? PrimaryZone,
        int EntryCount,
        IReadOnlyList<ActivityWindowSnapshotEntryDto> Entries,
        // Which alliance posted this, and its label. The Activity renders these as a chip so a
        // multi-alliance camp reads as several rosters rather than one jumbled list.
        int? AllianceNumber,
        string AllianceLabel,
        // Awaiting an officer's Confirm: shown, but not in the combined roster and not paid.
        bool IsPending,
        // The spawn window this capture landed in, and its "Window 3 of 25" label.
        //
        // These were declared optional on the TypeScript side and never actually emitted, so the
        // Activity's window chip has been dead since it was written while the web equivalent
        // worked. Emitted now.
        int? WindowNumber,
        string? WindowLabel,
        // Window or Misc. Distinct from a null WindowNumber, which means the camp runs no grid.
        string SlotKind,
        bool IsMisc,
        // What the chip reads: "Misc", or the WindowLabel.
        string? SlotLabel);

    public sealed record ActivityWindowSnapshotEntryDto(
        int Id,
        string CharacterName,
        string? MainJob,
        int? MainJobLevel,
        string? SubJob,
        int? SubJobLevel,
        string? Zone,
        // Typed in by an officer rather than scanned. Same story as WindowLabel above: the
        // client already styles these rows, but the flag never reached it.
        bool AddedManually);

    public sealed record ActivityWindowCombinedMemberDto(
        string CharacterName,
        string? MainJob,
        int? MainJobLevel,
        string? SubJob,
        int? SubJobLevel,
        string? Zone,
        int SnapshotCount,
        // Which alliances this character was captured in, ascending.
        IReadOnlyList<int> AllianceNumbers,
        double? DkpAmountOverride,
        double? EffectiveDkpAmount,
        // "Window", "Misc" or "Both" — why this member is priced the way they are.
        string CreditSource);

    // WindowEventId attaches to an existing attendance event; Name find-or-creates one.
    // LinkedEventId is orthogonal to both: it records WHICH CAMP the snapshot belongs to,
    // so the camp's own card can show it. Sent when the officer picks a live camp from the
    // unlinked-snapshot dropdown, which does both at once — the name groups it for payroll,
    // the link puts it on the camp.
    // CreateNew forces a brand-new attendance event instead of folding into an open one of the
    // same name — what the "Create New Event" button means, as opposed to the dropdown's attach.
    // SlotKind/WindowNumber are the officer filing decision. Ingest classifies nothing any more,
    // so this is where a capture first becomes a window post or a misc post.
    public sealed record ActivityAttachWindowSnapshotRequest(
        int? WindowEventId, string? Name, int? LinkedEventId = null, bool CreateNew = false,
        string? SlotKind = null, int? WindowNumber = null);
    // Moves an already-filed capture between a window and Misc without detaching it.
    public sealed record ActivityWindowSnapshotSlotRequest(string? SlotKind, int? WindowNumber);
    public sealed record ActivityWindowEventRenameRequest(string? Name);
    public sealed record ActivityWindowSnapshotStatusRequest(string Status);
    public sealed record ActivityWindowSnapshotAllianceRequest(int AllianceNumber);
    // true = Confirm, false = Reject. One endpoint rather than two because both outcomes write the
    // same three fields and differ only in the status they land on.
    public sealed record ActivityWindowSnapshotVerifyRequest(bool Verified);
    public sealed record ActivityAddSnapshotEntryRequest(
        string? CharacterName,
        string? MainJob,
        int? MainJobLevel,
        string? SubJob,
        int? SubJobLevel,
        string? Zone);

    // Closed events per page in the Activity's Attendance Archive. Matches the web's
    // EventController.ClosedAttendancePageSize so the two archives page in lockstep.
    private const int ActivityClosedAttendancePageSize = 10;

    // attQ / attPage name the archive's search and page after the web's query string, so the two
    // surfaces read the same way. They are the ONLY paged part of this payload: open events and
    // unlinked snapshots are live work an officer has to see in full.
    [HttpGet("window-events")]
    public async Task<IActionResult> GetWindowEventsAsync(
        [FromQuery] int linkshellId,
        CancellationToken cancellationToken,
        [FromQuery] string? attQ = null,
        [FromQuery] int attPage = 1)
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

        // The archive: searched and paged in SQL, BEFORE the Includes, exactly as the web's
        // AttendanceSectionsBuilder.BuildClosedAsync does — each closed event drags its whole
        // snapshot/entry tree along, and this payload is polled while a camp is live.
        //
        // This replaced a flat .Take(25). That cap was invisible: the 26th closed event simply
        // wasn't there, and no search could reach it. Sharing the builder's predicate also means a
        // query matching a card on the web matches the same card here.
        var closedQuery = string.IsNullOrWhiteSpace(attQ) ? null : attQ.Trim();
        var closedBaseQuery = AttendanceSectionsBuilder.ApplyClosedSearch(
            _dbContext.WindowEvents
                .AsNoTracking()
                .Where(e => e.LinkshellId == linkshellId && e.Status == WindowEventStatuses.Closed),
            closedQuery);

        var closedTotalCount = await closedBaseQuery.CountAsync(cancellationToken);
        // Clamped, not stored: a search that narrows the archive to fewer pages while the client
        // sits on a high page would otherwise strand it on an empty one.
        var closedPage = Math.Clamp(
            attPage <= 0 ? 1 : attPage,
            1,
            Math.Max(1, (int)Math.Ceiling(closedTotalCount / (double)ActivityClosedAttendancePageSize)));

        var closedEvents = await closedBaseQuery
            .OrderByDescending(e => e.LastCapturedAtUtc)
            .Skip((closedPage - 1) * ActivityClosedAttendancePageSize)
            .Take(ActivityClosedAttendancePageSize)
            .Include(e => e.Snapshots).ThenInclude(s => s.Entries)
            .Include(e => e.MemberDkpOverrides)
            .ToListAsync(cancellationToken);

        // Counted as well as listed. Every /lsm now capture lands unlinked now, so the cap is
        // genuinely reachable and the panel has to be able to say it is hiding some rather than
        // letting the oldest quietly fall off the end.
        const int unlinkedDisplayCap = 100;
        var unlinkedTotalCount = await _dbContext.AttendanceSnapshots
            .AsNoTracking()
            .CountAsync(
                s => s.LinkshellId == linkshellId && s.WindowEventId == null && s.SnapshotStatus != AttendanceSnapshotStatuses.Ignored,
                cancellationToken);

        var unlinkedSnapshots = await _dbContext.AttendanceSnapshots
            .AsNoTracking()
            .Where(s => s.LinkshellId == linkshellId && s.WindowEventId == null && s.SnapshotStatus != AttendanceSnapshotStatuses.Ignored)
            .OrderByDescending(s => s.CapturedAtUtc)
            .ThenBy(s => s.AllianceNumber)
            .Take(unlinkedDisplayCap)
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
            // No parent event, so no window grid and no window label — deliberately, exactly as
            // AttendanceSectionsBuilder.MapSnapshot does for an unlinked snapshot on the web.
            unlinkedSnapshots.Select(s => MapActivityWindowSnapshot(s)).ToList(),
            canManage,
            WindowEventEntryTypes.All,
            rosterCharacterNames,
            closedQuery,
            closedPage,
            ActivityClosedAttendancePageSize,
            closedTotalCount,
            unlinkedTotalCount,
            unlinkedDisplayCap));
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
        // Resolved BEFORE the assignment, or the stored-value fallback would read the value we are
        // about to overwrite and a save carrying no misc field would silently reset it.
        var resolvedMisc = WindowEventDkp.ResolveMisc(
            request.MiscDkpAmount, windowEvent.Value.MiscDkpAmount, resolvedDkp);
        windowEvent.Value.MiscDkpAmount = request.MiscDkpAmount;
        WindowEventMiscDkp.ApplyMiscOverrides(
            windowEvent.Value, resolvedDkp, resolvedMisc,
            WindowEventMiscDkp.SubmittedNames(
                request.MemberDkp?.Select(m => new ViewModels.WindowEventMemberDkpInput
                {
                    CharacterName = m.CharacterName,
                    DkpAmount = m.DkpAmount,
                })));
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
        // Unverified captures are excluded from the combined roster, so publishing with any still
        // outstanding would pay a roster that is visibly missing people — and publishing is
        // one-way. Force the decision first; rejecting is one tap if the capture is junk.
        var pendingCount = await _windowEventLinks.CountPendingSnapshotsAsync(
            windowEvent.Value.Id, cancellationToken);
        if (pendingCount > 0)
        {
            return BadRequest(new
            {
                error = $"{pendingCount} snapshot{(pendingCount == 1 ? " is" : "s are")} awaiting "
                        + "verification. Confirm or reject them before publishing.",
            });
        }
        if (ValidateWindowEventDkp(request) is { } error) return BadRequest(new { error });

        // Both fields are gone from the UI; keep whatever the event already carries when the
        // client omits them — see WindowEventDkp.Resolve / WindowEventEntryTypes.Resolve.
        var resolvedDkp = WindowEventDkp.Resolve(request.DkpAmount, windowEvent.Value.DkpAmount);
        windowEvent.Value.DkpAmount = resolvedDkp;
        windowEvent.Value.EntryType = WindowEventEntryTypes.Resolve(
            request.EntryType, windowEvent.Value.EntryType, windowEvent.Value.Name);
        ApplyActivityMemberDkpOverrides(windowEvent.Value, resolvedDkp, request.MemberDkp);
        // Resolved BEFORE the assignment, or the stored-value fallback would read the value we are
        // about to overwrite and a save carrying no misc field would silently reset it.
        var resolvedMisc = WindowEventDkp.ResolveMisc(
            request.MiscDkpAmount, windowEvent.Value.MiscDkpAmount, resolvedDkp);
        windowEvent.Value.MiscDkpAmount = request.MiscDkpAmount;
        WindowEventMiscDkp.ApplyMiscOverrides(
            windowEvent.Value, resolvedDkp, resolvedMisc,
            WindowEventMiscDkp.SubmittedNames(
                request.MemberDkp?.Select(m => new ViewModels.WindowEventMemberDkpInput
                {
                    CharacterName = m.CharacterName,
                    DkpAmount = m.DkpAmount,
                })));
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
        // Resolved BEFORE the assignment, or the stored-value fallback would read the value we are
        // about to overwrite and a save carrying no misc field would silently reset it.
        var resolvedMisc = WindowEventDkp.ResolveMisc(
            request.MiscDkpAmount, windowEvent.Value.MiscDkpAmount, resolvedDkp);
        windowEvent.Value.MiscDkpAmount = request.MiscDkpAmount;
        WindowEventMiscDkp.ApplyMiscOverrides(
            windowEvent.Value, resolvedDkp, resolvedMisc,
            WindowEventMiscDkp.SubmittedNames(
                request.MemberDkp?.Select(m => new ViewModels.WindowEventMemberDkpInput
                {
                    CharacterName = m.CharacterName,
                    DkpAmount = m.DkpAmount,
                })));
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
        [FromServices] AllianceIdentityService allianceIdentity,
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
            // Falls back to the snapshot's own name so "Create New Event" works straight off a
            // capture the addon already named, with no retyping.
            var name = TrimToNull(request.Name, 128) ?? TrimToNull(snapshot.Name, 128);
            if (name is null)
            {
                return BadRequest(new { error = "Choose an existing attendance event, or name this snapshot first." });
            }
            windowEvent = await _windowEventLinks.FindOrCreateAsync(
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

        WindowEventLinkService.ApplySlot(snapshot, windowEvent, request.SlotKind, request.WindowNumber);

        // The alliance NUMBER is an ordinal within THIS camp, so it is assigned on attach rather
        // than at ingest -- until a capture is filed there is no camp to be first or second on.
        snapshot.AllianceNumber = await allianceIdentity.ResolveNumberAsync(
            windowEvent.Id, snapshot.AllianceKey, cancellationToken);

        // Note what is NOT here: the snapshot's status. Filing a capture and vouching for it are
        // separate decisions, and this used to force Active — which would have verified a Pending
        // capture the instant an officer sorted it into the right camp.
        _windowEventLinks.Attach(snapshot, windowEvent);
        await _dbContext.SaveChangesAsync(cancellationToken);
        return Ok(new { success = true });
    }

    // Renames the SNAPSHOT itself, and nothing else — the Activity twin of
    // WindowEventsController.RenameSnapshot. Separate from attach's name field, which conflated
    // naming a capture with creating an event to file it under.
    [HttpPost("window-events/snapshots/{snapshotId:int}/rename")]
    public async Task<IActionResult> RenameWindowSnapshotAsync(
        int snapshotId,
        [FromBody] ActivityWindowEventRenameRequest request,
        CancellationToken cancellationToken)
    {
        var snapshot = await _dbContext.AttendanceSnapshots
            .FirstOrDefaultAsync(s => s.Id == snapshotId, cancellationToken);
        if (snapshot is null) return NotFound(new { error = "Snapshot not found." });

        var manageResult = await RequireWindowEventManagerAsync(snapshot.LinkshellId, cancellationToken);
        if (manageResult is not null) return manageResult;

        snapshot.Name = TrimToNull(request.Name, 128);
        await _dbContext.SaveChangesAsync(cancellationToken);
        return Ok(new { success = true });
    }

    // Moves an already-filed capture between a numbered window and Misc, in place.
    //
    // Filing is entirely manual now, so mis-filing is not an edge case — it is the expected cost of
    // the trade. Detach-and-refile works but throws the link away and makes the officer hunt for
    // the capture again in the triage queue.
    [HttpPost("window-events/snapshots/{snapshotId:int}/slot")]
    public async Task<IActionResult> SetWindowSnapshotSlotAsync(
        int snapshotId,
        [FromBody] ActivityWindowSnapshotSlotRequest request,
        CancellationToken cancellationToken)
    {
        var snapshot = await _dbContext.AttendanceSnapshots
            .Include(s => s.WindowEvent)
            .FirstOrDefaultAsync(s => s.Id == snapshotId, cancellationToken);
        if (snapshot is null) return NotFound(new { error = "Snapshot not found." });

        var manageResult = await RequireWindowEventManagerAsync(snapshot.LinkshellId, cancellationToken);
        if (manageResult is not null) return manageResult;

        if (snapshot.WindowEvent is null)
        {
            return BadRequest(new { error = "File this capture to an event before choosing a slot." });
        }

        // Re-slotting a POSTED event would move members between the window rate and the misc rate
        // after the DKP was paid, and this action does not reconcile the ledger. Update the posted
        // event instead, which does.
        if (snapshot.WindowEvent.PostedToSheetAt.HasValue)
        {
            return BadRequest(new
            {
                error = "This event is already posted. Use Update on the event to change DKP after a re-slot.",
            });
        }

        WindowEventLinkService.ApplySlot(snapshot, snapshot.WindowEvent, request.SlotKind, request.WindowNumber);
        await _dbContext.SaveChangesAsync(cancellationToken);
        return Ok(new { success = true });
    }

    // Corrects the alliance a poster claimed. The number cannot be detected in game — the client
    // only ever sees your own alliance — so it is typed by a member under pressure at a pop, and
    // getting it wrong collapses two alliances into one row on the card.
    [HttpPost("window-events/snapshots/{snapshotId:int}/alliance")]
    public async Task<IActionResult> SetWindowSnapshotAllianceAsync(
        int snapshotId,
        [FromBody] ActivityWindowSnapshotAllianceRequest request,
        CancellationToken cancellationToken)
    {
        var snapshot = await _dbContext.AttendanceSnapshots
            .FirstOrDefaultAsync(s => s.Id == snapshotId, cancellationToken);
        if (snapshot is null) return NotFound(new { error = "Snapshot not found." });

        var manageResult = await RequireWindowEventManagerAsync(snapshot.LinkshellId, cancellationToken);
        if (manageResult is not null) return manageResult;

        snapshot.AllianceNumber = AttendanceSnapshotAlliances.Resolve(request.AllianceNumber);
        await _dbContext.SaveChangesAsync(cancellationToken);
        return Ok(new { success = true });
    }

    // Confirm (verified: true) or Reject (verified: false) a member-posted capture.
    //
    // Reject lands on Ignored rather than getting a status of its own: every query that has to
    // exclude a rejected snapshot already excludes Ignored, so a sixth status would have meant six
    // new filters and one of them eventually being missed. Either way the verifier is stamped, so
    // "a person looked at this" is distinguishable from "nobody has triaged it".
    [HttpPost("window-events/snapshots/{snapshotId:int}/verify")]
    public async Task<IActionResult> VerifyWindowSnapshotAsync(
        int snapshotId,
        [FromBody] ActivityWindowSnapshotVerifyRequest request,
        CancellationToken cancellationToken)
    {
        var snapshot = await _dbContext.AttendanceSnapshots
            .FirstOrDefaultAsync(s => s.Id == snapshotId, cancellationToken);
        if (snapshot is null) return NotFound(new { error = "Snapshot not found." });

        var manageResult = await RequireWindowEventManagerAsync(snapshot.LinkshellId, cancellationToken);
        if (manageResult is not null) return manageResult;

        var appUser = await ResolveAppUserAsync(cancellationToken);
        if (appUser is null) return Unauthorized(new { error = "Sign in to review snapshots." });

        snapshot.SnapshotStatus = request.Verified
            ? AttendanceSnapshotStatuses.Active
            : AttendanceSnapshotStatuses.Ignored;
        snapshot.VerifiedAtUtc = DateTime.UtcNow;
        snapshot.VerifiedByAppUserId = appUser.Id;
        await _dbContext.SaveChangesAsync(cancellationToken);
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
            AttendanceSnapshotStatuses.Ignored => AttendanceSnapshotStatuses.Ignored,
            // Accepted so an officer can send a capture BACK for review after promoting it by
            // mistake. Confirming still goes through the verify endpoint, the only path that
            // records who vouched for it.
            AttendanceSnapshotStatuses.Pending => AttendanceSnapshotStatuses.Pending,
            _ => null
        };
        if (status is null) return BadRequest(new { error = "Unsupported snapshot status." });

        snapshot.SnapshotStatus = status;
        if (status is AttendanceSnapshotStatuses.Active or AttendanceSnapshotStatuses.Ignored)
        {
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
            // Snapshots too: the misc rate applies to members credited ONLY by Misc captures, and
            // nothing on this path used to care how a capture was filed.
            query = query.Include(e => e.Snapshots).ThenInclude(s => s.Entries);
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

    private static ActivityWindowEventDto MapActivityWindowEvent(WindowEvent item)
    {
        // Pass the event itself: each snapshot needs its cadence (for "of 25") and its grid anchor
        // (to name the window of a capture taken before window numbering existed).
        var snapshots = item.Snapshots
            .OrderByDescending(s => s.CapturedAtUtc)
            .ThenBy(s => s.AllianceNumber)
            .Select(s => MapActivityWindowSnapshot(s, item))
            .ToList();
        var overrides = item.MemberDkpOverrides
            .Where(o => !string.IsNullOrWhiteSpace(o.CharacterName))
            .GroupBy(o => o.CharacterName.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().DkpAmount, StringComparer.OrdinalIgnoreCase);
        var combined = BuildActivityCombinedMembers(item.Snapshots, overrides, item.DkpAmount, item.MiscDkpAmount);
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
            snapshots.Count(s => s.SnapshotStatus == AttendanceSnapshotStatuses.Ignored),
            snapshots.Count(s => s.IsPending),
            // From ACTIVE snapshots only, matching BuildActivityCombinedMembers: the header count
            // has to describe the roster below it, and a pending alliance is not in it yet.
            item.Snapshots
                .Where(s => s.SnapshotStatus == AttendanceSnapshotStatuses.Active && s.AllianceNumber.HasValue)
                .Select(s => s.AllianceNumber!.Value)
                .Distinct()
                .OrderBy(n => n)
                .ToList(),
            combined.Count,
            snapshots,
            combined,
            item.DkpAmount,
            item.EntryType,
            item.PostedToSheetAt,
            item.SourceEventId,
            snapshots.Count(s => s.IsMisc),
            item.MiscDkpAmount,
            WindowEventWindowGrid.WindowCount(item),
            WindowEventWindowGrid.Minutes(item) > 0);
    }

    // `windowEvent` supplies the cadence and grid anchor used to name the spawn window, exactly as
    // AttendanceSectionsBuilder.MapSnapshot does for the web. Omitted for an UNLINKED snapshot,
    // which has no camp and therefore no window.
    private static ActivityWindowSnapshotDto MapActivityWindowSnapshot(
        AttendanceSnapshot snapshot, WindowEvent? windowEvent = null)
    {
        // Scanned names first, alphabetically; hand-added people underneath in the order an officer
        // entered them. Mirrors the web ordering so the two surfaces list a roster identically.
        var entries = snapshot.Entries
            .Where(e => !e.AddedManually)
            .OrderBy(e => e.CharacterName, StringComparer.OrdinalIgnoreCase)
            .Concat(snapshot.Entries.Where(e => e.AddedManually).OrderBy(e => e.Id))
            .Select(e => new ActivityWindowSnapshotEntryDto(
                e.Id,
                e.CharacterName,
                e.MainJob,
                e.MainJobLevel,
                e.SubJob,
                e.SubJobLevel,
                e.Zone,
                e.AddedManually))
            .ToList();

        // The STORED window number wins — it was pinned against the grid as it stood at capture.
        // Snapshots posted before window numbering existed have none, so theirs is derived here.
        var gridWindows = windowEvent is null ? (int?)null : WindowEventWindowGrid.WindowCount(windowEvent);
        var hasGrid = windowEvent is not null && WindowEventWindowGrid.Minutes(windowEvent) > 0;
        // A Misc capture has NO window, and the fallback must not invent one for it. Misc stores a
        // null WindowNumber, which is exactly the shape that triggers the derivation — so without
        // this test a misc post on a gridded camp would render "Window 4 of 25" beside its own Misc
        // chip. Same guard as AttendanceSectionsBuilder.MapSnapshot on the web.
        var isMisc = AttendanceSnapshotSlotKinds.IsMisc(snapshot.SlotKind);
        var resolvedWindow = isMisc
            ? null
            : snapshot.WindowNumber
              ?? (windowEvent is not null
                  ? WindowEventWindowGrid.SnapshotWindowNumber(windowEvent, snapshot.CapturedAtUtc)
                  : null);

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
            snapshot.CapturedAtUtc,
            snapshot.CapturedByCharacterName,
            primaryZone,
            snapshot.EntryCount,
            entries,
            snapshot.AllianceNumber,
            AttendanceSnapshotAlliances.Label(snapshot.AllianceNumber, snapshot.AllianceKey, snapshot.AllianceLeaderName),
            snapshot.SnapshotStatus == AttendanceSnapshotStatuses.Pending,
            resolvedWindow,
            resolvedWindow is { } window
                ? (hasGrid && gridWindows is { } total ? $"Window {window} of {total}" : $"Window {window}")
                : null,
            AttendanceSnapshotSlotKinds.Resolve(snapshot.SlotKind),
            isMisc,
            isMisc
                ? "Misc"
                : resolvedWindow is { } slotWindow
                    ? (hasGrid && gridWindows is { } slotTotal ? $"Window {slotWindow} of {slotTotal}" : $"Window {slotWindow}")
                    : null);
    }

    private static List<ActivityWindowCombinedMemberDto> BuildActivityCombinedMembers(
        IEnumerable<AttendanceSnapshot> snapshots,
        IDictionary<string, double>? memberDkpOverrides = null,
        double? defaultDkpAmount = null,
        double? miscDkpAmount = null)
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

                // A member seen in ANY window capture is an ordinary attendee even if they also
                // appear in a misc post. Same rule as the web builder, deliberately duplicated
                // rather than shared: these two mappers already mirror each other line for line.
                var sawWindow = g.Any(x => !AttendanceSnapshotSlotKinds.IsMisc(x.Snapshot.SlotKind));
                var sawMisc = g.Any(x => AttendanceSnapshotSlotKinds.IsMisc(x.Snapshot.SlotKind));
                var creditSource = sawWindow && sawMisc
                    ? "Both"
                    : sawMisc ? AttendanceSnapshotSlotKinds.Misc : AttendanceSnapshotSlotKinds.Window;
                var baseAmount = sawMisc && !sawWindow ? miscDkpAmount ?? defaultDkpAmount : defaultDkpAmount;
                return new ActivityWindowCombinedMemberDto(
                    g.Key,
                    latest.MainJob,
                    latest.MainJobLevel,
                    latest.SubJob,
                    latest.SubJobLevel,
                    latest.Zone,
                    g.Select(x => x.Snapshot.Id).Distinct().Count(),
                    g.Where(x => x.Snapshot.AllianceNumber.HasValue)
                        .Select(x => x.Snapshot.AllianceNumber!.Value)
                        .Distinct()
                        .OrderBy(n => n)
                        .ToList(),
                    overrideAmount,
                    overrideAmount ?? baseAmount,
                    creditSource);
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
