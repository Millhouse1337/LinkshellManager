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

        // HNM is a manual outside-signup board: a no-DKP board posted to Discord so
        // unsynced server members can sign up, repeated off ToD captures. It has its
        // own gate (HNM Outside Sign Up), independent of Outside Party Signup, and
        // requires a monster.
        var isHnm = string.Equals((request.EventType ?? string.Empty).Trim(), "HNM", StringComparison.OrdinalIgnoreCase);
        string? monsterName = null;
        if (isHnm)
        {
            if (membership?.Linkshell?.HnmOutsideSignupEnabled != true)
            {
                return BadRequest(new
                {
                    error = "HNM signup boards require HNM Outside Sign Up to be enabled for this linkshell."
                });
            }
            monsterName = request.MonsterName?.Trim();
            if (string.IsNullOrWhiteSpace(monsterName))
            {
                return BadRequest(new { error = "Select a monster for the HNM event." });
            }
        }

        if (!TryConvertUserTimeZoneToUtc(request.StartTimeLocal, appUser.TimeZone, out var startTimeUtc) ||
            !TryConvertUserTimeZoneToUtc(request.EndTimeLocal, appUser.TimeZone, out var endTimeUtc))
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

        var eventEntity = new Event
        {
            LinkshellId = request.LinkshellId,
            EventName = request.EventName.Trim(),
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

        // Repeat-on-ToD: persist/refresh the recurring-board template so the board
        // re-posts before the next predicted pop. Works for a custom monster too —
        // recurrence keys on the (case-insensitive) AssignedMonsterName the ToD records.
        // Lead is null on purpose: this form only toggles recurrence, and UpsertAsync keeps
        // whatever lead the End Camp / Post ToD form last set.
        if (isHnm && request.RepeatOnTod)
        {
            await HnmRecurringBoardService.UpsertAsync(_dbContext, eventEntity, null, appUser.Id, cancellationToken);
        }
        else if (isHnm && !request.RepeatOnTod)
        {
            await HnmRecurringBoardService.DisableAsync(_dbContext, request.LinkshellId, monsterName, cancellationToken);
        }

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

        if (!TryConvertUserTimeZoneToUtc(request.StartTimeLocal, appUser.TimeZone, out var startTimeUtc) ||
            !TryConvertUserTimeZoneToUtc(request.EndTimeLocal, appUser.TimeZone, out var endTimeUtc))
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
        // (e.g. toggling repeat-on-ToD) would fail with "does not belong to this linkshell".
        // The snapshot is preserved further down (see currentIsSnapshot).
        if (request.PartySetupId.HasValue &&
            request.PartySetupId != eventEntity.PartySetupId &&
            !await PartySetupBelongsToLinkshellAsync(request.PartySetupId.Value, request.LinkshellId, cancellationToken))
        {
            return BadRequest(new { error = "Selected party setup does not belong to this linkshell." });
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
        // If the board was customized into a per-event snapshot (which the template
        // picker can't represent), keep it — its slots are managed from the board editor,
        // not this form — so editing event details never wipes the customized board.
        var currentIsSnapshot = eventEntity.PartySetupId is { } curSetupId
            && await _dbContext.PartySetups.AnyAsync(
                ps => ps.Id == curSetupId && ps.OwnerEventId == eventEntity.Id, cancellationToken);
        if (!currentIsSnapshot)
        {
            // Changing (or removing) the linked party setup orphans the event's slot
            // signups — they're keyed to the OLD setup's slot ids. Rather than drop
            // those members, move them to the event's "no slot" attendance so they
            // still show on the new board (under "Also attending — no slot"). The
            // Activity warns the user first.
            if (eventEntity.PartySetupId != request.PartySetupId)
            {
                await EventPartySignupService.MoveSlotSignupsToNoSlotAsync(
                    _dbContext, eventEntity.Id, eventEntity.CommencementStartTime, cancellationToken);
            }
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
                eventEntity.AssignedMonsterName = monsterName;
                eventEntity.WindowCountOverride = HnmConfig.EffectiveWindowCount(monsterName);
                if (string.IsNullOrWhiteSpace(eventEntity.EventLocation))
                {
                    eventEntity.EventLocation = HnmConfig.ZoneFor(monsterName);
                }
            }
            eventEntity.DayNumber = request.DayNumber;
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
        }

        await _dbContext.SaveChangesAsync(cancellationToken);

        // Keep the recurring-board template in sync with the edited "repeat on ToD" choice
        // (mirrors create). Works for a custom monster too — recurrence keys on the
        // (case-insensitive) AssignedMonsterName the ToD records.
        var recurrenceMonster = eventEntity.AssignedMonsterName?.Trim();
        if (isHnm && !string.IsNullOrWhiteSpace(recurrenceMonster))
        {
            if (request.RepeatOnTod)
            {
                await HnmRecurringBoardService.UpsertAsync(_dbContext, eventEntity, null, appUser.Id, cancellationToken);
            }
            else
            {
                await HnmRecurringBoardService.DisableAsync(_dbContext, eventEntity.LinkshellId, recurrenceMonster, cancellationToken);
            }
        }

        return Ok(new { success = true });
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

    [HttpPost("events/{eventId:int}/end")]
    public async Task<IActionResult> EndEventAsync(int eventId, CancellationToken cancellationToken)
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

        // Manual Check In camps end through the camp path — a normal end pays HNM boards 0 DKP and
        // would discard the check-in credit. Hands the roster to the Event System page's attendance sections for review
        // (an officer's Post is what credits DKP) and recycles the board.
        if (string.Equals(eventEntity.AttendanceMode, HnmAttendanceModes.Wd, StringComparison.OrdinalIgnoreCase)
            && eventEntity.WdFinalizedAt is null)
        {
            var finalized = HttpContext.RequestServices.GetService(typeof(HnmCampReviewHandoffService))
                    is HnmCampReviewHandoffService handoff
                && await handoff.HandOffAndRecycleAsync(eventEntity.Id, cancellationToken);
            return Ok(new { success = true, finalized });
        }

        // Can't end while attendees are still pending confirmation (IsVerified == null).
        // Every pending member must be confirmed present or removed first. Outside
        // (account-less) signups can't be verified/credited, so they never block close.
        var pendingCount = eventEntity.AppUserEvents.Count(p => p.IsVerified == null && p.AppUserId != null);
        if (pendingCount > 0)
        {
            return BadRequest(new
            {
                error = $"Confirm or remove the {pendingCount} member(s) still pending in attendance before ending the event."
            });
        }

        var lootStructure = NormalizeLootStructure(eventEntity.Linkshell?.LootStructure ?? "Dkp");
        var isLootCouncil = lootStructure == "LootCouncil";
        var isHybrid = lootStructure == "Hybrid";
        var roundingStep = DkpRounding.StepFor(eventEntity.Linkshell?.DkpRoundingIncrement);

        // Wrap the ENTIRE close (materialize → save) so ANY exception — not just a
        // DbUpdateException at the save — returns actionable JSON instead of an HTML 500 that
        // the Activity mislabels as "your session may have expired". See the catch below.
        try
        {
        // The live event card can show party-board signups before they exist as
        // persisted participants. Materialize any missing rows before writing
        // EventHistory so channel signups are not dropped on close.
        await EventPartySignupService.MaterializeSignupsAsParticipantsAsync(_dbContext, eventEntity, cancellationToken);

        var endTimeUtc = DateTime.UtcNow;
        var history = new EventHistory
        {
            LinkshellId = eventEntity.LinkshellId,
            EventName = eventEntity.EventName,
            EventType = eventEntity.EventType,
            EventLocation = eventEntity.EventLocation,
            StartDate = eventEntity.StartTime?.Date,
            StartTime = eventEntity.StartTime,
            EndTime = endTimeUtc,
            CommencementStartTime = eventEntity.CommencementStartTime,
            Duration = eventEntity.CommencementStartTime.HasValue
                ? (endTimeUtc - eventEntity.CommencementStartTime.Value).TotalHours
                : eventEntity.Duration,
            DkpPerHour = eventEntity.DkpPerHour,
            EventDkp = eventEntity.EventDkp,
            Details = eventEntity.Details,
            CountsTowardActive = eventEntity.CountsTowardActive,
            TimeStamp = DateTime.UtcNow,
            AppUserEventHistories = new List<AppUserEventHistory>()
        };

        var linkshellMemberships = await _dbContext.AppUserLinkshells
            .Include(link => link.AppUser) // alt names, for resolving alt-won loot to the account
            .Where(link => link.LinkshellId == eventEntity.LinkshellId && link.AppUserId != null)
            .ToListAsync(cancellationToken);
        var membershipsByAppUserId = linkshellMemberships
            .Where(link => !string.IsNullOrWhiteSpace(link.AppUserId))
            .GroupBy(link => link.AppUserId!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        var participantsByCharacterName = eventEntity.AppUserEvents
            .Where(participation => !string.IsNullOrWhiteSpace(participation.CharacterName))
            .GroupBy(participation => participation.CharacterName!.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        // The event's type decides which DKP pool it pays INTO and which pool its loot is paid
        // OUT of — resolved once, not per participant. Mirrors the web EndEventCoreAsync.
        var eventPool = DkpPoolRef.Derived(eventEntity.EventType);
        // Windowed events (HNM Style / Claim/Kill) award DKP per WINDOW ATTENDED, not per hour of
        // presence: DkpPerHour is reused as DkpPerWindow when WindowCount > 1. Counted once up
        // front so the loop below can read from a dictionary.
        //
        // This block is a deliberate mirror of EventController.EndEventCoreAsync — including its
        // window-count expression, which is the CREDIT chain (WindowCountOverride ?? EventName).
        // Do not swap it for the display chain or for EventBreakPolicy.GatingWindowCount: the two
        // end-event paths must agree exactly, and a third variant is how they drifted apart in the
        // first place. This endpoint had no windowed branch at all, so a windowed camp closed from
        // the Activity's "End event" button was paid durationHours × DkpPerWindow — a figure with
        // no relation to windows attended, and the one place in the app where break state still
        // moved windowed DKP.
        var windowCount = eventEntity.WindowCountOverride
            ?? LinkshellManagerDiscordApp.Services.HnmConfig.GetWindowCount(eventEntity.EventName);
        var isWindowed = windowCount > 1;
        // AppUserEventId is nullable since snapshots outlive a cleared roster (see
        // AppUserEventWindow); orphans can't be credited through a participation. Same filter as
        // EventController.EndEventCoreAsync — keep the two identical.
        Dictionary<int, int> windowsAttendedByParticipationId = isWindowed
            ? await _dbContext.AppUserEventWindows
                .Where(w => w.EventAttendanceWindow!.EventId == eventEntity.Id && w.AppUserEventId != null)
                .GroupBy(w => w.AppUserEventId!.Value)
                .Select(g => new { ParticipationId = g.Key, Count = g.Count() })
                .ToDictionaryAsync(x => x.ParticipationId, x => x.Count, cancellationToken)
            : new Dictionary<int, int>();
        // One account = one history/ledger row. There is no DB uniqueness on AppUserEvent, so
        // an account can hold two participations for an event (e.g. a website join under the
        // main name + an addon post under an alt). Emitting a history row per participation
        // would violate the unique (EventHistoryId, AppUserId) index and throw at SaveChanges
        // — which the Activity surfaces as the misleading "your session may have expired".
        var creditedAppUserIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var participation in eventEntity.AppUserEvents)
        {
            // Outside (account-less) signups accrue NO DKP and write NO history —
            // they're board-only and cleared with the event below.
            if (string.IsNullOrWhiteSpace(participation.AppUserId))
            {
                continue;
            }
            // Collapse duplicate participations for the same account (first wins) so a second
            // row can't trip the unique history index at save.
            if (!creditedAppUserIds.Add(participation.AppUserId))
            {
                continue;
            }

            var durationHours = CalculateAccumulatedDurationHours(participation, endTimeUtc, eventEntity.CommencementStartTime);
            // Timed events pay for the ACTUAL time present, snapping the DKP value (not the
            // duration) to the linkshell's increment. Rounding the duration first floored
            // sub-quarter-hour events to 0h and paid present members 0 DKP.
            //
            // Windowed events pay per window attended instead — durationHours is still stamped
            // onto the history row below for display, but it must not reach the payout, or break
            // state (which CalculateAccumulatedDurationHours honours) would move windowed DKP.
            // Matches EventController.EndEventCoreAsync exactly.
            var eventDkp = isLootCouncil
                ? 0
                : EventAttendanceDkpCalculator.Compute(
                    isWindowed,
                    windowsAttendedByParticipationId.GetValueOrDefault(participation.Id, 0),
                    durationHours,
                    eventEntity.DkpPerHour ?? 0,
                    roundingStep);

            participation.Duration = durationHours;
            participation.EventDkp = eventDkp;

            history.AppUserEventHistories.Add(new AppUserEventHistory
            {
                AppUserId = participation.AppUserId,
                CharacterName = participation.CharacterName,
                JobName = participation.JobName,
                SubJobName = participation.SubJobName,
                JobType = participation.JobType,
                StartTime = participation.StartTime,
                Duration = durationHours,
                EventDkp = eventDkp,
                IsQuickJoin = participation.IsQuickJoin,
                IsVerified = participation.IsVerified,
                Proctor = participation.Proctor,
                ActiveCredit = eventEntity.CountsTowardActive
            });

            if (!isLootCouncil
                && membershipsByAppUserId.TryGetValue(participation.AppUserId, out var linkshellMembership))
            {
                await _dkpLedger.AppendAsync(
                    linkshellMembership,
                    "EventEarned",
                    eventDkp,
                    endTimeUtc,
                    eventPool,
                    new DkpEntryContext(
                        CharacterName: participation.CharacterName,
                        EventName: eventEntity.EventName,
                        EventType: eventEntity.EventType,
                        EventLocation: eventEntity.EventLocation,
                        EventStartTime: eventEntity.StartTime,
                        EventEndTime: endTimeUtc,
                        Details: "DKP earned from completed event.",
                        EventHistory: history),
                    cancellationToken);
            }
        }

        _dbContext.EventHistories.Add(history);
        if (!isLootCouncil)
        {
            foreach (var lootDetail in eventEntity.EventLootDetails.OrderBy(detail => detail.Id))
            {
                var rawValue = lootDetail.WinningDkpSpent.GetValueOrDefault();
                if (rawValue <= 0)
                {
                    continue;
                }

                var winnerMembership = ResolveLootWinnerMembership(
                    lootDetail.ItemWinner,
                    membershipsByAppUserId,
                    participantsByCharacterName,
                    linkshellMemberships);
                if (winnerMembership is null || string.IsNullOrWhiteSpace(winnerMembership.AppUserId))
                {
                    continue;
                }

                double amount;
                string lootDetailsText;
                if (isHybrid)
                {
                    var pct = Math.Clamp(rawValue, 0, 100);
                    // Hybrid takes a PERCENTAGE of the winner's balance — and a pool is a wallet,
                    // so it's a percentage of the POOL this event pays from, not of their grand
                    // total. With one pool that's LinkshellDkp, exactly as before. The writer's
                    // in-request view means the second item in an event sees the first item's
                    // debit, which is what the old read of the mutated LinkshellDkp did.
                    var poolId = await _dkpPools.ResolveAsync(eventEntity.LinkshellId, eventEntity.EventType, cancellationToken);
                    var currentBalance = Math.Max(0, await _dkpLedger.GetPoolBalanceAsync(winnerMembership, poolId, cancellationToken));
                    amount = -LootDkpCalculator.ComputeHybridDebit(currentBalance, pct, roundingStep);
                    lootDetailsText = $"Hybrid DKP spent ({pct}%) on loot: {lootDetail.ItemName ?? "Unknown item"}.";
                }
                else
                {
                    amount = -rawValue;
                    lootDetailsText = $"DKP spent on loot: {lootDetail.ItemName ?? "Unknown item"}.";
                }

                // Stamp the actual deducted amount so Loot History edits can refund precisely.
                lootDetail.ActualDeductedDkp = Math.Abs(amount);

                await _dkpLedger.AppendAsync(
                    winnerMembership,
                    "LootSpent",
                    amount,
                    endTimeUtc,
                    eventPool,
                    new DkpEntryContext(
                        CharacterName: winnerMembership.CharacterName,
                        EventName: eventEntity.EventName,
                        EventType: eventEntity.EventType,
                        EventLocation: eventEntity.EventLocation,
                        EventStartTime: eventEntity.StartTime,
                        EventEndTime: endTimeUtc,
                        ItemName: lootDetail.ItemName,
                        Details: lootDetailsText,
                        EventHistory: history,
                        // The web close has always stamped this; this one never did, which left
                        // Loot History unable to find the ledger row it had to reverse.
                        SourceEventLootDetailId: lootDetail.Id),
                    cancellationToken);
            }
        }
        // Preserve the loot rows post-close: re-parent each to the new EventHistory (and
        // detach the EventId before the Event is deleted) so they show in Loot History
        // instead of vanishing. EventLootDetail.EventId is SetNull, so the Event delete
        // below won't cascade-remove them. Mirrors the web EndEventCoreAsync — the Activity
        // path was deleting them, which is why live-event loot disappeared after ending.
        foreach (var lootDetail in eventEntity.EventLootDetails)
        {
            lootDetail.EventHistory = history;
            lootDetail.Event = null;
            lootDetail.EventId = null;
        }
        // Participations materialized earlier in THIS close (late board signups) are still in
        // the Added state with TEMPORARY keys — EF can't transition those to Deleted
        // (InvalidOperationException: "AppUserEvent.Id has a temporary value"). Detach them
        // (they were never persisted; their history + DKP were already recorded above) and
        // delete only the rows that actually exist in the database.
        foreach (var participation in eventEntity.AppUserEvents.ToList())
        {
            var entry = _dbContext.Entry(participation);
            if (entry.State == EntityState.Added)
            {
                // Detach AND drop from the navigation so EF's change detector can't re-track it
                // (and re-attempt the temp-key delete) when the event is removed below.
                entry.State = EntityState.Detached;
                eventEntity.AppUserEvents.Remove(participation);
            }
            else
            {
                _dbContext.AppUserEvents.Remove(participation);
            }
        }
        _dbContext.Events.Remove(eventEntity);

            // A financial close must not be half-committed: use None so a flaky Discord-iframe
            // request abort (the client cancellation token) can't cancel the save mid-write.
            await _dbContext.SaveChangesAsync(CancellationToken.None);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Return actionable JSON instead of letting it bubble to the generic HTML error
            // page — which the SPA can only read back as the misleading "your session may have
            // expired" message. Broad on purpose: a narrow catch(DbUpdateException) let
            // non-DbUpdate exceptions (EF tracking, etc.) escape. Don't echo the raw EF/Npgsql
            // message to the client (it leaks schema/constraint/SQL detail); log the full
            // exception with a correlation id and surface only that id for support to trace.
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

        // A counting event just closed → recompute each member's Active/Inactive status
        // from attendance (no-op when tracking is disabled for the linkshell). This runs
        // AFTER the close is committed, so a failure here must NOT fail the request — the
        // event is already ended; just log it and return success.
        try
        {
            await _memberActivity.ApplyComputedStatusAsync(eventEntity.LinkshellId, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Event {EventId} ended, but recomputing member activity status for linkshell {LinkshellId} failed.", eventId, eventEntity.LinkshellId);
        }

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
