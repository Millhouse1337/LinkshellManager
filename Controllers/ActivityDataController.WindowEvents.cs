using LinkshellManagerDiscordApp.Data;
using LinkshellManagerDiscordApp.Models;
using LinkshellManagerDiscordApp.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace LinkshellManagerDiscordApp.Controllers;

public sealed partial class ActivityDataController
{
    // ClosedEvents is ONE PAGE of the Attendance Archive, not the whole history — the four Closed*
    // fields describe which page, so the Activity can render the tally and pager.
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
        int UnlinkedDisplayCap,
        // The linkshell's Misc post rate: what an unlinked post pays when it has no price of its
        // own, and what the Unlinked Snapshots row's DKP box starts at.
        double? MiscPostRate = null,
        // Posted cards that still owe someone: a member who was not registered when the card was
        // posted and has registered since. Each carries UnpaidRegisteredMemberCount, and the card
        // offers "Pay newly registered". Officers only; empty for everyone else.
        IReadOnlyList<ActivityWindowEventDto>? PostedOwingEvents = null);

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
        // The camp board this row was handed off from, while that board still exists. Null once
        // it is gone -- a camp that does not repeat has its board removed at End Event -- so it
        // is NOT what says "this is a camp"; IsCamp is.
        int? SourceEventId,
        // The captures filed as Misc, and how many. Split server-side so the two clients cannot
        // disagree about what counts as Misc.
        int MiscSnapshotCount,
        // Per-member DKP for anyone credited ONLY by Misc posts: the event's own, else the
        // linkshell's Misc post rate. Null only on a card that prices its captures.
        double? MiscDkpAmount,
        // This camp own window grid, for the slot picker. HasWindowGrid false means there are no
        // numbers to offer (Sky gods, farm NMs); Misc is still selectable.
        int WindowCount,
        bool HasWindowGrid,
        // The CAPTURES carry the money on this card: each one shows what that window pays and is
        // edited there, and a member's combined amount is the sum of them. Set on camps handed off
        // from a Standard HNM board — see WindowEvent.PerCaptureDkp.
        bool PerCaptureDkp,
        // True when this row came from ending an HNM camp rather than an addon "/lsm now" capture.
        // Drives the "Camp" tag. Read off CampEndedAtUtc, which only End Camp writes and which
        // outlives the camp's board -- SourceEventId went null with it and took the tag along.
        bool IsCamp = false,
        // How many of CombinedMemberCount are NOT on the roster. Counted rather than subtracted:
        // every name stays listed, the unregistered ones are marked, and the officer is told how
        // many of the people they are about to Post for cannot be paid.
        int UnregisteredMemberCount = 0,
        // On a POSTED card: how many accounts on it are registered now but have not been paid from
        // it (they registered after Post). What "Pay newly registered" would pay. 0 elsewhere.
        int UnpaidRegisteredMemberCount = 0,
        // The same people by name (one per account), so the officer sees who that button pays
        // before pressing it. Null except on a posted card that owes someone.
        IReadOnlyList<string>? UnpaidRegisteredMemberNames = null);

    public sealed record ActivityWindowEventMemberDkpInput(string? CharacterName, double? DkpAmount);

    // Keyed on the snapshot entry, not a character name: the whole point is that one person is
    // worth a different amount in each capture they appear in.
    public sealed record ActivityWindowEventCaptureDkpInput(int EntryId, double? DkpAmount);

    public sealed record ActivityWindowEventDkpRequest(
        double? DkpAmount,
        string? EntryType,
        IReadOnlyList<ActivityWindowEventMemberDkpInput>? MemberDkp,
        // Null means "misc pays what a window pays", which is the default.
        double? MiscDkpAmount = null,
        // The per-capture amounts, on a card that prices captures. Mutually exclusive with
        // MemberDkp by construction — the client sends one shape or the other.
        IReadOnlyList<ActivityWindowEventCaptureDkpInput>? CaptureDkp = null);

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
        string? SlotLabel,
        // The account that posted it (character name, else user name). Filled for unlinked
        // captures, where Triage shows who sent a member's capture; null elsewhere.
        string? PostedByName = null,
        // The post's own price (AttendanceSnapshot.DkpAmount), null when it pays the Misc post rate.
        // Filled for unlinked captures, where the Unlinked Snapshots row edits it.
        double? DkpOverride = null,
        // Where it was posted from (AttendanceSnapshotPostSources), null when not recorded.
        string? PostSource = null);

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
        bool AddedManually,
        // What THIS capture pays them, on a card that prices captures (see PerCaptureDkp). Null
        // on every other card, where the money is one amount per member.
        double? DkpAmount,
        // On the linkshell roster, so a DKP path can place them. False means the capture read
        // someone the shell does not know (a capture reads the whole alliance). Computed at read
        // time, so someone who registers mid-camp becomes creditable the moment they do.
        bool IsRegistered = true);

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
        string CreditSource,
        // On the linkshell roster. False means nothing will pay them no matter what amount this
        // row shows, which is what the client has to say out loud.
        bool IsRegistered = true);

    // WindowEventId attaches to an existing attendance event; Name find-or-creates one.
    // LinkedEventId is orthogonal to both: it records WHICH CAMP the snapshot belongs to,
    // so the camp's own card can show it. Sent when the officer picks a live camp from the
    // unlinked-snapshot dropdown, which does both at once — the name groups it for payroll,
    // the link puts it on the camp.
    // CreateNew forces a brand-new attendance event instead of folding into an open one of the
    // same name — what the "Create New Event" button means, as opposed to the dropdown's attach.
    // There is no slot to pick: a filed capture is always a misc post (SnapshotFilingService).
    public sealed record ActivityAttachWindowSnapshotRequest(
        int? WindowEventId, string? Name, int? LinkedEventId = null, bool CreateNew = false);
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

    // Closed events per page in the Activity's Attendance Archive.
    private const int ActivityClosedAttendancePageSize = 10;

    // How far back the attendance payload looks for posted cards owing a newly registered member.
    // Bounded because the payload is polled; most people register within days of their first camp.
    private const int PayNewlyRegisteredLookbackDays = 30;

    // attQ / attPage are the archive's search and page. They are the ONLY paged part of this
    // payload: open events and unlinked snapshots are live work an officer has to see in full.
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
        var canManage = await CanAsync(membership, LinkshellPermissions.EditsLiveCamp, cancellationToken);

        // Live only -- an ended camp is Status=Open but belongs in the pending queue below.
        // Shares AttendanceQueryFilters' predicate with the addon's Misc Posts panel so the two agree.
        var openEvents = await AttendanceQueryFilters.ApplyLiveFilter(
                _dbContext.WindowEvents.AsNoTracking().Where(e => e.LinkshellId == linkshellId))
            .OrderByDescending(e => e.LastCapturedAtUtc)
            .Include(e => e.Snapshots).ThenInclude(s => s.Entries)
            .Include(e => e.MemberDkpOverrides)
            .ToListAsync(cancellationToken);

        // The archive: searched and paged in SQL, BEFORE the Includes — each closed event drags
        // its whole snapshot/entry tree along, and this payload is polled while a camp is live.
        //
        // This replaced a flat .Take(25). That cap was invisible: the 26th closed event simply
        // wasn't there, and no search could reach it.
        var closedQuery = string.IsNullOrWhiteSpace(attQ) ? null : attQ.Trim();
        var closedBaseQuery = AttendanceQueryFilters.ApplyClosedSearch(
            AttendanceQueryFilters.ApplyPendingDkpPostFilter(
                _dbContext.WindowEvents.AsNoTracking().Where(e => e.LinkshellId == linkshellId)),
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
                s => s.LinkshellId == linkshellId && s.WindowEventId == null && s.LinkedEventId == null && s.SnapshotStatus != AttendanceSnapshotStatuses.Ignored,
                cancellationToken);

        // Captures awaiting confirmation first, so the cap never hides a member's /lsm now from a
        // day no officer was there behind officers' own captures; newest first within each group.
        var unlinkedSnapshots = await _dbContext.AttendanceSnapshots
            .AsNoTracking()
            .Where(s => s.LinkshellId == linkshellId && s.WindowEventId == null && s.LinkedEventId == null && s.SnapshotStatus != AttendanceSnapshotStatuses.Ignored)
            .OrderBy(s => s.SnapshotStatus == AttendanceSnapshotStatuses.Pending ? 0 : 1)
            .ThenByDescending(s => s.CapturedAtUtc)
            .ThenBy(s => s.AllianceNumber)
            .Take(unlinkedDisplayCap)
            .Include(s => s.Entries)
            .ToListAsync(cancellationToken);

        // Who posted each unlinked capture: the account behind the addon token, which for a member's
        // capture is not necessarily the in-game character that ran /lsm now. One query.
        var posterIds = unlinkedSnapshots
            .Select(s => s.PostedByAppUserId)
            .Where(id => !string.IsNullOrEmpty(id))
            .Distinct()
            .ToList();
        var posterNames = await _dbContext.Users
            .AsNoTracking()
            .Where(u => posterIds.Contains(u.Id))
            .Select(u => new { u.Id, Name = u.CharacterName ?? u.UserName })
            .ToDictionaryAsync(u => u.Id, u => u.Name, cancellationToken);

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

        // Who the shell actually knows, so every name a capture read can be marked as creditable or
        // not. One query for the whole response — the mappers take the set, never the DbContext.
        var roster = await LinkshellRosterNames.LoadAsync(_dbContext, linkshellId, cancellationToken);

        var miscPostRate = await MiscPostRateAsync(linkshellId, cancellationToken);

        // Posted cards drop out of every list above, so a card still owing a newly registered
        // member is listed on its own, for an officer to pay. Recent cards only: an older one can
        // still be settled through the DKP audit's Add.
        var postedOwingEvents = new List<ActivityWindowEventDto>();
        if (canManage)
        {
            var owing = await _windowEventDkpLedger.FindUnpaidAccountsAsync(
                linkshellId, DateTime.UtcNow.AddDays(-PayNewlyRegisteredLookbackDays), cancellationToken);
            if (owing.Count > 0)
            {
                var owingIds = owing.Keys.ToList();
                var owingCards = await _dbContext.WindowEvents
                    .AsNoTracking()
                    .Where(e => owingIds.Contains(e.Id))
                    .OrderByDescending(e => e.PostedToSheetAt)
                    .Include(e => e.Snapshots).ThenInclude(s => s.Entries)
                    .Include(e => e.MemberDkpOverrides)
                    .ToListAsync(cancellationToken);
                postedOwingEvents = owingCards
                    .Select(e => MapActivityWindowEvent(e, roster, miscPostRate) with
                    {
                        UnpaidRegisteredMemberCount = owing[e.Id].Count,
                        UnpaidRegisteredMemberNames = owing[e.Id],
                    })
                    .ToList();
            }
        }

        return Ok(new ActivityWindowEventsResponse(
            openEvents.Select(e => MapActivityWindowEvent(e, roster, miscPostRate)).ToList(),
            closedEvents.Select(e => MapActivityWindowEvent(e, roster, miscPostRate)).ToList(),
            // No parent event, so no window grid and no window label — deliberately: an unlinked
            // snapshot has no camp.
            unlinkedSnapshots
                .Select(s => MapActivityWindowSnapshot(s, null, roster) with
                {
                    PostedByName = s.PostedByAppUserId is { } posterId && posterNames.TryGetValue(posterId, out var name) ? name : null,
                    DkpOverride = s.DkpAmount,
                })
                .ToList(),
            canManage,
            WindowEventEntryTypes.All,
            rosterCharacterNames,
            closedQuery,
            closedPage,
            ActivityClosedAttendancePageSize,
            closedTotalCount,
            unlinkedTotalCount,
            unlinkedDisplayCap,
            miscPostRate,
            postedOwingEvents));
    }

    // Sets DKP + Entry Type (and any per-character overrides) on an UNposted event
    // without pushing rows (draft).
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

        await ApplyDkpDraftAsync(windowEvent.Value, request, cancellationToken);
        await _dbContext.SaveChangesAsync(cancellationToken);
        return Ok(new { success = true });
    }

    // Send to Review: a LIVE attendance event made from /lsm now leaves Live Events for Events
    // Pending DKP Post, where an officer reviews it and posts it to the sheet. Keeps the DKP typed
    // on the card (exactly what Save keeps) and closes it, in one save, so the card can't arrive in
    // review without the amounts it was sent with.
    [HttpPost("window-events/{windowEventId:int}/send-to-review")]
    public async Task<IActionResult> SendWindowEventToReviewAsync(
        int windowEventId,
        [FromBody] ActivityWindowEventDkpRequest request,
        CancellationToken cancellationToken)
    {
        var windowEvent = await LoadManageableWindowEventAsync(
            windowEventId, cancellationToken, includeMemberDkpOverrides: true);
        if (windowEvent.Result is not null) return windowEvent.Result;

        if (windowEvent.Value!.PostedToSheetAt.HasValue)
        {
            return BadRequest(new { error = "This event is already posted." });
        }
        // Live means what the Live Events list means (AttendanceQueryFilters.ApplyLiveFilter): an
        // ended camp or a closed event is already in review.
        if (windowEvent.Value.Status != WindowEventStatuses.Open || windowEvent.Value.CampEndedAtUtc != null)
        {
            return BadRequest(new { error = "This event is already in review." });
        }
        if (ValidateWindowEventDkp(request) is { } error) return BadRequest(new { error });

        await ApplyDkpDraftAsync(windowEvent.Value, request, cancellationToken);
        windowEvent.Value.Status = WindowEventStatuses.Closed;
        await _dbContext.SaveChangesAsync(cancellationToken);
        return Ok(new { success = true });
    }

    // What Save keeps from the card: DKP + Entry Type, per-character and per-capture amounts, and
    // the Misc rate. Stages only; the caller saves.
    private async Task ApplyDkpDraftAsync(
        WindowEvent windowEvent, ActivityWindowEventDkpRequest request, CancellationToken cancellationToken)
    {
        // Both fields are gone from the UI; keep whatever the event already carries when the
        // client omits them — see WindowEventDkp.Resolve / WindowEventEntryTypes.Resolve.
        var resolvedDkp = WindowEventDkp.Resolve(request.DkpAmount, windowEvent.DkpAmount);
        windowEvent.DkpAmount = resolvedDkp;
        windowEvent.EntryType = WindowEventEntryTypes.Resolve(
            request.EntryType, windowEvent.EntryType, windowEvent.Name);
        ApplyActivityMemberDkpOverrides(windowEvent, resolvedDkp, request.MemberDkp);
        ApplyActivityCaptureDkp(windowEvent, request.CaptureDkp);
        // Resolved BEFORE the assignment, or the stored-value fallback would read the value we are
        // about to overwrite and a save carrying no misc field would silently reset it.
        var resolvedMisc = WindowEventDkp.ResolveMisc(
            request.MiscDkpAmount, windowEvent.MiscDkpAmount,
            await MiscPostRateAsync(windowEvent.LinkshellId, cancellationToken) ?? resolvedDkp);
        windowEvent.MiscDkpAmount = request.MiscDkpAmount;
        // Skipped on a card that prices its captures: the Misc rate materializes itself as
        // per-member override rows, and those are not what such a card is paid from.
        if (!windowEvent.PerCaptureDkp)
        {
            WindowEventMiscDkp.ApplyMiscOverrides(
                windowEvent, resolvedDkp, resolvedMisc,
                WindowEventMiscDkp.SubmittedNames(
                    request.MemberDkp?.Select(m => new ViewModels.WindowEventMemberDkpInput
                    {
                        CharacterName = m.CharacterName,
                        DkpAmount = m.DkpAmount,
                    })));
        }
    }

    // Publishes a window event: sets DKP + Entry Type (and any per-character
    // overrides), marks it posted, and materializes the per-member SnapshotEarned
    // DKP ledger.
    //
    // Re-run from the top when a paid member's balance was changed by another request between our
    // read and our save (Data/ConcurrencyRetry.cs). The failed attempt's transaction rolled the
    // claim back too, so the re-run claims and pays as if it had arrived a moment later.
    [HttpPost("window-events/{windowEventId:int}/post")]
    public Task<IActionResult> PostWindowEventToSheetAsync(
        int windowEventId,
        [FromBody] ActivityWindowEventDkpRequest request,
        CancellationToken cancellationToken) =>
        ConcurrencyRetry.RunAsync(
            _dbContext, () => PostWindowEventToSheetOnceAsync(windowEventId, request, cancellationToken));

    private async Task<IActionResult> PostWindowEventToSheetOnceAsync(
        int windowEventId,
        ActivityWindowEventDkpRequest request,
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
        ApplyActivityCaptureDkp(windowEvent.Value, request.CaptureDkp);
        // Resolved BEFORE the assignment, or the stored-value fallback would read the value we are
        // about to overwrite and a save carrying no misc field would silently reset it.
        var resolvedMisc = WindowEventDkp.ResolveMisc(
            request.MiscDkpAmount, windowEvent.Value.MiscDkpAmount,
            await MiscPostRateAsync(windowEvent.Value.LinkshellId, cancellationToken) ?? resolvedDkp);
        windowEvent.Value.MiscDkpAmount = request.MiscDkpAmount;
        // Skipped on a card that prices its captures: the Misc rate materializes itself as
        // per-member override rows, and those are not what such a card is paid from.
        if (!windowEvent.Value.PerCaptureDkp)
        {
            WindowEventMiscDkp.ApplyMiscOverrides(
                windowEvent.Value, resolvedDkp, resolvedMisc,
                WindowEventMiscDkp.SubmittedNames(
                    request.MemberDkp?.Select(m => new ViewModels.WindowEventMemberDkpInput
                    {
                        CharacterName = m.CharacterName,
                        DkpAmount = m.DkpAmount,
                    })));
        }
        // Marks the card posted (with the draft above) and pays it, in one step that only one
        // request can win. The check at the top is only a fast answer: two Posts at the same moment
        // both pass it, and the loser is caught here.
        var paid = await _windowEventDkpLedger.PostAndPayAsync(
            windowEvent.Value, DateTime.UtcNow, cancellationToken);
        if (paid is null)
        {
            return Conflict(new { error = "This card has already been posted." });
        }

        // The saves inside PostAndPayAsync already told open clients to refresh, but before the
        // transaction committed, so a quick client could re-read the card while it still looked
        // unposted. Tell them again now that it is.
        var changeNotifier = HttpContext?.RequestServices.GetService<LinkshellChangeNotifier>();
        changeNotifier?.Notify(windowEvent.Value.LinkshellId, LinkshellChangeNotifier.Areas.Windows);
        changeNotifier?.Notify(windowEvent.Value.LinkshellId, LinkshellChangeNotifier.Areas.Dkp);
        return Ok(new { success = true });
    }

    // Edits a published event's DKP + Entry Type (and overrides) and reconciles
    // the existing ledger entries + per-member LinkshellDkp totals by the delta.
    //
    // Re-run from the top on a lost balance race (Data/ConcurrencyRetry.cs). It saves twice (the
    // draft, then Reconcile's balance moves), so both run in one transaction: a conflict on the
    // second undoes the first and the re-run starts clean.
    [HttpPost("window-events/{windowEventId:int}/edit-posted")]
    public Task<IActionResult> EditPostedWindowEventAsync(
        int windowEventId,
        [FromBody] ActivityWindowEventDkpRequest request,
        CancellationToken cancellationToken) =>
        ConcurrencyRetry.RunInTransactionAsync(
            _dbContext,
            () => EditPostedWindowEventOnceAsync(windowEventId, request, cancellationToken),
            cancellationToken);

    private async Task<IActionResult> EditPostedWindowEventOnceAsync(
        int windowEventId,
        ActivityWindowEventDkpRequest request,
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
        ApplyActivityCaptureDkp(windowEvent.Value, request.CaptureDkp);
        // Resolved BEFORE the assignment, or the stored-value fallback would read the value we are
        // about to overwrite and a save carrying no misc field would silently reset it.
        var resolvedMisc = WindowEventDkp.ResolveMisc(
            request.MiscDkpAmount, windowEvent.Value.MiscDkpAmount,
            await MiscPostRateAsync(windowEvent.Value.LinkshellId, cancellationToken) ?? resolvedDkp);
        windowEvent.Value.MiscDkpAmount = request.MiscDkpAmount;
        // Skipped on a card that prices its captures: the Misc rate materializes itself as
        // per-member override rows, and those are not what such a card is paid from.
        if (!windowEvent.Value.PerCaptureDkp)
        {
            WindowEventMiscDkp.ApplyMiscOverrides(
                windowEvent.Value, resolvedDkp, resolvedMisc,
                WindowEventMiscDkp.SubmittedNames(
                    request.MemberDkp?.Select(m => new ViewModels.WindowEventMemberDkpInput
                    {
                        CharacterName = m.CharacterName,
                        DkpAmount = m.DkpAmount,
                    })));
        }
        await _dbContext.SaveChangesAsync(cancellationToken);

        await _windowEventDkpLedger.ReconcilePostedWindowEventLedgerAsync(windowEvent.Value.Id, cancellationToken);
        return Ok(new { success = true });
    }

    // "Pay newly registered": Post pays only who is registered at that moment. Someone captured on
    // the card who registers afterwards is paid here, once, at the card's posted rates. Everyone
    // already paid from the card is skipped, so pressing it twice pays nobody twice.
    //
    // Re-run from the top on a lost balance race (Data/ConcurrencyRetry.cs); the payout's
    // transaction rolled back, so the re-run pays whoever is still unpaid.
    [HttpPost("window-events/{windowEventId:int}/pay-registered")]
    public Task<IActionResult> PayNewlyRegisteredAsync(
        int windowEventId,
        CancellationToken cancellationToken) =>
        ConcurrencyRetry.RunAsync(
            _dbContext, () => PayNewlyRegisteredOnceAsync(windowEventId, cancellationToken));

    private async Task<IActionResult> PayNewlyRegisteredOnceAsync(
        int windowEventId,
        CancellationToken cancellationToken)
    {
        var windowEvent = await LoadManageableWindowEventAsync(windowEventId, cancellationToken);
        if (windowEvent.Result is not null) return windowEvent.Result;

        if (!windowEvent.Value!.PostedToSheetAt.HasValue)
        {
            return BadRequest(new { error = "This event hasn't been published yet. Use Post to pay everyone on it." });
        }

        var linkshellId = windowEvent.Value.LinkshellId;
        var paid = await _windowEventDkpLedger.PayNewlyRegisteredAsync(windowEventId, cancellationToken);
        if (paid is null)
        {
            // Deleted between the check above and the payout's lock.
            return NotFound(new { error = "Window Event not found." });
        }

        if (paid > 0)
        {
            var changeNotifier = HttpContext?.RequestServices.GetService<LinkshellChangeNotifier>();
            changeNotifier?.Notify(linkshellId, LinkshellChangeNotifier.Areas.Windows);
            changeNotifier?.Notify(linkshellId, LinkshellChangeNotifier.Areas.Dkp);
        }
        return Ok(new { paid = paid.Value });
    }

    // Permanently deletes a Window Event and the captures filed under it.
    [HttpPost("window-events/{windowEventId:int}/delete")]
    public async Task<IActionResult> DeleteWindowEventAsync(
        int windowEventId,
        CancellationToken cancellationToken)
    {
        // Snapshots loaded because they are deleted WITH the event, not left behind as unlinked
        // captures (the FK is SetNull).
        var windowEvent = await LoadManageableWindowEventAsync(
            windowEventId, cancellationToken, includeSnapshots: true);
        if (windowEvent.Result is not null) return windowEvent.Result;

        var snapshots = windowEvent.Value!.Snapshots.ToList();
        if (snapshots.Count > 0)
        {
            _dbContext.AttendanceSnapshotEntries.RemoveRange(
                snapshots.SelectMany(snapshot => snapshot.Entries));
            _dbContext.AttendanceSnapshots.RemoveRange(snapshots);
        }
        _dbContext.WindowEvents.Remove(windowEvent.Value!);
        await _dbContext.SaveChangesAsync(cancellationToken);
        return Ok(new { success = true });
    }

    // Hard-deletes a snapshot (and its entries).
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

    // Removes one person from a snapshot (officer correction).
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

    // Adds a person the addon missed (alliance cap = 18, name de-dupe).
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
        if (await RefuseOnPostedCardAsync(snapshot.WindowEventId, cancellationToken) is { } posted) return posted;

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

    // The amounts land on the snapshot entries they were typed against, so one person priced
    // differently in three windows keeps three different amounts. Refuses on a card that is not
    // paid from its captures: numbers the payout ignores are worse than none.
    private static void ApplyActivityCaptureDkp(
        WindowEvent windowEvent,
        IReadOnlyList<ActivityWindowEventCaptureDkpInput>? inputs)
    {
        if (!windowEvent.PerCaptureDkp || inputs is null) return;

        var entriesById = windowEvent.Snapshots
            .SelectMany(snapshot => snapshot.Entries)
            .GroupBy(entry => entry.Id)
            .ToDictionary(group => group.Key, group => group.First());

        foreach (var input in inputs)
        {
            if (entriesById.TryGetValue(input.EntryId, out var entry))
            {
                entry.DkpAmount = input.DkpAmount;
            }
        }
    }

    // Characters whose value matches the event default (or is blank) have their
    // override row removed; differing values get upserted.
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
        await _dbContext.SaveChangesAsync(cancellationToken);
        return Ok(new { success = true });
    }

    [HttpPost("window-events/{windowEventId:int}/reopen")]
    public async Task<IActionResult> ReopenWindowEventAsync(int windowEventId, CancellationToken cancellationToken)
    {
        var windowEvent = await LoadManageableWindowEventAsync(windowEventId, cancellationToken);
        if (windowEvent.Result is not null) return windowEvent.Result;

        windowEvent.Value!.Status = WindowEventStatuses.Open;
        await _dbContext.SaveChangesAsync(cancellationToken);
        return Ok(new { success = true });
    }

    [HttpPost("window-events/snapshots/{snapshotId:int}/attach")]
    public async Task<IActionResult> AttachWindowSnapshotAsync(
        int snapshotId,
        [FromBody] ActivityAttachWindowSnapshotRequest request,
        [FromServices] SnapshotFilingService filing,
        CancellationToken cancellationToken)
    {
        var snapshot = await _dbContext.AttendanceSnapshots
            .Include(s => s.Entries)
            .FirstOrDefaultAsync(s => s.Id == snapshotId, cancellationToken);
        if (snapshot is null) return NotFound(new { error = "Snapshot not found." });

        var manageResult = await RequireWindowEventManagerAsync(snapshot.LinkshellId, cancellationToken);
        if (manageResult is not null) return manageResult;

        // The rules (live camp vs attendance event, always as a misc post) are shared with the
        // addon's Unlinked Posts panel. See SnapshotFilingService.
        var result = await filing.FileAsync(snapshot, new SnapshotFilingService.Request(
            request.WindowEventId, request.Name, request.LinkedEventId, request.CreateNew), cancellationToken);
        if (result.Error is not null) return StatusCode(result.StatusCode, new { error = result.Error });
        return result.FiledAgainstCamp
            ? Ok(new { success = true, filedAgainstCamp = true })
            : Ok(new { success = true });
    }

    // Undoes filing a capture against a LIVE camp: it goes back to Unlinked Snapshots exactly as it
    // arrived -- no camp, no window or Misc slot, no per-post DKP price -- so it stops paying anyone
    // on this camp and can be filed again. Only while the camp is running and the capture is still
    // parked on it; once End Camp has moved it onto the review card it is edited there instead.
    [HttpPost("window-events/snapshots/{snapshotId:int}/unlink")]
    public async Task<IActionResult> UnlinkCampSnapshotAsync(int snapshotId, CancellationToken cancellationToken)
    {
        var snapshot = await _dbContext.AttendanceSnapshots
            .FirstOrDefaultAsync(s => s.Id == snapshotId, cancellationToken);
        if (snapshot is null) return NotFound(new { error = "Snapshot not found." });

        var manageResult = await RequireWindowEventManagerAsync(snapshot.LinkshellId, cancellationToken);
        if (manageResult is not null) return manageResult;

        if (snapshot.LinkedEventId is null || snapshot.WindowEventId is not null)
        {
            return BadRequest(new { error = "This snapshot isn't linked to a live camp." });
        }

        snapshot.LinkedEventId = null;
        snapshot.SlotKind = AttendanceSnapshotSlotKinds.Window;
        snapshot.WindowNumber = null;
        snapshot.DkpAmount = null;
        await _dbContext.SaveChangesAsync(cancellationToken);
        return Ok(new { success = true });
    }

    // Renames the SNAPSHOT itself, and nothing else. Separate from attach's name field, which
    // conflated naming a capture with creating an event to file it under.
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
        // Confirming puts the capture's people on the card. Rejecting takes them off, which pays
        // nobody, so it stays allowed.
        if (request.Verified
            && await RefuseOnPostedCardAsync(snapshot.WindowEventId, cancellationToken) is { } posted)
        {
            return posted;
        }

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
        // Only Active puts the capture's people on the card; Ignored and Pending take them off.
        if (status == AttendanceSnapshotStatuses.Active
            && await RefuseOnPostedCardAsync(snapshot.WindowEventId, cancellationToken) is { } posted)
        {
            return posted;
        }

        snapshot.SnapshotStatus = status;
        if (status is AttendanceSnapshotStatuses.Active or AttendanceSnapshotStatuses.Ignored)
        {
        }
        await _dbContext.SaveChangesAsync(cancellationToken);
        return Ok(new { success = true });
    }

    // Refuses an edit that would put people on a POSTED card: adding a name, confirming a capture,
    // or setting one back to Active (filing one onto a posted card is refused in
    // SnapshotFilingService). Post paid who was on the card then, and "Pay newly registered" pays
    // anyone on it who has no row from it yet, so a name slipped onto a posted card would be paid
    // by the next press of that button without the review the card went through. Edits that only
    // take people off stay allowed.
    private async Task<IActionResult?> RefuseOnPostedCardAsync(int? windowEventId, CancellationToken cancellationToken)
    {
        if (windowEventId is not int cardId) return null;
        var postedAt = await _dbContext.WindowEvents
            .AsNoTracking()
            .Where(w => w.Id == cardId)
            .Select(w => w.PostedToSheetAt)
            .FirstOrDefaultAsync(cancellationToken);
        return postedAt.HasValue
            ? BadRequest(new { error = SnapshotFilingService.PostedCardRefusal })
            : null;
    }

    private async Task<(WindowEvent? Value, IActionResult? Result)> LoadManageableWindowEventAsync(
        int windowEventId,
        CancellationToken cancellationToken,
        bool includeMemberDkpOverrides = false,
        // The captures alone, for the delete path — which has no use for the override rows.
        bool includeSnapshots = false)
    {
        var query = _dbContext.WindowEvents.AsQueryable();
        if (includeMemberDkpOverrides)
        {
            query = query.Include(e => e.MemberDkpOverrides);
            // Snapshots too: the misc rate applies to members credited ONLY by Misc captures, and
            // nothing on this path used to care how a capture was filed.
            query = query.Include(e => e.Snapshots).ThenInclude(s => s.Entries);
        }
        else if (includeSnapshots)
        {
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
        if (!await CanAsync(membership, LinkshellPermissions.EditsLiveCamp, cancellationToken))
        {
            return Forbid();
        }
        return null;
    }

    // The linkshell's Misc post rate (HNM Settings). It is what a blank Misc DKP pays: an event
    // that never set its own pays misc-only members this rate, not the window amount.
    private Task<double?> MiscPostRateAsync(int linkshellId, CancellationToken cancellationToken)
        => _dbContext.Linkshells
            .AsNoTracking()
            .Where(l => l.Id == linkshellId)
            .Select(l => (double?)l.HnmStandardMiscBonus)
            .FirstOrDefaultAsync(cancellationToken);

    // `miscPostRate` fills in an event's blank Misc DKP, so the card's Misc DKP box and the roster
    // show the rate it will pay. A card that prices its captures keeps its own value: it is paid
    // per capture, not from the Misc rate.
    internal static ActivityWindowEventDto MapActivityWindowEvent(
        WindowEvent item, RosterNameSet? roster = null, double? miscPostRate = null)
    {
        var rosterNames = roster ?? RosterNameSet.Unknown;
        var miscDkp = item.PerCaptureDkp ? item.MiscDkpAmount : item.MiscDkpAmount ?? miscPostRate;

        // Pass the event itself: each snapshot needs its cadence (for "of 25") and its grid anchor
        // (to name the window of a capture taken before window numbering existed).
        var snapshots = item.Snapshots
            .OrderByDescending(s => s.CapturedAtUtc)
            .ThenBy(s => s.AllianceNumber)
            .Select(s => MapActivityWindowSnapshot(s, item, rosterNames))
            .ToList();
        var overrides = item.MemberDkpOverrides
            .Where(o => !string.IsNullOrWhiteSpace(o.CharacterName))
            .GroupBy(o => o.CharacterName.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().DkpAmount, StringComparer.OrdinalIgnoreCase);
        var combined = BuildActivityCombinedMembers(
            item.Snapshots, overrides, item.DkpAmount, miscDkp, item.PerCaptureDkp,
            rosterNames);
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
            miscDkp,
            WindowEventWindowGrid.WindowCount(item),
            WindowEventWindowGrid.Minutes(item) > 0,
            item.PerCaptureDkp,
            item.CampEndedAtUtc is not null || item.SourceEventId is not null,
            combined.Count(m => !m.IsRegistered));
    }

    // One row of a capture linked to a live camp. Asks the roster the same question, in the same
    // order, as the review card this camp becomes at End Camp and the ledger that pays from it.
    internal static ActivityLinkedSnapshotEntryDto MapLinkedSnapshotEntry(
        AttendanceSnapshotEntry entry, RosterNameSet roster)
        => new(
            entry.Id,
            entry.CharacterName,
            entry.MainJob,
            entry.MainJobLevel,
            entry.SubJob,
            entry.SubJobLevel,
            entry.Zone,
            entry.AddedManually,
            roster.Knows(entry.CharacterName, entry.AppUserId));

    // `windowEvent` supplies the cadence and grid anchor used to name the spawn window. Omitted for
    // an UNLINKED snapshot, which has no camp and therefore no window.
    internal static ActivityWindowSnapshotDto MapActivityWindowSnapshot(
        AttendanceSnapshot snapshot, WindowEvent? windowEvent = null, RosterNameSet? roster = null)
    {
        var rosterNames = roster ?? RosterNameSet.Unknown;

        // Scanned names first, alphabetically; hand-added people underneath in the order an officer
        // entered them.
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
                e.AddedManually,
                e.DkpAmount,
                rosterNames.Knows(e.CharacterName, e.AppUserId)))
            .ToList();

        // The STORED window number wins — it was pinned against the grid as it stood at capture.
        // Snapshots posted before window numbering existed have none, so theirs is derived here.
        var gridWindows = windowEvent is null ? (int?)null : WindowEventWindowGrid.WindowCount(windowEvent);
        var hasGrid = windowEvent is not null && WindowEventWindowGrid.Minutes(windowEvent) > 0;
        // A Misc capture has NO window, and the fallback must not invent one for it. Misc stores a
        // null WindowNumber, which is exactly the shape that triggers the derivation — so without
        // this test a misc post on a gridded camp would render "Window 4 of 25" beside its own Misc
        // chip.
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
            AttendanceSnapshotAlliances.LabelFor(snapshot),
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
                    : null,
            PostSource: snapshot.PostSource);
    }

    internal static List<ActivityWindowCombinedMemberDto> BuildActivityCombinedMembers(
        IEnumerable<AttendanceSnapshot> snapshots,
        IDictionary<string, double>? memberDkpOverrides = null,
        double? defaultDkpAmount = null,
        double? miscDkpAmount = null,
        // The captures carry the money on this card, so a member is owed their sum and neither the
        // per-member overrides nor the misc rate has anything to say.
        bool perCaptureDkp = false,
        RosterNameSet? roster = null)
    {
        var captureTotals = perCaptureDkp
            ? WindowEventCaptureDkp.SumByCharacter(snapshots)
            : null;
        var rosterNames = roster ?? RosterNameSet.Unknown;

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
                // appear in a misc post.
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
                    captureTotals is null ? overrideAmount : null,
                    captureTotals is not null ? captureTotals.GetValueOrDefault(g.Key) : overrideAmount ?? baseAmount,
                    creditSource,
                    g.Any(x => rosterNames.Knows(x.Entry.CharacterName, x.Entry.AppUserId)));
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
