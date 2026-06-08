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

    private readonly ApplicationDbContext _db;
    private readonly UserManager<AppUser> _userManager;
    private readonly TimeZoneConversionService _timeZones;
    private readonly WindowEventDkpLedgerService _windowEventDkpLedger;

    public WindowEventsController(
        ApplicationDbContext db,
        UserManager<AppUser> userManager,
        TimeZoneConversionService timeZones,
        WindowEventDkpLedgerService windowEventDkpLedger)
    {
        _db = db;
        _userManager = userManager;
        _timeZones = timeZones;
        _windowEventDkpLedger = windowEventDkpLedger;
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
            .Include(e => e.MemberDkpOverrides)
            .ToListAsync(cancellationToken);

        // Closed events live on the dedicated, searchable Attendance History
        // page (History action) so this page stays focused on what's open.
        var unlinked = await _db.AttendanceSnapshots
            .AsNoTracking()
            .Where(s => s.LinkshellId == linkshellId && s.WindowEventId == null && s.SnapshotStatus != AttendanceSnapshotStatuses.Ignored)
            .OrderByDescending(s => s.CapturedAtUtc)
            .Take(MaxUnlinkedSnapshots)
            .Include(s => s.Entries)
            .ToListAsync(cancellationToken);

        // Linkshell roster character names, for the "Add a character by name…"
        // typeahead on the snapshot editor. Only fetched for managers (the
        // only ones who can edit a snapshot's roster).
        var rosterNames = canManage
            ? await _db.AppUserLinkshells
                .AsNoTracking()
                .Where(link => link.LinkshellId == linkshellId
                               && link.CharacterName != null
                               && link.CharacterName != "")
                .Select(link => link.CharacterName!)
                .Distinct()
                .OrderBy(name => name)
                .ToListAsync(cancellationToken)
            : new List<string>();

        var vm = new WindowEventsViewModel
        {
            LinkshellId = linkshellId,
            LinkshellName = linkshell.LinkshellName,
            CanManage = canManage,
            OpenEvents = openEvents.Select(e => MapWindowEvent(e, zone)).ToList(),
            ClosedEvents = new(),
            UnlinkedSnapshots = unlinked.Select(s => MapSnapshot(s, zone)).ToList(),
            RosterCharacterNames = rosterNames,
        };

        return View(vm);
    }

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

        var zone = _timeZones.Resolve(user.TimeZone);
        var query = string.IsNullOrWhiteSpace(q) ? null : q.Trim();

        var closed = await _db.WindowEvents
            .AsNoTracking()
            .Where(e => e.LinkshellId == linkshellId && e.Status == WindowEventStatuses.Closed)
            .OrderByDescending(e => e.LastCapturedAtUtc)
            .Include(e => e.Snapshots).ThenInclude(s => s.Entries)
            .Include(e => e.MemberDkpOverrides)
            .ToListAsync(cancellationToken);

        var rows = closed.Select(e => MapWindowEvent(e, zone)).ToList();
        if (query is not null)
        {
            rows = rows.Where(r =>
                (r.Name?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false)
                || (r.CreatedByCharacterName?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false)
                || r.Snapshots.Any(s => s.CapturedByCharacterName?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false)
                || r.CombinedMembers.Any(m => m.CharacterName.Contains(query, StringComparison.OrdinalIgnoreCase)))
                .ToList();
        }

        const int pageSize = 20;
        var totalCount = rows.Count;
        var pageNumber = Math.Clamp(page <= 0 ? 1 : page, 1, Math.Max(1, (int)Math.Ceiling(totalCount / (double)pageSize)));

        var vm = new WindowEventsHistoryViewModel
        {
            LinkshellId = linkshellId,
            LinkshellName = linkshell.LinkshellName,
            CanManage = IsLeaderOrOfficer(membership),
            Query = query,
            Page = pageNumber,
            PageSize = pageSize,
            TotalCount = totalCount,
            Events = rows.Skip((pageNumber - 1) * pageSize).Take(pageSize).ToList(),
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
        if (!TryValidateMemberDkp(memberDkp, out var memberDkpError))
        {
            TempData["WindowEventError"] = memberDkpError;
            return RedirectToAction(nameof(Index), new { linkshellId });
        }

        windowEvent.DkpAmount = dkpAmount.Value;
        windowEvent.EntryType = entryType;
        ApplyMemberDkpOverrides(windowEvent, dkpAmount.Value, memberDkp);
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
        if (!TryValidateMemberDkp(memberDkp, out var memberDkpError))
        {
            TempData["WindowEventError"] = memberDkpError;
            return RedirectToAction(nameof(Index), new { linkshellId });
        }

        var amountChanged = !windowEvent.DkpAmount.HasValue || Math.Abs(windowEvent.DkpAmount.Value - dkpAmount.Value) > 0.0001;
        var typeChanged = !string.Equals(windowEvent.EntryType, entryType, StringComparison.Ordinal);
        var overrideChanged = HasMemberDkpChange(windowEvent, dkpAmount.Value, memberDkp);
        if (!amountChanged && !typeChanged && !overrideChanged)
        {
            TempData["WindowEventStatus"] = "No changes to apply.";
            return RedirectToAction(nameof(Index), new { linkshellId });
        }

        windowEvent.DkpAmount = dkpAmount.Value;
        windowEvent.EntryType = entryType;
        ApplyMemberDkpOverrides(windowEvent, dkpAmount.Value, memberDkp);
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
        if (!TryValidateMemberDkp(memberDkp, out var memberDkpError))
        {
            TempData["WindowEventError"] = memberDkpError;
            return RedirectToAction(nameof(Index), new { linkshellId });
        }

        windowEvent.DkpAmount = dkpAmount.Value;
        windowEvent.EntryType = entryType;
        ApplyMemberDkpOverrides(windowEvent, dkpAmount.Value, memberDkp);
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

    private WindowEventRow MapWindowEvent(WindowEvent item, DateTimeZone userZone)
    {
        var snapshots = item.Snapshots
            .OrderByDescending(s => s.CapturedAtUtc)
            .Select(s => MapSnapshot(s, userZone))
            .ToList();
        var overrides = item.MemberDkpOverrides
            .Where(o => !string.IsNullOrWhiteSpace(o.CharacterName))
            .GroupBy(o => o.CharacterName.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().DkpAmount, StringComparer.OrdinalIgnoreCase);
        var combined = BuildCombinedMembers(item.Snapshots, overrides, item.DkpAmount);

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
                Id = e.Id,
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

    private static List<WindowCombinedMemberRow> BuildCombinedMembers(
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
                return new WindowCombinedMemberRow
                {
                    CharacterName = g.Key,
                    MainJob = latest.MainJob,
                    MainJobLevel = latest.MainJobLevel,
                    SubJob = latest.SubJob,
                    SubJobLevel = latest.SubJobLevel,
                    Zone = latest.Zone,
                    SnapshotCount = g.Select(x => x.Snapshot.Id).Distinct().Count(),
                    DkpAmountOverride = overrideAmount,
                    EffectiveDkpAmount = overrideAmount ?? defaultDkpAmount,
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
