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

        // HNM events are created automatically by the in-game addon (from member
        // ToD captures), never by hand -- reject manual creation of the "HNM" type.
        if (string.Equals((request.EventType ?? string.Empty).Trim(), "HNM", StringComparison.OrdinalIgnoreCase))
        {
            return BadRequest(new
            {
                error = "HNM events are created automatically by the in-game addon and can't be added manually."
            });
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

        if (request.PartySetupId.HasValue &&
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

        await _dbContext.SaveChangesAsync(cancellationToken);
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

        // HNM events run on the in-game addon's window timing — start events
        // for them must come from the addon (POST /api/addon/events/{id}/start),
        // never the Activity / web app, otherwise the post-by-window roster
        // workflow gets out of sync. Reject up front with a clear message the
        // UI can surface.
        if (string.Equals((eventEntity.EventType ?? string.Empty).Trim(), "HNM", StringComparison.OrdinalIgnoreCase))
        {
            return BadRequest(new
            {
                error = "HNM events are started with the in-game addon. Use the Att launcher to start this event."
            });
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
        var ledgerEntries = new List<DkpLedgerEntry>();
        var nextSequenceByAppUserId = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (var participation in eventEntity.AppUserEvents)
        {
            // Outside (account-less) signups accrue NO DKP and write NO history —
            // they're board-only and cleared with the event below.
            if (string.IsNullOrWhiteSpace(participation.AppUserId))
            {
                continue;
            }

            var durationHours = CalculateAccumulatedDurationHours(participation, endTimeUtc, eventEntity.CommencementStartTime);
            // Pay DKP for the ACTUAL time present, snapping the DKP value (not the
            // duration) to the linkshell's increment. Rounding the duration first
            // floored sub-quarter-hour events to 0h and paid present members 0 DKP.
            var eventDkp = isLootCouncil ? 0 : DkpRounding.Round(durationHours * (eventEntity.DkpPerHour ?? 0), roundingStep);

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

            if (!string.IsNullOrWhiteSpace(participation.AppUserId) &&
                membershipsByAppUserId.TryGetValue(participation.AppUserId, out var linkshellMembership))
            {
                if (!isLootCouncil)
                {
                    linkshellMembership.LinkshellDkp = (linkshellMembership.LinkshellDkp ?? 0) + eventDkp;
                }
                nextSequenceByAppUserId[participation.AppUserId] = 2;
            }

            if (!isLootCouncil && !string.IsNullOrWhiteSpace(participation.AppUserId))
            {
                ledgerEntries.Add(new DkpLedgerEntry
                {
                    AppUserId = participation.AppUserId,
                    EventHistory = history,
                    LinkshellId = eventEntity.LinkshellId,
                    EntryType = "EventEarned",
                    Amount = eventDkp,
                    Sequence = 1,
                    OccurredAt = endTimeUtc,
                    CharacterName = participation.CharacterName,
                    EventName = eventEntity.EventName,
                    EventType = eventEntity.EventType,
                    EventLocation = eventEntity.EventLocation,
                    EventStartTime = eventEntity.StartTime,
                    EventEndTime = endTimeUtc,
                    Details = "DKP earned from completed event."
                });
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
                    var currentBalance = Math.Max(0, winnerMembership.LinkshellDkp ?? 0);
                    amount = -LootDkpCalculator.ComputeHybridDebit(currentBalance, pct, roundingStep);
                    lootDetailsText = $"Hybrid DKP spent ({pct}%) on loot: {lootDetail.ItemName ?? "Unknown item"}.";
                }
                else
                {
                    amount = -rawValue;
                    lootDetailsText = $"DKP spent on loot: {lootDetail.ItemName ?? "Unknown item"}.";
                }

                winnerMembership.LinkshellDkp = (winnerMembership.LinkshellDkp ?? 0) + amount;
                // Stamp the actual deducted amount so Loot History edits can refund precisely.
                lootDetail.ActualDeductedDkp = Math.Abs(amount);

                var currentSequence = nextSequenceByAppUserId.GetValueOrDefault(winnerMembership.AppUserId, 2);
                ledgerEntries.Add(new DkpLedgerEntry
                {
                    AppUserId = winnerMembership.AppUserId,
                    EventHistory = history,
                    LinkshellId = eventEntity.LinkshellId,
                    EntryType = "LootSpent",
                    Amount = amount,
                    Sequence = currentSequence,
                    OccurredAt = endTimeUtc,
                    CharacterName = winnerMembership.CharacterName,
                    EventName = eventEntity.EventName,
                    EventType = eventEntity.EventType,
                    EventLocation = eventEntity.EventLocation,
                    EventStartTime = eventEntity.StartTime,
                    EventEndTime = endTimeUtc,
                    ItemName = lootDetail.ItemName,
                    Details = lootDetailsText
                });
                nextSequenceByAppUserId[winnerMembership.AppUserId] = currentSequence + 1;
            }
        }

        _dbContext.DkpLedgerEntries.AddRange(ledgerEntries);
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
        _dbContext.AppUserEvents.RemoveRange(eventEntity.AppUserEvents);
        _dbContext.Events.Remove(eventEntity);

        await _dbContext.SaveChangesAsync(cancellationToken);

        // A counting event just closed → recompute each member's Active/Inactive
        // status from attendance (no-op when tracking is disabled for the linkshell).
        await _memberActivity.ApplyComputedStatusAsync(eventEntity.LinkshellId, cancellationToken);

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
}
