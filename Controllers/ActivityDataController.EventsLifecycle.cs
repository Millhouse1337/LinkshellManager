using System.Globalization;
using System.Net.Http.Headers;
using LinkshellManagerDiscordApp.Data;
using LinkshellManagerDiscordApp.Models;
using LinkshellManagerDiscordApp.Services;
using LinkshellManagerDiscordApp.ViewModels;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NodaTime;

namespace LinkshellManagerDiscordApp.Controllers;

public sealed partial class ActivityDataController
{
    // How long after a create a second, byte-identical create is read as a retry rather than as a
    // second event somebody meant. Wide enough to cover a proxy retry of a slow request, far short
    // of the time it takes a human to fill the form in again with the same name and the same start.
    private const int DuplicateCreateWindowSeconds = 60;

    [HttpPost("events")]
    public async Task<IActionResult> CreateEventAsync([FromBody] ActivityCreateEventRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.EventName))
        {
            return BadRequest(new { error = "Event name is required." });
        }

        if (request.LinkshellId <= 0)
        {
            return BadRequest(new { error = "A linkshell selection is required." });
        }

        var appUser = await ResolveAppUserAsync(cancellationToken);
        if (appUser is null)
        {
            return Unauthorized(new
            {
                error = "Sign in with ASP.NET Identity or provide a Discord bearer token to create events."
            });
        }

        var membership = await GetMembershipAsync(appUser.Id, request.LinkshellId, cancellationToken);
        if (!await CanAsync(membership, r => r.CanManageEvents, cancellationToken))
        {
            return Forbid();
        }

        // HNM is an ordinary event type — any linkshell can create a camp. It just needs a
        // monster, which is what drives the window cycle and the board's routing.
        var isHnm = string.Equals((request.EventType ?? string.Empty).Trim(), "HNM", StringComparison.OrdinalIgnoreCase);
        string? monsterName = null;
        if (isHnm)
        {
            monsterName = request.MonsterName?.Trim();
            if (string.IsNullOrWhiteSpace(monsterName))
            {
                return BadRequest(new { error = "Select a monster for the HNM event." });
            }
        }

        if (!_timeZones.TryParseUserLocalOrUtc(request.StartTimeLocal, appUser.TimeZone, out var startTimeUtc) ||
            !_timeZones.TryParseUserLocalOrUtc(request.EndTimeLocal, appUser.TimeZone, out var endTimeUtc))
        {
            return BadRequest(new { error = "Use valid local start and end times in the event form." });
        }

        // Cross-linkshell defense: a PartySetup attached to an event must
        // belong to the same linkshell as the event. The frontend dropdown
        // is already filtered, but verify server-side too.
        if (request.PartySetupId.HasValue &&
            !await PartySetupBelongsToLinkshellAsync(request.PartySetupId.Value, request.LinkshellId, cancellationToken))
        {
            return BadRequest(new { error = "Selected party setup does not belong to this linkshell." });
        }

        // Double-submit guard. The create form disables its button while the POST is in flight, so
        // the duplicate that keeps landing in Queued Events is a duplicate REQUEST, not a duplicate
        // click: the Activity is proxied (Discord -> Cloudflare -> origin), and a retried POST reaches
        // this action a second time with the same body, which used to insert a second identical row.
        // Same linkshell, same name, same start, same creator, stamped seconds ago = that retry, so
        // hand back the first row's id and let the caller treat it as the success it already was.
        // Deliberately ahead of every Add/stage below, so the early return leaves nothing tracked.
        var duplicateCutoff = DateTime.UtcNow.AddSeconds(-DuplicateCreateWindowSeconds);
        var eventName = request.EventName.Trim();
        var duplicateId = await _dbContext.Events
            .Where(e => e.LinkshellId == request.LinkshellId
                        && e.CreatorUserId == appUser.Id
                        && e.EventName == eventName
                        && e.StartTime == startTimeUtc
                        && e.TimeStamp != null
                        && e.TimeStamp >= duplicateCutoff)
            .Select(e => (int?)e.Id)
            .FirstOrDefaultAsync(cancellationToken);
        if (duplicateId is { } existingEventId)
        {
            return Ok(new { success = true, eventId = existingEventId });
        }

        var eventEntity = new Event
        {
            LinkshellId = request.LinkshellId,
            EventName = eventName,
            EventType = request.EventType?.Trim(),
            EventLocation = request.EventLocation?.Trim(),
            CreatorUserId = appUser.Id,
            StartTime = startTimeUtc,
            EndTime = endTimeUtc,
            Duration = request.Duration,
            DkpPerHour = request.DkpPerHour,
            Details = request.Details?.Trim(),
            PartySetupId = request.PartySetupId,
            AutoStart = request.AutoStart,
            CountsTowardActive = request.CountsTowardActive,
            TimeStamp = DateTime.UtcNow
        };

        if (isHnm)
        {
            // No-DKP signup board: stamp the monster (authoritative for recurrence),
            // default the camp zone, engage post-by-window UI, and strip the fields
            // the form hides (End/Duration/DKP/AutoStart/active tracking).
            eventEntity.AssignedMonsterName = monsterName;
            eventEntity.DayNumber = request.DayNumber;
            if (string.IsNullOrWhiteSpace(eventEntity.EventLocation))
            {
                eventEntity.EventLocation = HnmConfig.ZoneFor(monsterName);
            }
            // Seed the built-in per-monster window count + Manual Check In stamp.
            await HnmEventSeeder.SeedHnmEventAsync(_dbContext, eventEntity, null, monsterName, cancellationToken);
            eventEntity.EndTime = null;
            eventEntity.Duration = null;
            eventEntity.DkpPerHour = 0;
            eventEntity.AutoStart = false;
            eventEntity.CountsTowardActive = false;
            // Per-camp bonus overrides are HNM-only, so they're stamped here rather than in the
            // initializer above — a non-HNM event can't carry them even if a client sends them.
            eventEntity.HnmOpenBonusOverride = NormalizeBonusOverride(request.HnmOpenBonusOverride);
            eventEntity.HnmCloseBonusOverride = NormalizeBonusOverride(request.HnmCloseBonusOverride);
            eventEntity.HnmClaimBonusOverride = NormalizeBonusOverride(request.HnmClaimBonusOverride);
            eventEntity.HnmKillBonusOverride = NormalizeBonusOverride(request.HnmKillBonusOverride);
            eventEntity.HnmPerWindowOverride = NormalizeBonusOverride(request.HnmPerWindowOverride);
        }

        _dbContext.Events.Add(eventEntity);
        await _dbContext.SaveChangesAsync(cancellationToken);

        return Ok(new { success = true, eventId = eventEntity.Id });
    }

    private async Task<bool> PartySetupBelongsToLinkshellAsync(
        int partySetupId, int linkshellId, CancellationToken cancellationToken)
    {
        return await _dbContext.PartySetups
            // OwnerEventId == null → only a reusable template may be attached to an event,
            // never another event's private snapshot.
            .AnyAsync(setup => setup.Id == partySetupId && setup.LinkshellId == linkshellId
                && setup.OwnerEventId == null, cancellationToken);
    }

    [HttpPost("events/{eventId:int}/update")]
    public async Task<IActionResult> UpdateEventAsync(
        int eventId,
        [FromBody] ActivityCreateEventRequest request,
        CancellationToken cancellationToken)
    {
        if (request.LinkshellId <= 0)
        {
            return BadRequest(new { error = "A linkshell selection is required." });
        }

        if (string.IsNullOrWhiteSpace(request.EventName))
        {
            return BadRequest(new { error = "Event name is required." });
        }

        var appUser = await ResolveAppUserAsync(cancellationToken);
        if (appUser is null)
        {
            return Unauthorized(new
            {
                error = "Sign in with ASP.NET Identity or provide a Discord bearer token to update events."
            });
        }

        var membership = await GetMembershipAsync(appUser.Id, request.LinkshellId, cancellationToken);
        if (!await CanAsync(membership, r => r.CanManageEvents, cancellationToken))
        {
            return Forbid();
        }

        var eventEntity = await _dbContext.Events
            .Include(evt => evt.AppUserEvents)
            .FirstOrDefaultAsync(evt => evt.Id == eventId, cancellationToken);

        if (eventEntity is null)
        {
            return NotFound(new { error = "The selected event was not found." });
        }

        var currentMembership = await GetMembershipAsync(appUser.Id, eventEntity.LinkshellId, cancellationToken);
        if (!await CanAsync(currentMembership, r => r.CanManageEvents, cancellationToken))
        {
            return Forbid();
        }

        if (!_timeZones.TryParseUserLocalOrUtc(request.StartTimeLocal, appUser.TimeZone, out var startTimeUtc) ||
            !_timeZones.TryParseUserLocalOrUtc(request.EndTimeLocal, appUser.TimeZone, out var endTimeUtc))
        {
            return BadRequest(new { error = "Use valid local start and end times in the event form." });
        }

        // Once an event is live, its participants are tied to the originating
        // linkshell — moving it elsewhere mid-run would orphan their DKP awards.
        // Other fields (name, times, dkp/hour, details) remain editable so an
        // officer can correct typos or extend an in-progress run.
        if (eventEntity.CommencementStartTime.HasValue && request.LinkshellId != eventEntity.LinkshellId)
        {
            return BadRequest(new { error = "A live event's linkshell cannot be changed. End the event first." });
        }

        // Only validate the party setup when it's actually CHANGING to a different one —
        // keeping the event's current setup never needs re-checking (it was valid when
        // attached). This matters because a customized board becomes a per-event SNAPSHOT
        // (OwnerEventId != null), which PartySetupBelongsToLinkshellAsync intentionally
        // rejects; without this guard, editing any other field on a snapshot-board event
        // (e.g. renaming it) would fail with "does not belong to this linkshell".
        // A snapshot the caller posts back unchanged is therefore preserved further down.
        if (request.PartySetupId.HasValue &&
            request.PartySetupId != eventEntity.PartySetupId &&
            !await PartySetupBelongsToLinkshellAsync(request.PartySetupId.Value, request.LinkshellId, cancellationToken))
        {
            return BadRequest(new { error = "Selected party setup does not belong to this linkshell." });
        }

        // What the form opened with, so a parked board coming back below can tell the fields the
        // officer changed from the leftovers it was pre-filled with.
        var startBefore = eventEntity.StartTime;
        var dayBefore = await HnmRecurringBoardService.DayShownInEditFormAsync(
            _dbContext, eventEntity, cancellationToken);

        // The ToD the officer just agreed to move, looked up before the edit changes the event. Only
        // if it is still the one the popup named: a new ToD logged meanwhile moves nothing.
        var todToMove = request.MoveTodId is null
            ? null
            : await HnmRecurringBoardService.FindLinkedTodAsync(_dbContext, eventEntity, cancellationToken);
        if (todToMove?.Id != request.MoveTodId)
        {
            todToMove = null;
        }

        eventEntity.LinkshellId = request.LinkshellId;
        eventEntity.EventName = request.EventName.Trim();
        eventEntity.EventType = request.EventType?.Trim();
        eventEntity.EventLocation = request.EventLocation?.Trim();
        eventEntity.StartTime = startTimeUtc;
        eventEntity.EndTime = endTimeUtc;
        eventEntity.Duration = request.Duration;
        eventEntity.DkpPerHour = request.DkpPerHour;
        eventEntity.Details = request.Details?.Trim();
        // Changing (or removing) the linked party setup WIPES the sign-up board: the event's
        // slot signups are keyed to the OLD setup's slot ids, which don't exist on the new
        // board, so they're cleared and members claim again. Participation (AppUserEvents) is
        // untouched by that wipe — on a live camp everyone keeps their timer, their DKP and
        // their attendance credit; only the seating goes. The Activity warns first.
        //
        // A board customized on the live editor is a per-event SNAPSHOT (OwnerEventId == this
        // event), which the template picker can't offer as one of its own rows — the Activity
        // shows it as a "current board" entry instead, so an unchanged save posts the same id,
        // skips this branch entirely, and the customized board survives. An officer who picks a
        // different setup (or None) still gets the swap, and the wipe with it.
        if (eventEntity.PartySetupId != request.PartySetupId)
        {
            await EventPartySignupService.ClearSlotSignupsAsync(
                _dbContext, eventEntity.Id, cancellationToken);
            eventEntity.PartySetupId = request.PartySetupId;
        }
        eventEntity.AutoStart = request.AutoStart;
        eventEntity.CountsTowardActive = request.CountsTowardActive;

        // HNM signup boards never award DKP or feed activity tracking, and they carry no
        // end/duration. Re-assert that on edit so the form's hidden defaults (countsTowardActive
        // defaults true, dkpPerHour > 0) can't flip an HNM board into a DKP-earning, tracked
        // event. Also keep the monster/window in sync if the picker changed it.
        var isHnm = string.Equals((request.EventType ?? string.Empty).Trim(), "HNM", StringComparison.OrdinalIgnoreCase);
        if (isHnm)
        {
            var monsterName = request.MonsterName?.Trim();
            if (!string.IsNullOrWhiteSpace(monsterName))
            {
                // Re-stamped only when the camp hasn't started or its monster changed. The form
                // sends the monster on EVERY save, so re-stamping unconditionally let an unrelated
                // edit (a typo in the details) pull a Monster Setups change into a live camp — the
                // thing Event.SpawnWindowCount and Event.WindowMethod are stamped to prevent.
                var monsterChanged = !string.Equals(
                    eventEntity.AssignedMonsterName?.Trim(), monsterName, StringComparison.OrdinalIgnoreCase);
                var restamp = monsterChanged || eventEntity.CommencementStartTime is null;
                eventEntity.AssignedMonsterName = monsterName;
                if (restamp)
                {
                    eventEntity.WindowCountOverride = HnmConfig.EffectiveWindowCount(monsterName);
                    await HnmEventSeeder.StampSpawnGridAsync(_dbContext, eventEntity, monsterName, cancellationToken);
                }
                // No zone fill-in here, unlike create: the edit form opens with the saved location,
                // so a blank one is an officer who erased it. Refilling it made Location impossible
                // to clear -- erase the location, Update, and the default zone came straight back.
            }
            HnmRecurringBoardService.ApplyEditedDay(eventEntity, dayBefore, request.DayNumber);
            eventEntity.EndTime = null;
            eventEntity.Duration = null;
            eventEntity.DkpPerHour = 0;
            eventEntity.AutoStart = false;
            eventEntity.CountsTowardActive = false;
            // Sent on every edit (the form round-trips them), so a null here means the user
            // closed "Change DKP" and wants the linkshell default back, not "leave as-is".
            eventEntity.HnmOpenBonusOverride = NormalizeBonusOverride(request.HnmOpenBonusOverride);
            eventEntity.HnmCloseBonusOverride = NormalizeBonusOverride(request.HnmCloseBonusOverride);
            eventEntity.HnmClaimBonusOverride = NormalizeBonusOverride(request.HnmClaimBonusOverride);
            eventEntity.HnmKillBonusOverride = NormalizeBonusOverride(request.HnmKillBonusOverride);
            eventEntity.HnmPerWindowOverride = NormalizeBonusOverride(request.HnmPerWindowOverride);

            // When the sign-up board re-posts, if this monster re-posts at all. Saved with the
            // event below; RefreshRepostAtAsync after it moves the advertised time to match.
            await HnmRecurringBoardService.ApplyEditedLeadAsync(
                _dbContext, eventEntity, request.RepostLeadHours, cancellationToken);

            // A LIVE camp whose start no longer matches the time its grid counts from: the grid
            // moves to the start, or the board keeps counting windows from the time it went live
            // on. Compared against the ANCHOR, not the start the form opened with, so a camp whose
            // start was moved before this fix heals on its next save. Nothing else moves the anchor
            // once a camp is live, so a healthy camp never trips this.
            if (eventEntity.CommencementStartTime is not null
                && startTimeUtc is { } newStartUtc
                && newStartUtc != (eventEntity.WindowAnchorAt ?? startBefore))
            {
                HnmWindowAdvanceBackgroundService.ReanchorToStart(eventEntity, newStartUtc, DateTime.UtcNow);
            }

            // Saved with the event, so the event and the ToD page can't end up disagreeing.
            if (todToMove is not null && startTimeUtc is { } movedStartUtc)
            {
                HnmRecurringBoardService.MoveTodToRepop(todToMove, movedStartUtc, DateTime.UtcNow);
            }
        }

        await _dbContext.SaveChangesAsync(cancellationToken);

        // The edit never changes the monster's standing repost setting (only End Camp / Post ToD
        // does), but it can move the board or switch its monster. A board already waiting on its
        // repost advertises the old time until this runs.
        var recurrenceMonster = eventEntity.AssignedMonsterName?.Trim();
        if (isHnm && !string.IsNullOrWhiteSpace(recurrenceMonster))
        {
            // A moved ToD is handled like an edit on the ToD page, including re-opening a re-post
            // cycle the poller had already finished with.
            if (todToMove is not null)
            {
                await HnmRecurringBoardService.SyncParkedBoardsForTodAsync(
                    _dbContext, todToMove.LinkshellId, todToMove.MonsterName, cancellationToken);
            }

            await HnmRecurringBoardService.RefreshRepostAtAsync(
                _dbContext, eventEntity, recurrenceMonster, cancellationToken);

            // Parked, and its re-post time has already come: Update brings the sign-up board
            // back now rather than re-drawing the "defeated" note again.
            if (await HnmRecurringBoardService.ReviveDueParkedBoardAfterEditAsync(
                    _dbContext, eventEntity, DateTime.UtcNow,
                    startTimeUtc != startBefore ? startTimeUtc : null,
                    request.DayNumber != dayBefore ? request.DayNumber : null,
                    cancellationToken))
            {
                await _dbContext.SaveChangesAsync(cancellationToken);
            }
        }

        return Ok(new { success = true });
    }

    // The ToD a changed Starts would move, so the edit form can name it before anything is saved.
    [HttpGet("events/{eventId:int}/linked-tod")]
    public async Task<IActionResult> GetLinkedTodAsync(int eventId, CancellationToken cancellationToken)
    {
        var appUser = await ResolveAppUserAsync(cancellationToken);
        if (appUser is null)
        {
            return Unauthorized(new { error = "Sign in to read this event's ToD." });
        }

        var eventEntity = await _dbContext.Events
            .AsNoTracking()
            .FirstOrDefaultAsync(evt => evt.Id == eventId, cancellationToken);
        if (eventEntity is null)
        {
            return NotFound(new { error = "The selected event was not found." });
        }

        var membership = await GetMembershipAsync(appUser.Id, eventEntity.LinkshellId, cancellationToken);
        if (!await CanAsync(membership, r => r.CanManageEvents, cancellationToken))
        {
            return Forbid();
        }

        var tod = await HnmRecurringBoardService.FindLinkedTodAsync(_dbContext, eventEntity, cancellationToken);
        if (tod is null)
        {
            return Ok(new { tod = (object?)null });
        }
        return Ok(new
        {
            tod = new
            {
                todId = tod.Id,
                monsterName = tod.MonsterName,
                time = tod.Time is { } time ? DateTime.SpecifyKind(time, DateTimeKind.Utc) : (DateTime?)null,
                repopTime = DateTime.SpecifyKind(tod.RepopTime!.Value, DateTimeKind.Utc),
            }
        });
    }

    [HttpPost("events/{eventId:int}/start")]
    public async Task<IActionResult> StartEventAsync(
        int eventId,
        [FromBody] ActivityStartEventRequest? request,
        CancellationToken cancellationToken)
    {
        var appUser = await ResolveAppUserAsync(cancellationToken);
        if (appUser is null)
        {
            return Unauthorized(new
            {
                error = "Sign in with ASP.NET Identity or provide a Discord bearer token to start events."
            });
        }

        var eventEntity = await _dbContext.Events
            .Include(evt => evt.AppUserEvents)
            .Include(evt => evt.Linkshell)
            .FirstOrDefaultAsync(evt => evt.Id == eventId, cancellationToken);

        if (eventEntity is null)
        {
            return NotFound(new { error = "The selected event was not found." });
        }

        var membership = await GetMembershipAsync(appUser.Id, eventEntity.LinkshellId, cancellationToken);
        if (!await CanAsync(membership, r => r.CanManageEvents, cancellationToken))
        {
            return Forbid();
        }

        // HNM camps auto-start at their scheduled window-1 pop (EventAutoStartBackgroundService),
        // but an officer may also start one early by hand here — the window advancer keys off the
        // scheduled anchor either way, and the addon's own start (CommencementStartTime ??=) stays
        // a no-op once we've commenced. Stamp the window anchor from the scheduled time so an early
        // manual start doesn't shift the cadence.
        var isHnmEvent = string.Equals((eventEntity.EventType ?? string.Empty).Trim(), "HNM", StringComparison.OrdinalIgnoreCase);
        if (isHnmEvent)
        {
            // Same as the addon's start: a board still parked from its last camp comes back as a
            // fresh camp first, or it starts while still marked ended and vanishes from the list.
            await HnmRecurringBoardService.ReviveParkedBoardForStartAsync(_dbContext, eventEntity, cancellationToken);
            eventEntity.WindowAnchorAt ??= eventEntity.StartTime;
        }

        var absentIds = request?.AbsentParticipantIds;
        if (absentIds is { Count: > 0 })
        {
            var absentSet = new HashSet<int>(absentIds);
            var absentParticipations = eventEntity.AppUserEvents
                .Where(p => absentSet.Contains(p.Id))
                .ToList();

            foreach (var participation in absentParticipations)
            {
                _dbContext.AppUserEvents.Remove(participation);
            }
        }

        eventEntity.CommencementStartTime ??= DateTime.UtcNow;
        eventEntity.StarterUserId ??= appUser.Id;
        // Bring party-slot signups (Discord post / Activity) into the live event as
        // pending attendees — without this they'd never appear in the started event.
        await EventPartySignupService.MaterializeSignupsAsParticipantsAsync(_dbContext, eventEntity, cancellationToken);
        foreach (var participation in eventEntity.AppUserEvents)
        {
            if (absentIds is { Count: > 0 } && absentIds.Contains(participation.Id))
            {
                continue;
            }
            participation.StartTime ??= eventEntity.CommencementStartTime;
        }

        await _dbContext.SaveChangesAsync(cancellationToken);
        return Ok(new { success = true });
    }

    // Pays the whole roster (or hands an HNM camp off) in one save. Re-run from the top when a
    // member's balance or the event itself changed under it (Data/ConcurrencyRetry.cs): the re-run
    // reads the event again, so a second End pressed at the same moment finds it already ended.
    [HttpPost("events/{eventId:int}/end")]
    public Task<IActionResult> EndEventAsync(int eventId, CancellationToken cancellationToken) =>
        ConcurrencyRetry.RunAsync(_dbContext, () => EndEventOnceAsync(eventId, cancellationToken));

    private async Task<IActionResult> EndEventOnceAsync(int eventId, CancellationToken cancellationToken)
    {
        var appUser = await ResolveAppUserAsync(cancellationToken);
        if (appUser is null)
        {
            return Unauthorized(new
            {
                error = "Sign in with ASP.NET Identity or provide a Discord bearer token to end events."
            });
        }

        var eventEntity = await _dbContext.Events
            .Include(evt => evt.AppUserEvents)
            .Include(evt => evt.EventLootDetails)
            .Include(evt => evt.Linkshell)
            .FirstOrDefaultAsync(evt => evt.Id == eventId, cancellationToken);

        if (eventEntity is null)
        {
            return NotFound(new { error = "The selected event was not found." });
        }

        var membership = await GetMembershipAsync(appUser.Id, eventEntity.LinkshellId, cancellationToken);
        if (!await CanAsync(membership, r => r.CanManageEvents, cancellationToken))
        {
            return Forbid();
        }

        // EVERY HNM camp ends through the camp path, in both attendance modes: it stages a review
        // row (an officer's Post credits the DKP) and recycles the board. A generic end pays an HNM
        // camp windowsAttended × DkpPerHour, and DkpPerHour is 0 on every HNM camp — see the note
        // on HnmCampReviewHandoffService.EndsThroughCampPath.
        if (HnmCampReviewHandoffService.EndsThroughCampPath(eventEntity))
        {
            var finalized = HttpContext.RequestServices.GetService(typeof(HnmCampReviewHandoffService))
                    is HnmCampReviewHandoffService handoff
                && await handoff.HandOffAndRecycleAsync(eventEntity.Id, cancellationToken);
            return Ok(new { success = true, finalized });
        }

        // The close itself is EventEndService's, the one the addon's End Event runs too. The app's
        // own rules are EndEventOptions.Activity: refuse while a member is still pending
        // confirmation, pay nothing on a Loot Council linkshell, and give outside (account-less)
        // sign-ups no history row. It was a hand-kept copy of the same close until P3.5, and the
        // two drifted (alt-won loot went uncharged here); EndEventOnePathTests compares the two.
        //
        // Wrapped so ANY exception -- not just a DbUpdateException at the save -- returns
        // actionable JSON instead of an HTML 500 that the Activity mislabels as "your session may
        // have expired". See the catch below.
        EventEndService.EndEventResult result;
        try
        {
            result = await EventEndService.EndEventCoreAsync(
                _dbContext, _dkpLedger, _dkpPools, eventEntity, EventEndService.EndEventOptions.Activity);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not DbUpdateConcurrencyException)
        {
            // Return actionable JSON instead of letting it bubble to the generic HTML error
            // page — which the SPA can only read back as the misleading "your session may have
            // expired" message. Broad on purpose: a narrow catch(DbUpdateException) let
            // non-DbUpdate exceptions (EF tracking, etc.) escape. Don't echo the raw EF/Npgsql
            // message to the client (it leaks schema/constraint/SQL detail); log the full
            // exception with a correlation id and surface only that id for support to trace.
            //
            // A lost race (DbUpdateConcurrencyException) is left out on purpose: nothing was
            // saved, and EndEventAsync above re-runs the whole close for it.
            var correlationId = Guid.NewGuid().ToString("N");
            _logger.LogError(ex,
                "Failed to end event {EventId} for linkshell {LinkshellId}. CorrelationId={CorrelationId}",
                eventId, eventEntity.LinkshellId, correlationId);
            return StatusCode(500, new
            {
                error = "Ending the event failed — please retry; if it keeps happening, contact an admin.",
                correlationId
            });
        }

        // Nothing was saved: every pending member must be confirmed present or removed first.
        if (result.Refused)
        {
            return BadRequest(new
            {
                error = $"Confirm or remove the {result.PendingAttendeeCount} member(s) still pending in attendance before ending the event."
            });
        }

        // The core also recomputes Active/Inactive status after the close, logging (not failing)
        // if that recompute breaks, since the event is already ended by then.
        return Ok(new { success = true });
    }

    [HttpPost("events/{eventId:int}/cancel")]
    public async Task<IActionResult> CancelEventAsync(int eventId, CancellationToken cancellationToken)
    {
        var appUser = await ResolveAppUserAsync(cancellationToken);
        if (appUser is null)
        {
            return Unauthorized(new
            {
                error = "Sign in with ASP.NET Identity or provide a Discord bearer token to cancel events."
            });
        }

        var eventEntity = await _dbContext.Events
            .Include(evt => evt.AppUserEvents)
            .Include(evt => evt.EventLootDetails)
            .FirstOrDefaultAsync(evt => evt.Id == eventId, cancellationToken);

        if (eventEntity is null)
        {
            return NotFound(new { error = "The selected event was not found." });
        }

        var membership = await GetMembershipAsync(appUser.Id, eventEntity.LinkshellId, cancellationToken);
        if (!await CanAsync(membership, r => r.CanManageEvents, cancellationToken))
        {
            return Forbid();
        }

        if (eventEntity.CommencementStartTime.HasValue)
        {
            return BadRequest(new { error = "Live events cannot be canceled. End the event instead." });
        }

        // Removing a board stops it repeating -- see StopRepeatingForRemovedBoardAsync. Staged here
        // and committed by the save below, with the delete.
        await HnmRecurringBoardService.StopRepeatingForRemovedBoardAsync(_dbContext, eventEntity, cancellationToken);
        _dbContext.AppUserEvents.RemoveRange(eventEntity.AppUserEvents);
        _dbContext.EventLootDetails.RemoveRange(eventEntity.EventLootDetails);
        _dbContext.Events.Remove(eventEntity);

        await _dbContext.SaveChangesAsync(cancellationToken);
        return Ok(new { success = true });
    }

    // Hard-deletes an HNM camp outright — no ToD, no repop, no history, no DKP. This is the "discard
    // this board" path the Activity's live camp card exposes: End Camp requires a Time of Death, this
    // doesn't. Unlike Cancel it also works on a LIVE camp (Cancel refuses commenced events). Officer-
    // gated and scoped to HNM so a real event's DKP/history can't be silently thrown away. Mirrors
    // Cancel's cleanup: participants + loot details are removed explicitly (loot is SetNull on Event
    // delete, so it must be); removing the Event cascades slot signups, attendance windows, and
    // status-ledger rows. (The posted Discord board message, if any, is left inert — same as Cancel.)
    [HttpPost("events/{eventId:int}/delete")]
    public async Task<IActionResult> DeleteEventAsync(int eventId, CancellationToken cancellationToken)
    {
        var appUser = await ResolveAppUserAsync(cancellationToken);
        if (appUser is null)
        {
            return Unauthorized(new
            {
                error = "Sign in with ASP.NET Identity or provide a Discord bearer token to delete events."
            });
        }

        var eventEntity = await _dbContext.Events
            .Include(evt => evt.AppUserEvents)
            .Include(evt => evt.EventLootDetails)
            .FirstOrDefaultAsync(evt => evt.Id == eventId, cancellationToken);

        if (eventEntity is null)
        {
            return NotFound(new { error = "The selected event was not found." });
        }

        var membership = await GetMembershipAsync(appUser.Id, eventEntity.LinkshellId, cancellationToken);
        if (!await CanAsync(membership, r => r.CanManageEvents, cancellationToken))
        {
            return Forbid();
        }

        if (!string.Equals((eventEntity.EventType ?? string.Empty).Trim(), "HNM", StringComparison.OrdinalIgnoreCase))
        {
            return BadRequest(new
            {
                error = "Only HNM camps can be deleted here. Use Cancel (queued) or End (live) for other events."
            });
        }

        // Removing a board stops it repeating -- see StopRepeatingForRemovedBoardAsync. Staged here
        // and committed by the save below, with the delete.
        await HnmRecurringBoardService.StopRepeatingForRemovedBoardAsync(_dbContext, eventEntity, cancellationToken);
        _dbContext.AppUserEvents.RemoveRange(eventEntity.AppUserEvents);
        _dbContext.EventLootDetails.RemoveRange(eventEntity.EventLootDetails);
        _dbContext.Events.Remove(eventEntity);

        await _dbContext.SaveChangesAsync(cancellationToken);
        return Ok(new { success = true });
    }

    // A per-camp HNM bonus override as it should be stored: null (use the linkshell default)
    // or a non-negative amount. Clamping here rather than trusting the client keeps
    // HnmStandardCampFinalizer's Math.Max from being the only thing standing between a
    // negative payload and a camp that DEDUCTS DKP.
    private static double? NormalizeBonusOverride(double? value)
    {
        if (value is null || double.IsNaN(value.Value) || double.IsInfinity(value.Value))
        {
            return null;
        }
        return Math.Max(0d, value.Value);
    }
}
