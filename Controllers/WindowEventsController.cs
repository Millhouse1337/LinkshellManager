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
    private readonly AdminOverrideService _adminOverride;
    private readonly TimeZoneConversionService _timeZones;
    private readonly WindowEventDkpLedgerService _windowEventDkpLedger;
    private readonly AttendanceSectionsBuilder _attendanceSections;
    private readonly WindowEventLinkService _windowEventLinks;

    public WindowEventsController(
        ApplicationDbContext db,
        UserManager<AppUser> userManager,
        AdminOverrideService adminOverride,
        TimeZoneConversionService timeZones,
        WindowEventDkpLedgerService windowEventDkpLedger,
        AttendanceSectionsBuilder attendanceSections,
        WindowEventLinkService windowEventLinks)
    {
        _db = db;
        _userManager = userManager;
        _adminOverride = adminOverride;
        _timeZones = timeZones;
        _windowEventDkpLedger = windowEventDkpLedger;
        _attendanceSections = attendanceSections;
        _windowEventLinks = windowEventLinks;
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
            await CanManageAsync(membership, cancellationToken),
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
        // Null means "misc pays what a window pays", which is the default.
        [FromForm] double? miscDkpAmount,
        [FromForm] string? entryType,
        [FromForm(Name = "MemberDkp")] List<WindowEventMemberDkpInput>? memberDkp,
        CancellationToken cancellationToken)
    {
        if (await RequireOfficerAsync(linkshellId, cancellationToken) is { } reject) return reject;

        var windowEvent = await _db.WindowEvents
            .Include(e => e.MemberDkpOverrides)
            // Needed to tell who is misc-ONLY. Nothing here loaded snapshots before: the misc rate
            // is the first thing on this path that depends on how a capture was filed.
            .Include(e => e.Snapshots).ThenInclude(s => s.Entries)
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
        // Resolved BEFORE the assignment below, or the stored-value fallback would read the
        // value we are about to overwrite and a save that carries no misc field would reset it.
        var resolvedMisc = WindowEventDkp.ResolveMisc(miscDkpAmount, windowEvent.MiscDkpAmount, resolvedDkp);
        windowEvent.MiscDkpAmount = miscDkpAmount;
        WindowEventMiscDkp.ApplyMiscOverrides(
            windowEvent, resolvedDkp, resolvedMisc, WindowEventMiscDkp.SubmittedNames(memberDkp));
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
        // Null means "misc pays what a window pays", which is the default.
        [FromForm] double? miscDkpAmount,
        [FromForm] string? entryType,
        [FromForm(Name = "MemberDkp")] List<WindowEventMemberDkpInput>? memberDkp,
        CancellationToken cancellationToken)
    {
        if (await RequireOfficerAsync(linkshellId, cancellationToken) is { } reject) return reject;

        var windowEvent = await _db.WindowEvents
            .Include(e => e.MemberDkpOverrides)
            // Needed to tell who is misc-ONLY. Nothing here loaded snapshots before: the misc rate
            // is the first thing on this path that depends on how a capture was filed.
            .Include(e => e.Snapshots).ThenInclude(s => s.Entries)
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
        // A misc-rate change moves only misc-only members, so it shows up in neither the default
        // amount nor in a per-member override the form already carried. Without this term an edit
        // that only retunes Misc DKP would report "No changes to apply" and quietly do nothing.
        var miscChanged = windowEvent.MiscDkpAmount.HasValue != miscDkpAmount.HasValue
            || (windowEvent.MiscDkpAmount.HasValue && miscDkpAmount.HasValue
                && Math.Abs(windowEvent.MiscDkpAmount.Value - miscDkpAmount.Value) > 0.0001);
        if (!amountChanged && !typeChanged && !overrideChanged && !miscChanged)
        {
            TempData["WindowEventStatus"] = "No changes to apply.";
            return RedirectToAction(nameof(Index), new { linkshellId });
        }

        windowEvent.DkpAmount = resolvedDkp;
        windowEvent.EntryType = entryType;
        ApplyMemberDkpOverrides(windowEvent, resolvedDkp, memberDkp);
        // Resolved BEFORE the assignment below, or the stored-value fallback would read the
        // value we are about to overwrite and a save that carries no misc field would reset it.
        var resolvedMisc = WindowEventDkp.ResolveMisc(miscDkpAmount, windowEvent.MiscDkpAmount, resolvedDkp);
        windowEvent.MiscDkpAmount = miscDkpAmount;
        WindowEventMiscDkp.ApplyMiscOverrides(
            windowEvent, resolvedDkp, resolvedMisc, WindowEventMiscDkp.SubmittedNames(memberDkp));
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
        // Null means "misc pays what a window pays", which is the default.
        [FromForm] double? miscDkpAmount,
        [FromForm] string? entryType,
        [FromForm(Name = "MemberDkp")] List<WindowEventMemberDkpInput>? memberDkp,
        CancellationToken cancellationToken)
    {
        if (await RequireOfficerAsync(linkshellId, cancellationToken) is { } reject) return reject;

        var windowEvent = await _db.WindowEvents
            .Include(e => e.MemberDkpOverrides)
            // Needed to tell who is misc-ONLY. Nothing here loaded snapshots before: the misc rate
            // is the first thing on this path that depends on how a capture was filed.
            .Include(e => e.Snapshots).ThenInclude(s => s.Entries)
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
        // Unverified captures are excluded from the combined roster, so posting with any still
        // outstanding would pay a roster that is visibly missing people — and posting is one-way.
        // Force the decision first; rejecting is one click if the capture is junk.
        var pendingCount = await _windowEventLinks.CountPendingSnapshotsAsync(windowEvent.Id, cancellationToken);
        if (pendingCount > 0)
        {
            TempData["WindowEventError"] =
                $"{pendingCount} snapshot{(pendingCount == 1 ? " is" : "s are")} awaiting verification. "
                + "Confirm or reject them before posting — their members are not in the roster below yet.";
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
        // Resolved BEFORE the assignment below, or the stored-value fallback would read the
        // value we are about to overwrite and a save that carries no misc field would reset it.
        var resolvedMisc = WindowEventDkp.ResolveMisc(miscDkpAmount, windowEvent.MiscDkpAmount, resolvedDkp);
        windowEvent.MiscDkpAmount = miscDkpAmount;
        WindowEventMiscDkp.ApplyMiscOverrides(
            windowEvent, resolvedDkp, resolvedMisc, WindowEventMiscDkp.SubmittedNames(memberDkp));
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

    // Files an unlinked snapshot under an attendance event: an existing one when `windowEventId`
    // is set ("Link to Event"), otherwise one found-or-created from `name`.
    //
    // `createNew` forces a brand-new event rather than folding into an open one of the same name —
    // what the "Make a New Event from this Snapshot" button means, as opposed to the dropdown's
    // link. Without it a repeat camp would silently swallow today's snapshot into yesterday's row.
    [HttpPost("/linkshells/{linkshellId:int}/window-events/snapshots/{snapshotId:int}/attach")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> AttachSnapshot(
        int linkshellId,
        int snapshotId,
        [FromForm] int? windowEventId,
        [FromForm] string? name,
        [FromForm] bool createNew,
        // The slot the officer picked: a numbered window, or Misc. Ingest no longer derives
        // either, so this is the only place a capture is ever classified.
        [FromForm] string? slotKind,
        [FromForm] int? windowNumber,
        [FromServices] AllianceIdentityService allianceIdentity,
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
            // Falls back to the snapshot's own name so "Make a New Event" works straight off a
            // capture the addon already named, with no retyping.
            var trimmed = TrimToNull(name, 128) ?? TrimToNull(snapshot.Name, 128);
            if (trimmed is null)
            {
                TempData["WindowEventError"] = "Choose an existing attendance event, or name this snapshot first.";
                return RedirectToAction(nameof(Index), new { linkshellId });
            }
            windowEvent = await _windowEventLinks.FindOrCreateAsync(
                linkshellId,
                trimmed,
                snapshot.CapturedAtUtc,
                snapshot.CapturedByCharacterName,
                DateTime.UtcNow,
                cancellationToken,
                forceNew: createNew);
            snapshot.Name ??= trimmed;
        }

        if (windowEvent is null) return NotFound();

        WindowEventLinkService.ApplySlot(snapshot, windowEvent, slotKind, windowNumber);

        // The alliance NUMBER is assigned here, not at ingest: it is an ordinal within THIS camp,
        // and until a capture is filed there is no camp to be first, second or third on.
        snapshot.AllianceNumber = await allianceIdentity.ResolveNumberAsync(
            windowEvent.Id, snapshot.AllianceKey, cancellationToken);

        // Note what is NOT here: the snapshot's status. Filing a capture and vouching for it are
        // separate decisions, and this used to force Active — which would have verified a Pending
        // capture the instant an officer sorted it into the right camp.
        _windowEventLinks.Attach(snapshot, windowEvent);

        await _db.SaveChangesAsync(cancellationToken);

        // Sheet sync is officer-initiated via the Post to DKP Sheet button on
        // the Window Event card -- attaching a snapshot no longer auto-pushes
        // rows so the user has a chance to fill in DKP + Entry Type first.
        TempData["WindowEventStatus"] =
            $"Snapshot linked to \"{windowEvent.Name}\" as {DescribeSlot(snapshot)}.";
        return RedirectToAction(nameof(Index), new { linkshellId });
    }

    // Renames the SNAPSHOT itself, and nothing else. Separate from AttachSnapshot's name field,
    // which conflated naming a capture with creating an event to file it under — an officer
    // labelling "Fafnir pop 2" for their own sake would find a camp had appeared.
    [HttpPost("/linkshells/{linkshellId:int}/window-events/snapshots/{snapshotId:int}/rename")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> RenameSnapshot(
        int linkshellId,
        int snapshotId,
        [FromForm] string? name,
        CancellationToken cancellationToken)
    {
        if (await RequireOfficerAsync(linkshellId, cancellationToken) is { } reject) return reject;

        var snapshot = await _db.AttendanceSnapshots
            .FirstOrDefaultAsync(s => s.Id == snapshotId && s.LinkshellId == linkshellId, cancellationToken);
        if (snapshot is null) return NotFound();

        snapshot.Name = TrimToNull(name, 128);
        await _db.SaveChangesAsync(cancellationToken);

        TempData["WindowEventStatus"] = snapshot.Name is null
            ? "Snapshot name cleared."
            : $"Snapshot renamed to \"{snapshot.Name}\".";
        return RedirectToAction(nameof(Index), new { linkshellId });
    }

    // Corrects the alliance a poster claimed. The number cannot be detected in game — the client
    // only ever sees your own alliance — so it is typed by a member under pressure at a pop, and
    // getting it wrong collapses two alliances into one row on the card.
    [HttpPost("/linkshells/{linkshellId:int}/window-events/snapshots/{snapshotId:int}/alliance")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SetSnapshotAlliance(
        int linkshellId,
        int snapshotId,
        [FromForm] int allianceNumber,
        CancellationToken cancellationToken)
    {
        if (await RequireOfficerAsync(linkshellId, cancellationToken) is { } reject) return reject;

        var snapshot = await _db.AttendanceSnapshots
            .FirstOrDefaultAsync(s => s.Id == snapshotId && s.LinkshellId == linkshellId, cancellationToken);
        if (snapshot is null) return NotFound();

        snapshot.AllianceNumber = AttendanceSnapshotAlliances.Resolve(allianceNumber);
        await _db.SaveChangesAsync(cancellationToken);

        TempData["WindowEventStatus"] =
            $"Snapshot moved to {AttendanceSnapshotAlliances.Label(snapshot.AllianceNumber, snapshot.AllianceKey, snapshot.AllianceLeaderName)}.";
        return RedirectToAction(nameof(Index), new { linkshellId });
    }
    // Moves a capture between a numbered window and Misc, in place.
    //
    // Filing is entirely manual now — ingest classifies nothing — so mis-filing is not an edge
    // case, it is the expected cost of the trade. Detach-and-refile would work but throws away the
    // link and the officer has to find the capture again in the triage queue.
    [HttpPost("/linkshells/{linkshellId:int}/window-events/snapshots/{snapshotId:int}/slot")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SetSnapshotSlot(
        int linkshellId,
        int snapshotId,
        [FromForm] string? slotKind,
        [FromForm] int? windowNumber,
        CancellationToken cancellationToken)
    {
        if (await RequireOfficerAsync(linkshellId, cancellationToken) is { } reject) return reject;

        var snapshot = await _db.AttendanceSnapshots
            .Include(s => s.WindowEvent)
            .FirstOrDefaultAsync(s => s.Id == snapshotId && s.LinkshellId == linkshellId, cancellationToken);
        if (snapshot is null) return NotFound();

        if (snapshot.WindowEvent is null)
        {
            TempData["WindowEventError"] = "File this capture to an event before choosing a slot.";
            return RedirectToAction(nameof(Index), new { linkshellId });
        }

        // Re-slotting a POSTED event would move members between the window rate and the misc rate
        // after the DKP has already been paid, and this action does not reconcile the ledger. Edit
        // the posted event instead, which does.
        if (snapshot.WindowEvent.PostedToSheetAt.HasValue)
        {
            TempData["WindowEventError"] =
                "This event is already posted. Use Edit on the event to change DKP after a re-slot.";
            return RedirectToAction(nameof(Index), new { linkshellId });
        }

        WindowEventLinkService.ApplySlot(snapshot, snapshot.WindowEvent, slotKind, windowNumber);
        await _db.SaveChangesAsync(cancellationToken);

        TempData["WindowEventStatus"] = $"Snapshot moved to {DescribeSlot(snapshot)}.";
        return RedirectToAction(nameof(Index), new { linkshellId });
    }

    // How a slot reads back to the officer who just chose it. "Misc" rather than a window number,
    // and a bare "the camp roster" on an ungridded camp where there is no number to name.
    private static string DescribeSlot(AttendanceSnapshot snapshot)
        => AttendanceSnapshotSlotKinds.IsMisc(snapshot.SlotKind)
            ? "Miscellaneous"
            : snapshot.WindowNumber is int window ? $"Window {window}" : "the camp roster";

    // Confirms a member-posted capture: Pending -> Active, which is what puts its members into the
    // combined roster and therefore into the payout. Deliberately its own action rather than a
    // value on SetSnapshotStatus, because it is the one status change that has to record WHO.
    [HttpPost("/linkshells/{linkshellId:int}/window-events/snapshots/{snapshotId:int}/verify")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> VerifySnapshot(
        int linkshellId,
        int snapshotId,
        CancellationToken cancellationToken)
    {
        var user = await _userManager.GetUserAsync(User);
        if (user is null) return Challenge();
        if (await RequireOfficerAsync(linkshellId, cancellationToken) is { } reject) return reject;

        var snapshot = await _db.AttendanceSnapshots
            .FirstOrDefaultAsync(s => s.Id == snapshotId && s.LinkshellId == linkshellId, cancellationToken);
        if (snapshot is null) return NotFound();

        snapshot.SnapshotStatus = AttendanceSnapshotStatuses.Active;
        snapshot.VerifiedAtUtc = DateTime.UtcNow;
        snapshot.VerifiedByAppUserId = user.Id;
        await _db.SaveChangesAsync(cancellationToken);

        TempData["WindowEventStatus"] =
            $"Confirmed the {AttendanceSnapshotAlliances.Label(snapshot.AllianceNumber, snapshot.AllianceKey, snapshot.AllianceLeaderName)} capture posted by "
            + $"{snapshot.CapturedByCharacterName ?? "an unknown character"}.";
        return RedirectToAction(nameof(Index), new { linkshellId });
    }

    // Rejects a capture. Lands on Ignored rather than getting a status of its own: every query that
    // has to exclude a rejected snapshot — the combined roster, the DKP ledger, the unlinked list,
    // the merge-target search — already excludes Ignored, so a sixth status would have meant six
    // new filters and one of them eventually being missed. The verifier stamp is what records that
    // a person looked at this and said no, rather than it being junk nobody ever triaged.
    [HttpPost("/linkshells/{linkshellId:int}/window-events/snapshots/{snapshotId:int}/reject")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> RejectSnapshot(
        int linkshellId,
        int snapshotId,
        CancellationToken cancellationToken)
    {
        var user = await _userManager.GetUserAsync(User);
        if (user is null) return Challenge();
        if (await RequireOfficerAsync(linkshellId, cancellationToken) is { } reject) return reject;

        var snapshot = await _db.AttendanceSnapshots
            .FirstOrDefaultAsync(s => s.Id == snapshotId && s.LinkshellId == linkshellId, cancellationToken);
        if (snapshot is null) return NotFound();

        snapshot.SnapshotStatus = AttendanceSnapshotStatuses.Ignored;
        snapshot.VerifiedAtUtc = DateTime.UtcNow;
        snapshot.VerifiedByAppUserId = user.Id;
        await _db.SaveChangesAsync(cancellationToken);

        TempData["WindowEventStatus"] = "Snapshot rejected.";
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
            AttendanceSnapshotStatuses.Ignored => AttendanceSnapshotStatuses.Ignored,
            // Accepted so an officer can send a capture BACK for review after promoting it by
            // mistake. Confirming still goes through VerifySnapshot, which is the only path that
            // records who vouched for it.
            AttendanceSnapshotStatuses.Pending => AttendanceSnapshotStatuses.Pending,
            _ => null
        };
        if (normalized is null) return BadRequest("Unsupported snapshot status.");

        var snapshot = await _db.AttendanceSnapshots
            .FirstOrDefaultAsync(s => s.Id == snapshotId && s.LinkshellId == linkshellId, cancellationToken);
        if (snapshot is null) return NotFound();

        snapshot.SnapshotStatus = normalized;
        if (normalized == AttendanceSnapshotStatuses.Active || normalized == AttendanceSnapshotStatuses.Ignored)
        {
        }
        await _db.SaveChangesAsync(cancellationToken);

        // Sheet sync is officer-initiated via Post to DKP Sheet on the parent
        // Window Event card. Flipping a snapshot's status no longer pushes
        // rows directly so the officer controls when the AttInput append fires.
        return RedirectToAction(nameof(Index), new { linkshellId });
    }

    // Hard-deletes a snapshot (and its entries). Used from the Unlinked
    // Snapshots list for junk/typo captures the officer doesn't want kept
    // even as "Ignored". Entries cascade via the required SnapshotId FK. Rows
    // already appended to the sheet are not touched -- AttInput is append-only.
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

        // An FFXI alliance is 18 people, and a snapshot is exactly one alliance — the same ceiling
        // the addon ingest enforces, for the same reason.
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
        return await CanManageAsync(membership, cancellationToken) ? null : Forbid();
    }

    // Leader/Officer by rank, OR the app-wide admin override. The membership row must
    // already be non-null when this is called — the override never reaches a linkshell
    // the user has not joined. See AdminOverrideService.
    private async Task<bool> CanManageAsync(AppUserLinkshell membership, CancellationToken cancellationToken)
        => IsLeaderOrOfficer(membership)
           || await _adminOverride.IsActiveForAsync(membership.AppUserId, cancellationToken);

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

    private static string FormatPretty(DateTime utc, DateTimeZone zone)
        => AttendanceSectionsBuilder.FormatPretty(utc, zone);
}
