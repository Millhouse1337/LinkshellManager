using LinkshellManagerDiscordApp.Data;
using LinkshellManagerDiscordApp.Models;
using LinkshellManagerDiscordApp.Utils;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Logging;

namespace LinkshellManagerDiscordApp.Services;

// The addon's generic end-event path (AddonApiController.AddonEvents): credits DKP, writes the
// EventHistory + DkpLedgerEntry rows and deletes the live event. Moved out of the old website's
// EventController so it outlives that controller. HNM camps never come here — they end through
// HnmCampReviewHandoffService (see HnmCampReviewHandoffService.EndsThroughCampPath).
public static class EventEndService
{
    internal sealed record EndEventParticipantSummary(
        string? CharacterName,
        string? JobName,
        string? SubJobName,
        double? DurationHours,
        double? DkpEarned,
        int? WindowsAttended);

    internal sealed record EndEventResult(
        DateTime EndTimeUtc,
        IReadOnlyList<EndEventParticipantSummary> Participants,
        int WindowCount,
        int EventHistoryId,
        bool HasLootDeductions);

    // Caller is responsible for loading the Event with its AppUserEvents and EventLootDetails
    // included, and for verifying auth (linkshell membership / management permission) before
    // calling. Writes the EventHistory + DkpLedgerEntry rows, removes the related AppUserEvents /
    // EventLootDetails / Event, and saves.
    internal static async Task<EndEventResult> EndEventCoreAsync(
        ApplicationDbContext dbContext, DkpLedgerWriter dkpLedger, DkpPoolResolver dkpPools, Event eventEntity)
    {
        var endTimeUtc = DateTime.UtcNow;
        var participantSummaries = new List<EndEventParticipantSummary>();

        // The event's type decides which DKP pool it pays INTO and which pool its loot is paid
        // OUT of — resolved once here, not per participant. An event has one type, so a Sky event
        // credits the Sky pool and its loot is bought with Sky-pool DKP. Unmapped/custom/null
        // types land in the linkshell's default pool, which is where everything lives until an
        // officer partitions their event types.
        var eventPool = DkpPoolRef.Derived(eventEntity.EventType);

        // Backfill any party-board signups that never became AppUserEvents so the
        // closed event history matches the live roster and loot UI.
        await EventPartySignupService.MaterializeSignupsAsParticipantsAsync(dbContext, eventEntity, CancellationToken.None);

        // Windowed events (HNM Style / Claim/Kill) award DKP per window attended,
        // not per hour of presence: the DkpPerHour column is reused as
        // DkpPerWindow when WindowCount > 1, and the per-participation total is
        // (windowsAttended * dkpPerWindow). Count windows attended once up front
        // so the per-participation loop below can read from a dictionary.
        //
        // NOTE there are TWO window-count chains in this codebase and this is the CREDIT one.
        // The other ("display") chain is DiscordEventMessageBuilder.EffectiveWindowCount, which
        // also consults AssignedMonsterName and DefaultWindowCadence — it feeds the Discord board,
        // the Activity DTO and the addon event list. They can disagree for an event whose
        // EventName differs from its AssignedMonsterName. Unifying them would move real payouts,
        // so it is deliberately NOT done here; the divergence is usually invisible because all
        // three creation paths stamp WindowCountOverride, which short-circuits both chains
        // identically. Services/EventBreakPolicy takes the MAX of the two so its gate can never be
        // looser than whichever chain a credit path consults. Keep this expression byte-identical
        // to ActivityDataController.EndEventAsync's — a third variant is how they drifted apart.
        var windowCount = eventEntity.WindowCountOverride
            ?? HnmConfig.GetWindowCount(eventEntity.EventName);
        var isWindowed = windowCount > 1;
        // AppUserEventId is nullable since snapshots outlive a cleared roster (see
        // AppUserEventWindow). Orphaned rows can't be credited through a participation, so they're
        // filtered out here — this path is the Claim/Kill-style windowed events, which never clear
        // their roster. The HNM camps that do go through HnmStandardCampFinalizer, which folds on
        // the denormalized AppUserId instead.
        Dictionary<int, int> windowsAttendedByParticipationId = isWindowed
            ? await dbContext.AppUserEventWindows
                .Where(w => w.EventAttendanceWindow!.EventId == eventEntity.Id && w.AppUserEventId != null)
                .GroupBy(w => w.AppUserEventId!.Value)
                .Select(g => new { ParticipationId = g.Key, Count = g.Count() })
                .ToDictionaryAsync(x => x.ParticipationId, x => x.Count)
            : new Dictionary<int, int>();
        var history = new EventHistory
        {
            LinkshellId = eventEntity.LinkshellId,
            EventName = eventEntity.EventName,
            EventType = eventEntity.EventType,
            EventLocation = eventEntity.EventLocation,
            // Resolved to a TEMPLATE before the Event row (and any per-event snapshot hanging off
            // it) is deleted below. This is the only record of the camp's board that outlives it,
            // and it is what lets the next pop come back with the same setup attached.
            PartySetupId = await PartySetupInheritance.ResolveTemplateIdAsync(
                dbContext, eventEntity, CancellationToken.None),
            StartDate = eventEntity.StartTime?.Date,
            StartTime = eventEntity.StartTime,
            EndTime = endTimeUtc,
            CommencementStartTime = eventEntity.CommencementStartTime,
            Duration = eventEntity.CommencementStartTime.HasValue ? (endTimeUtc - eventEntity.CommencementStartTime.Value).TotalHours : eventEntity.Duration,
            DkpPerHour = eventEntity.DkpPerHour,
            EventDkp = eventEntity.EventDkp,
            Details = eventEntity.Details,
            CountsTowardActive = eventEntity.CountsTowardActive,
            TimeStamp = DateTime.UtcNow,
            AppUserEventHistories = new List<AppUserEventHistory>()
        };

        var linkshellMemberships = await dbContext.AppUserLinkshells
            .Include(link => link.AppUser) // alt names, for resolving alt-won loot to the account
            .Where(link => link.LinkshellId == eventEntity.LinkshellId && link.AppUserId != null)
            .ToListAsync();
        var membershipsByAppUserId = linkshellMemberships
            .Where(link => !string.IsNullOrWhiteSpace(link.AppUserId))
            .GroupBy(link => link.AppUserId!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        var participantsByCharacterName = eventEntity.AppUserEvents
            .Where(participation => !string.IsNullOrWhiteSpace(participation.CharacterName))
            .GroupBy(participation => participation.CharacterName!.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        // One history/ledger row per account. There is no DB uniqueness on AppUserEvent, so an
        // account can hold two participations for an event (e.g. a website join + an addon post
        // under an alt); a second history row would violate the unique (EventHistoryId,
        // AppUserId) index and throw at save. Account-less rows (null AppUserId) are outside
        // that filtered index, so they're exempt from this dedup.
        var creditedAppUserIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var participation in eventEntity.AppUserEvents)
        {
            if (!string.IsNullOrWhiteSpace(participation.AppUserId) && !creditedAppUserIds.Add(participation.AppUserId))
            {
                continue;
            }
            var durationHours = CalculateAccumulatedDurationHours(participation, endTimeUtc, eventEntity.CommencementStartTime);
            int? windowsAttended = isWindowed
                ? windowsAttendedByParticipationId.GetValueOrDefault(participation.Id, 0)
                : (int?)null;
            // Windowed pays per window attended; timed pays for the ACTUAL time present. Shared
            // with ActivityDataController.EndEventAsync so the two end-event paths can't drift —
            // see EventAttendanceDkpCalculator.
            var eventDkp = EventAttendanceDkpCalculator.Compute(
                isWindowed,
                windowsAttended ?? 0,
                durationHours,
                eventEntity.DkpPerHour ?? 0,
                DkpRounding.StepFor(eventEntity.Linkshell?.DkpRoundingIncrement));

            participation.Duration = durationHours;
            participation.EventDkp = eventDkp;

            participantSummaries.Add(new EndEventParticipantSummary(
                participation.CharacterName,
                participation.JobName,
                participation.SubJobName,
                durationHours,
                eventDkp,
                windowsAttended));

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
                ActiveCredit = eventEntity.CountsTowardActive,
                // Null on a timed event. On a windowed one this is the numerator of the DKP row
                // above, so the closed event can explain its own payout.
                WindowsAttended = windowsAttended
            });

            if (!string.IsNullOrWhiteSpace(participation.AppUserId) &&
                membershipsByAppUserId.TryGetValue(participation.AppUserId, out var linkshellMembership))
            {
                await dkpLedger.AppendAsync(
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
                    CancellationToken.None);
            }
        }

        dbContext.EventHistories.Add(history);
        var hasLootDeductions = false;
        foreach (var lootDetail in eventEntity.EventLootDetails.OrderBy(detail => detail.Id))
        {
            if (lootDetail.WinningDkpSpent.GetValueOrDefault() <= 0)
            {
                continue;
            }

            // Loot added by hand from the Loot System is charged the moment it is entered, and it
            // can be attached to a LIVE event -- so without this the close would take the same DKP
            // off the same winner a second time. Null on everything added through the event flow,
            // which is still owed its debit here.
            if (lootDetail.DkpDebitedAt is not null)
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

            var amount = -lootDetail.WinningDkpSpent.GetValueOrDefault();

            // Stamp the actual deducted amount onto the loot row so future
            // Loot History edits can refund precisely (matches the ToD
            // ActualDeductedDkp pattern in HelpersTods.AdjustTodLootDkpAsync).
            lootDetail.ActualDeductedDkp = Math.Abs(amount);

            // Same pool the event earned into: you buy this event's loot with this event's DKP.
            await dkpLedger.AppendAsync(
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
                    Details: $"DKP spent on loot: {lootDetail.ItemName ?? "Unknown item"}.",
                    EventHistory: history,
                    SourceEventLootDetailId: lootDetail.Id),
                CancellationToken.None,
                // Affordability was enforced when the loot was AWARDED (LootDkpGuard). Event close
                // settles the whole roster in one batch — blocking here would leave the event
                // un-closable over a single member.
                DkpOverdraft.Allow);
            hasLootDeductions = true;
        }

        // Preserve EventLootDetails post-close so officers can edit them via
        // Loot History. Re-parent each row to the new EventHistory and detach
        // the EventId before the parent Event is deleted below. The
        // EventLootDetail.EventId FK was changed to SetNull in
        // AddLootHistoryAudit, so the Event delete won't cascade-remove them.
        foreach (var lootDetail in eventEntity.EventLootDetails)
        {
            lootDetail.EventHistory = history;
            lootDetail.Event = null;
            lootDetail.EventId = null;
        }
        // Same move for the camp's posted attendance windows, and for the same reason: the Event
        // FK is Cascade, so the delete below would take every window — and through them every
        // AppUserEventWindow roster row — with it. Clearing EventId first unhooks them from the
        // doomed Event; EventHistory then owns them.
        //
        // This is the ONLY surviving record of which windows a camp posted and who was scanned in
        // each. Nothing re-derives it: the live rows are gone the moment this method returns.
        // Deliberately unconditional rather than gated on isWindowed — if a row exists, an officer
        // posted it, and the window-count heuristics that decide isWindowed are not a good enough
        // reason to throw one away.
        var archivedWindows = await dbContext.EventAttendanceWindows
            .Where(window => window.EventId == eventEntity.Id)
            .ToListAsync();
        foreach (var window in archivedWindows)
        {
            window.EventHistory = history;
            window.Event = null;
            window.EventId = null;
        }
        // Participations materialized earlier in THIS close (late board signups) are still in
        // the Added state with TEMPORARY keys — EF can't transition those to Deleted. Detach
        // them (never persisted; history + DKP already recorded) and delete only the rows that
        // actually exist. Mirrors the Activity EndEventAsync guard.
        foreach (var participation in eventEntity.AppUserEvents.ToList())
        {
            var entry = dbContext.Entry(participation);
            if (entry.State == EntityState.Added)
            {
                // Detach AND drop from the navigation so EF's change detector can't re-track it
                // (and re-attempt the temp-key delete) when the event is removed below.
                entry.State = EntityState.Detached;
                eventEntity.AppUserEvents.Remove(participation);
            }
            else
            {
                dbContext.AppUserEvents.Remove(participation);
            }
        }
        dbContext.Events.Remove(eventEntity);
        await dbContext.SaveChangesAsync();

        // A counting event just closed → recompute each member's Active/Inactive
        // status from attendance (no-op when the linkshell hasn't enabled tracking).
        // Best-effort: the close is already committed above, so a recompute failure must NOT
        // fail the request (mirrors the Activity EndEventAsync guard). Status re-derives on the
        // next close / roster load.
        try
        {
            await new MemberActivityService(dbContext).ApplyComputedStatusAsync(eventEntity.LinkshellId, CancellationToken.None);
        }
        catch (Exception ex)
        {
            // Swallow — the event is ended; activity status will recompute next time. But LOG
            // it (resolving the app logger from the DbContext, since this is a static helper)
            // so a persistently-failing recompute isn't invisible. Mirrors the Activity twin.
            dbContext.GetService<ILoggerFactory>()?
                .CreateLogger(typeof(EventEndService).FullName!)
                .LogError(ex, "Active/Inactive recompute failed after ending event for linkshell {LinkshellId}.",
                    eventEntity.LinkshellId);
        }

        return new EndEventResult(endTimeUtc, participantSummaries, windowCount, history.Id, hasLootDeductions);
    }

    internal static double CalculateAccumulatedDurationHours(AppUserEvent participation, DateTime referenceUtc, DateTime? eventStartUtc)
    {
        var accumulatedHours = Math.Max(0, participation.Duration ?? 0);
        if (participation.IsOnBreak == true)
        {
            return accumulatedHours;
        }

        var segmentStart = participation.ResumeTime ?? participation.StartTime ?? eventStartUtc;
        if (!segmentStart.HasValue)
        {
            return accumulatedHours;
        }

        var segmentHours = Math.Max(0, (referenceUtc - segmentStart.Value).TotalHours);
        return accumulatedHours + segmentHours;
    }

    internal static AppUserLinkshell? ResolveLootWinnerMembership(
        string? itemWinner,
        IReadOnlyDictionary<string, AppUserLinkshell> membershipsByAppUserId,
        IReadOnlyDictionary<string, AppUserEvent> participantsByCharacterName,
        IEnumerable<AppUserLinkshell> linkshellMemberships)
    {
        var normalizedWinner = NormalizeLookupKey(itemWinner);
        if (normalizedWinner is null)
        {
            return null;
        }

        if (participantsByCharacterName.TryGetValue(normalizedWinner, out var participation) &&
            !string.IsNullOrWhiteSpace(participation.AppUserId) &&
            membershipsByAppUserId.TryGetValue(participation.AppUserId, out var participantMembership))
        {
            return participantMembership;
        }

        // Match the winner by the membership's MAIN character OR either ALT (alts
        // share the main's account, so loot won on an alt deducts from the one
        // account balance). Requires the memberships to be loaded WITH AppUser.
        return linkshellMemberships.FirstOrDefault(link =>
            string.Equals(NormalizeLookupKey(link.CharacterName), normalizedWinner, StringComparison.OrdinalIgnoreCase)
            || string.Equals(NormalizeLookupKey(link.AppUser?.AltCharacterName1), normalizedWinner, StringComparison.OrdinalIgnoreCase)
            || string.Equals(NormalizeLookupKey(link.AppUser?.AltCharacterName2), normalizedWinner, StringComparison.OrdinalIgnoreCase));
    }

    internal static string? NormalizeLookupKey(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }
}
