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
    private readonly ApplicationDbContext _db;
    private readonly UserManager<AppUser> _userManager;
    private readonly TimeZoneConversionService _timeZones;
    private readonly WindowEventDkpLedgerService _windowEventDkpLedger;
    private readonly AttendanceSectionsBuilder _attendanceSections;

    public WindowEventsController(
        ApplicationDbContext db,
        UserManager<AppUser> userManager,
        TimeZoneConversionService timeZones,
        WindowEventDkpLedgerService windowEventDkpLedger,
        AttendanceSectionsBuilder attendanceSections)
    {
        _db = db;
        _userManager = userManager;
        _timeZones = timeZones;
        _windowEventDkpLedger = windowEventDkpLedger;
        _attendanceSections = attendanceSections;
    }

    // The standalone "Attendance Events" page is gone: open attendance events and unlinked snapshots
    // now render on the Event System page, since snapshots only ever come from HNM activity and an
    // officer shouldn't have to leave the camp to review the roster it produced.
    //
    // This action survives as a redirect rather than being deleted because every POST in this
    // controller ends in `RedirectToAction(nameof(Index), new { linkshellId })` — two dozen of them.
    // Forwarding here keeps all of those landing on the merged page with no edit apiece, and keeps
    // any bookmark or external link working.
    [HttpGet("/linkshells/{linkshellId:int}/window-events")]
    public IActionResult Index(int linkshellId)
        => RedirectToAction("Index", "Event");

    // Searchable archive of CLOSED Window Events ("Attendance History").
    // Search matches the event name, the character who created it, any
    // snapshot poster, or any combined-roster member name.
    [HttpGet("/linkshells/{linkshellId:int}/window-events/history")]
    public async Task<IActionResult> History(
        int linkshellId, string? q, int page, CancellationToken cancellationToken)
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

        // Query, search and paging live in AttendanceSectionsBuilder so this page and the archive
        // block on /Event stay identical. Page size stays at 20 here — that block uses 10, since it
        // shares a page with the live board.
        var vm = await _attendanceSections.BuildClosedAsync(
            linkshellId,
            linkshell.LinkshellName,
            IsLeaderOrOfficer(membership),
            _timeZones.Resolve(user.TimeZone),
            q,
            page,
            pageSize: 20,
            cancellationToken);

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

    // Saves DKP + Entry Type on a Window Event that hasn't been posted yet
    // so an officer can stage values without immediately pushing rows. The
    // Post to DKP Sheet button still does both write-and-enqueue in one
    // step; this endpoint covers the "draft" case where the officer wants
    // to set things up before another officer hits Post.
    [HttpPost("/linkshells/{linkshellId:int}/window-events/{windowEventId:int}/save-details")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SaveDetails(
        int linkshellId,
        int windowEventId,
        [FromForm] double? dkpAmount,
        [FromForm] string? entryType,
        [FromForm(Name = "MemberDkp")] List<WindowEventMemberDkpInput>? memberDkp,
        CancellationToken cancellationToken)
    {
        if (await RequireOfficerAsync(linkshellId, cancellationToken) is { } reject) return reject;

        var windowEvent = await _db.WindowEvents
            .Include(e => e.MemberDkpOverrides)
            .FirstOrDefaultAsync(e => e.Id == windowEventId && e.LinkshellId == linkshellId, cancellationToken);
        if (windowEvent is null) return NotFound();

        if (windowEvent.PostedToSheetAt.HasValue)
        {
            TempData["WindowEventError"] = "This Window Event is already posted. Use Edit to change DKP or Entry Type.";
            return RedirectToAction(nameof(Index), new { linkshellId });
        }
        // The card no longer carries a Default DKP input (DKP is set per snapshot). Resolve keeps
        // the stored baseline — or seeds it — instead of rejecting the save, and never yields
        // null, so there is nothing left to reject here.
        var resolvedDkp = WindowEventDkp.Resolve(dkpAmount, windowEvent.DkpAmount);
        // The form no longer carries Entry Type — it's auto-tagged from the monster at creation.
        // Resolve keeps the stored value (or re-derives it) instead of rejecting the save, and
        // always yields a valid tag, so there is nothing left to reject here.
        entryType = WindowEventEntryTypes.Resolve(entryType, windowEvent.EntryType, windowEvent.Name);
        if (!TryValidateMemberDkp(memberDkp, out var memberDkpError))
        {
            TempData["WindowEventError"] = memberDkpError;
            return RedirectToAction(nameof(Index), new { linkshellId });
        }

        windowEvent.DkpAmount = resolvedDkp;
        windowEvent.EntryType = entryType;
        ApplyMemberDkpOverrides(windowEvent, resolvedDkp, memberDkp);
        await _db.SaveChangesAsync(cancellationToken);

        TempData["WindowEventStatus"] = $"Saved DKP details for \"{windowEvent.Name}\".";
        return RedirectToAction(nameof(Index), new { linkshellId });
    }

    // Edits a posted Window Event's DKP + Entry Type and queues a background
    // job to rewrite columns J/K on the appended rows and reconcile matching
    // ledger entries + per-member LinkshellDkp totals by the delta. The
    // sheet's other columns (date, character, jobs) stay untouched -- only
    // values that originate from the Window Event itself are mutable here.
    [HttpPost("/linkshells/{linkshellId:int}/window-events/{windowEventId:int}/edit-posted")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> EditPostedDetails(
        int linkshellId,
        int windowEventId,
        [FromForm] double? dkpAmount,
        [FromForm] string? entryType,
        [FromForm(Name = "MemberDkp")] List<WindowEventMemberDkpInput>? memberDkp,
        CancellationToken cancellationToken)
    {
        if (await RequireOfficerAsync(linkshellId, cancellationToken) is { } reject) return reject;

        var windowEvent = await _db.WindowEvents
            .Include(e => e.MemberDkpOverrides)
            .FirstOrDefaultAsync(e => e.Id == windowEventId && e.LinkshellId == linkshellId, cancellationToken);
        if (windowEvent is null) return NotFound();

        if (!windowEvent.PostedToSheetAt.HasValue)
        {
            TempData["WindowEventError"] = "This Window Event hasn't been posted yet. Use Save to set draft values.";
            return RedirectToAction(nameof(Index), new { linkshellId });
        }
        // The card no longer carries a Default DKP input (DKP is set per snapshot). Resolve keeps
        // the stored baseline — or seeds it — instead of rejecting the save, and never yields
        // null, so there is nothing left to reject here.
        var resolvedDkp = WindowEventDkp.Resolve(dkpAmount, windowEvent.DkpAmount);
        // The form no longer carries Entry Type — it's auto-tagged from the monster at creation.
        // Resolve keeps the stored value (or re-derives it) instead of rejecting the save, and
        // always yields a valid tag, so there is nothing left to reject here.
        entryType = WindowEventEntryTypes.Resolve(entryType, windowEvent.EntryType, windowEvent.Name);
        if (!TryValidateMemberDkp(memberDkp, out var memberDkpError))
        {
            TempData["WindowEventError"] = memberDkpError;
            return RedirectToAction(nameof(Index), new { linkshellId });
        }

        var amountChanged = !windowEvent.DkpAmount.HasValue || Math.Abs(windowEvent.DkpAmount.Value - resolvedDkp) > 0.0001;
        // Still meaningful with the input gone: Resolve echoes the stored tag back for an ordinary
        // save (so this is false), but a legacy row whose tag was null gets healed to a real value
        // here — which SHOULD count as a change, since that's exactly the row the ledger has been
        // silently refusing to credit.
        var typeChanged = !string.Equals(windowEvent.EntryType, entryType, StringComparison.Ordinal);
        var overrideChanged = HasMemberDkpChange(windowEvent, resolvedDkp, memberDkp);
        if (!amountChanged && !typeChanged && !overrideChanged)
        {
            TempData["WindowEventStatus"] = "No changes to apply.";
            return RedirectToAction(nameof(Index), new { linkshellId });
        }

        windowEvent.DkpAmount = resolvedDkp;
        windowEvent.EntryType = entryType;
        ApplyMemberDkpOverrides(windowEvent, resolvedDkp, memberDkp);
        await _db.SaveChangesAsync(cancellationToken);

        // Reconcile the already-credited ledger + per-member DKP by the delta.
        await _windowEventDkpLedger.ReconcilePostedWindowEventLedgerAsync(windowEvent.Id, cancellationToken);

        TempData["WindowEventStatus"] = $"Updated DKP for \"{windowEvent.Name}\".";
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
        [FromForm(Name = "MemberDkp")] List<WindowEventMemberDkpInput>? memberDkp,
        CancellationToken cancellationToken)
    {
        if (await RequireOfficerAsync(linkshellId, cancellationToken) is { } reject) return reject;

        var windowEvent = await _db.WindowEvents
            .Include(e => e.MemberDkpOverrides)
            .FirstOrDefaultAsync(e => e.Id == windowEventId && e.LinkshellId == linkshellId, cancellationToken);
        if (windowEvent is null) return NotFound();

        // The card no longer carries a Default DKP input (DKP is set per snapshot). Resolve keeps
        // the stored baseline — or seeds it — instead of rejecting the save, and never yields
        // null, so there is nothing left to reject here.
        var resolvedDkp = WindowEventDkp.Resolve(dkpAmount, windowEvent.DkpAmount);
        // The form no longer carries Entry Type — it's auto-tagged from the monster at creation.
        // Resolve keeps the stored value (or re-derives it) instead of rejecting the save, and
        // always yields a valid tag, so there is nothing left to reject here.
        entryType = WindowEventEntryTypes.Resolve(entryType, windowEvent.EntryType, windowEvent.Name);
        if (windowEvent.PostedToSheetAt.HasValue)
        {
            TempData["WindowEventError"] = "This Window Event has already been posted to the DKP sheet.";
            return RedirectToAction(nameof(Index), new { linkshellId });
        }
        if (!TryValidateMemberDkp(memberDkp, out var memberDkpError))
        {
            TempData["WindowEventError"] = memberDkpError;
            return RedirectToAction(nameof(Index), new { linkshellId });
        }

        windowEvent.DkpAmount = resolvedDkp;
        windowEvent.EntryType = entryType;
        ApplyMemberDkpOverrides(windowEvent, resolvedDkp, memberDkp);
        windowEvent.PostedToSheetAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(cancellationToken);

        // Publish = credit DKP. Materialize the per-member SnapshotEarned ledger
        // rows now that the event is marked posted (idempotent).
        await _windowEventDkpLedger.EnsurePostedWindowEventLedgerEntriesAsync(windowEvent.Id, cancellationToken);

        TempData["WindowEventStatus"] = $"Published \"{windowEvent.Name}\" and credited DKP.";
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

    // Hard-deletes a snapshot (and its entries). Used from the Unlinked
    // Snapshots list for junk/typo captures the officer doesn't want kept
    // even as "Ignored". Entries cascade via the required SnapshotId FK;
    // any sibling snapshot pointing here via DuplicateOfSnapshotId is
    // SetNull'd by the configured delete behavior. Rows already appended to
    // the sheet are not touched -- AttInput is append-only.
    [HttpPost("/linkshells/{linkshellId:int}/window-events/snapshots/{snapshotId:int}/delete")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteSnapshot(
        int linkshellId,
        int snapshotId,
        CancellationToken cancellationToken)
    {
        if (await RequireOfficerAsync(linkshellId, cancellationToken) is { } reject) return reject;

        var snapshot = await _db.AttendanceSnapshots
            .Include(s => s.Entries)
            .FirstOrDefaultAsync(s => s.Id == snapshotId && s.LinkshellId == linkshellId, cancellationToken);
        if (snapshot is null) return NotFound();

        if (snapshot.Entries.Count > 0)
        {
            _db.AttendanceSnapshotEntries.RemoveRange(snapshot.Entries);
        }
        _db.AttendanceSnapshots.Remove(snapshot);
        await _db.SaveChangesAsync(cancellationToken);

        TempData["WindowEventStatus"] = "Snapshot deleted.";
        return RedirectToAction(nameof(Index), new { linkshellId });
    }

    // Removes a single person from a snapshot (officer correction of a bad
    // capture). The denormalized EntryCount is kept in sync; the combined
    // roster + counts are always recomputed on the next Index load. Rows
    // already appended to the DKP sheet are not touched -- AttInput is
    // append-only, so the officer re-runs Update on DKP sheet if needed.
    [HttpPost("/linkshells/{linkshellId:int}/window-events/snapshots/{snapshotId:int}/entries/{entryId:int}/delete")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteSnapshotEntry(
        int linkshellId,
        int snapshotId,
        int entryId,
        CancellationToken cancellationToken)
    {
        if (await RequireOfficerAsync(linkshellId, cancellationToken) is { } reject) return reject;

        var snapshot = await _db.AttendanceSnapshots
            .Include(s => s.Entries)
            .FirstOrDefaultAsync(s => s.Id == snapshotId && s.LinkshellId == linkshellId, cancellationToken);
        if (snapshot is null) return NotFound();

        var entry = snapshot.Entries.FirstOrDefault(e => e.Id == entryId);
        if (entry is null) return NotFound();

        _db.AttendanceSnapshotEntries.Remove(entry);
        snapshot.Entries.Remove(entry);
        snapshot.EntryCount = snapshot.Entries.Count;
        await _db.SaveChangesAsync(cancellationToken);

        TempData["WindowEventStatus"] = $"Removed {entry.CharacterName} from the snapshot.";
        return RedirectToAction(nameof(Index), new { linkshellId });
    }

    // Adds a person to a snapshot the addon missed. Mirrors the addon
    // snapshot's 18-member alliance cap and de-dupes by character name.
    [HttpPost("/linkshells/{linkshellId:int}/window-events/snapshots/{snapshotId:int}/entries/add")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> AddSnapshotEntry(
        int linkshellId,
        int snapshotId,
        [FromForm] string? characterName,
        [FromForm] string? mainJob,
        [FromForm] int? mainJobLevel,
        [FromForm] string? subJob,
        [FromForm] int? subJobLevel,
        [FromForm] string? zone,
        CancellationToken cancellationToken)
    {
        if (await RequireOfficerAsync(linkshellId, cancellationToken) is { } reject) return reject;

        var snapshot = await _db.AttendanceSnapshots
            .Include(s => s.Entries)
            .FirstOrDefaultAsync(s => s.Id == snapshotId && s.LinkshellId == linkshellId, cancellationToken);
        if (snapshot is null) return NotFound();

        var name = characterName?.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            TempData["WindowEventStatus"] = "Character name is required to add a person.";
            return RedirectToAction(nameof(Index), new { linkshellId });
        }

        // FFXI alliance caps at 18; match the addon snapshot invariant.
        if (snapshot.Entries.Count >= 18)
        {
            TempData["WindowEventStatus"] = "Snapshot already has the 18-member alliance maximum.";
            return RedirectToAction(nameof(Index), new { linkshellId });
        }

        if (snapshot.Entries.Any(e =>
                string.Equals(e.CharacterName.Trim(), name, StringComparison.OrdinalIgnoreCase)))
        {
            TempData["WindowEventStatus"] = $"{name} is already in this snapshot.";
            return RedirectToAction(nameof(Index), new { linkshellId });
        }

        snapshot.Entries.Add(new AttendanceSnapshotEntry
        {
            SnapshotId = snapshot.Id,
            CharacterName = Clip(name, 256)!,
            MainJob = Clip(string.IsNullOrWhiteSpace(mainJob) ? null : mainJob.Trim(), 8),
            MainJobLevel = mainJobLevel,
            SubJob = Clip(string.IsNullOrWhiteSpace(subJob) ? null : subJob.Trim(), 8),
            SubJobLevel = subJobLevel,
            Zone = Clip(string.IsNullOrWhiteSpace(zone) ? null : zone.Trim(), 128),
            AddedManually = true, // typed in by an officer, not scanned — sorts last and renders tinted
        });
        snapshot.EntryCount = snapshot.Entries.Count;
        await _db.SaveChangesAsync(cancellationToken);

        TempData["WindowEventStatus"] = $"Added {name} to the snapshot.";
        return RedirectToAction(nameof(Index), new { linkshellId });
    }

    private static string? Clip(string? value, int max)
        => string.IsNullOrEmpty(value) || value.Length <= max ? value : value[..max];

    private async Task<WindowEvent> FindOrCreateOpenEventAsync(
        int linkshellId,
        string name,
        DateTime capturedAtUtc,
        string? capturedByCharacterName,
        DateTime nowUtc,
        CancellationToken cancellationToken)
    {
        var normalized = NormalizeName(name)!;
        var staleCutoff = capturedAtUtc.AddHours(-21);
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
            // Pre-select the camp from the monster name so officers don't
            // have to set it manually on every newly created event.
            EntryType = WindowEventEntryTypes.FromMonsterName(name),
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

        var fromUtc = snapshot.CapturedAtUtc.AddMinutes(-8);
        var toUtc = snapshot.CapturedAtUtc.AddMinutes(8);
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

    // Row mapping moved to AttendanceSectionsBuilder when the open-events sections were folded into
    // the Event System page — EventController needs the identical shape. These stay as forwarders so
    // the call sites throughout this controller read the same as they always did.
    private static WindowEventRow MapWindowEvent(WindowEvent item, DateTimeZone userZone)
        => AttendanceSectionsBuilder.MapWindowEvent(item, userZone);

    private static WindowSnapshotRow MapSnapshot(AttendanceSnapshot snapshot, DateTimeZone userZone)
        => AttendanceSectionsBuilder.MapSnapshot(snapshot, userZone);

    private static List<WindowCombinedMemberRow> BuildCombinedMembers(
        IEnumerable<AttendanceSnapshot> snapshots,
        IDictionary<string, double>? memberDkpOverrides = null,
        double? defaultDkpAmount = null)
        => AttendanceSectionsBuilder.BuildCombinedMembers(snapshots, memberDkpOverrides, defaultDkpAmount);

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

    // Rejects negative or non-numeric per-character DKP inputs before they
    // hit the database. Blank values are allowed -- they mean "fall back to
    // the event default" and result in the override row being removed below.
    private static bool TryValidateMemberDkp(
        IEnumerable<WindowEventMemberDkpInput>? inputs,
        out string error)
    {
        if (inputs is not null)
        {
            foreach (var input in inputs)
            {
                if (input.DkpAmount.HasValue && input.DkpAmount.Value < 0)
                {
                    error = $"Per-character DKP for \"{input.CharacterName}\" must be zero or greater.";
                    return false;
                }
            }
        }
        error = string.Empty;
        return true;
    }

    // Reconciles MemberDkpOverrides with the form payload:
    //   * Any character with a value equal to the event default has its
    //     override row removed (no point keeping a noisy duplicate).
    //   * Any character with a different non-null value gets its row
    //     upserted.
    // Characters absent from the form are left alone -- the view always
    // submits every row so the only way a row vanishes is for the user to
    // clear the value, which sends a null and triggers the remove branch.
    private static void ApplyMemberDkpOverrides(
        WindowEvent windowEvent,
        double defaultDkpAmount,
        IEnumerable<WindowEventMemberDkpInput>? inputs)
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

    private static bool HasMemberDkpChange(
        WindowEvent windowEvent,
        double defaultDkpAmount,
        IEnumerable<WindowEventMemberDkpInput>? inputs)
    {
        if (inputs is null) return false;
        var existingByName = windowEvent.MemberDkpOverrides
            .Where(o => !string.IsNullOrWhiteSpace(o.CharacterName))
            .GroupBy(o => o.CharacterName.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().DkpAmount, StringComparer.OrdinalIgnoreCase);

        foreach (var input in inputs)
        {
            var name = input.CharacterName?.Trim();
            if (string.IsNullOrWhiteSpace(name)) continue;
            var hadOverride = existingByName.TryGetValue(name, out var existingAmount);
            var willHaveOverride = input.DkpAmount.HasValue &&
                                   Math.Abs(input.DkpAmount.Value - defaultDkpAmount) >= 0.0001;
            if (hadOverride != willHaveOverride) return true;
            if (willHaveOverride && Math.Abs(existingAmount - input.DkpAmount!.Value) > 0.0001) return true;
        }
        return false;
    }

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
        => AttendanceSectionsBuilder.FormatPretty(utc, zone);
}
