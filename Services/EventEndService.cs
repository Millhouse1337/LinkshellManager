using LinkshellManagerDiscordApp.Controllers;
using LinkshellManagerDiscordApp.Data;
using LinkshellManagerDiscordApp.Models;
using LinkshellManagerDiscordApp.Utils;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Logging;

namespace LinkshellManagerDiscordApp.Services;

// The one generic end-event close, run by both End Event buttons (the app's,
// ActivityDataController.EventsLifecycle, and the addon's, AddonApiController.AddonEvents), each
// with its own EndEventOptions: credits DKP, writes the
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
        bool HasLootDeductions)
    {
        // Above 0 when RequireNoPendingAttendees refused the close: nothing was written or saved,
        // and this many members are still waiting to be confirmed present or removed.
        public int PendingAttendeeCount { get; init; }
        public bool Refused => PendingAttendeeCount > 0;
    }

    // The rules the two End Event buttons (the app's and the addon's) have always disagreed on.
    // They were two hand-written copies of this method until P3.5; now both call this one and say
    // which way each rule goes, so a difference can only exist on purpose and in one place.
    internal sealed record EndEventOptions
    {
        // Refuse the close (and save nothing) while a member with an account is still pending
        // (IsVerified == null): an officer must confirm them present or remove them first.
        // Checked before late board sign-ups are brought in, so those never block the close.
        // Outside (account-less) sign-ups can't be confirmed, so they never block it either.
        public bool RequireNoPendingAttendees { get; init; }

        // A Loot Council linkshell doesn't spend DKP on loot, so the close pays nobody: every
        // history row says 0 DKP and no EventEarned ledger row is written. Loot is never charged
        // on Loot Council either way.
        public bool LootCouncilPaysNoDkp { get; init; }

        // Outside (account-less) sign-ups are board-only: they get no history row and are simply
        // cleared with the event. Without this they are archived (with no ledger row, as there
        // is no balance to pay into).
        public bool SkipAccountlessParticipants { get; init; }

        // The addon's End Event: the rules this method has always run with.
        public static EndEventOptions Addon { get; } = new();

        // The app's End Event: the rules the Activity's own copy of the close always applied.
        public static EndEventOptions Activity { get; } = new()
        {
            RequireNoPendingAttendees = true,
            LootCouncilPaysNoDkp = true,
            SkipAccountlessParticipants = true,
        };
    }

    // Members (with an account) still pending confirmation -- what RequireNoPendingAttendees
    // refuses on.
    internal static int CountPendingAttendees(Event eventEntity) =>
        eventEntity.AppUserEvents.Count(p => p.IsVerified == null && p.AppUserId != null);

    // Caller is responsible for loading the Event with its AppUserEvents, EventLootDetails and
    // Linkshell included, and for verifying auth (linkshell membership / management permission)
    // before calling. Writes the EventHistory + DkpLedgerEntry rows, removes the related
    // AppUserEvents / EventLootDetails / Event, and saves. `options` defaults to the addon's rules.
    internal static async Task<EndEventResult> EndEventCoreAsync(
        ApplicationDbContext dbContext,
        DkpLedgerWriter dkpLedger,
        DkpPoolResolver dkpPools,
        Event eventEntity,
        EndEventOptions? options = null)
    {
        options ??= EndEventOptions.Addon;

        // Before anything is read or written, so a refused close leaves the event untouched.
        if (options.RequireNoPendingAttendees)
        {
            var pendingCount = CountPendingAttendees(eventEntity);
            if (pendingCount > 0)
            {
                return new EndEventResult(default, Array.Empty<EndEventParticipantSummary>(), 0, 0, false)
                {
                    PendingAttendeeCount = pendingCount
                };
            }
        }

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
        // looser than whichever chain a credit path consults. Both End Event buttons run this one
        // close now, so do not copy this expression elsewhere -- copies are how they drifted apart.
        var windowCount = eventEntity.WindowCountOverride
            ?? HnmConfig.GetWindowCount(eventEntity.EventName);
        var isWindowed = windowCount > 1;
        // Counted per account, not per participation row (see WindowsAttendedTally). This path is
        // the Claim/Kill-style windowed events, which never clear their roster. The HNM camps that
        // do go through HnmStandardCampFinalizer, which folds on the denormalized AppUserId instead.
        var windowsAttendedTally = isWindowed
            ? await WindowsAttendedTally.LoadAsync(dbContext, eventEntity.Id, CancellationToken.None)
            : WindowsAttendedTally.Empty;
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
            StartTime = eventEntity.StartTime,
            EndTime = endTimeUtc,
            CommencementStartTime = eventEntity.CommencementStartTime,
            Duration = eventEntity.CommencementStartTime.HasValue ? (endTimeUtc - eventEntity.CommencementStartTime.Value).TotalHours : eventEntity.Duration,
            DkpPerHour = eventEntity.DkpPerHour,
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
        // Every name a member could have won loot under, resolved by the one shared rule.
        var rosterByName = CharacterResolver.ForMemberships(linkshellMemberships);
        var participantsByCharacterName = eventEntity.AppUserEvents
            .Where(participation => !string.IsNullOrWhiteSpace(participation.CharacterName))
            .GroupBy(participation => participation.CharacterName!.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        // One history/ledger row per account. The database now holds one participation per
        // account per event (OneParticipationPerEvent), so this is only a guard: a second history
        // row would violate the unique (EventHistoryId, AppUserId) index and throw at save.
        // Account-less rows (null AppUserId) are outside that filtered index, so they're exempt
        // from this dedup.
        var creditedAppUserIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Read up front (it used to be read after the payout loop) because Loot Council also
        // decides what the loop pays when LootCouncilPaysNoDkp is on. Loot Council charges no loot,
        // Hybrid charges a percentage of the winner's pool balance, everything else the stated DKP.
        var lootStructure = ActivityDataController.NormalizeLootStructure(
            eventEntity.Linkshell?.LootStructure
            ?? await dbContext.Linkshells
                .Where(l => l.Id == eventEntity.LinkshellId)
                .Select(l => l.LootStructure)
                .FirstOrDefaultAsync()
            ?? "Dkp");
        var isLootCouncil = lootStructure == "LootCouncil";
        var paysNoDkp = isLootCouncil && options.LootCouncilPaysNoDkp;

        foreach (var participation in eventEntity.AppUserEvents)
        {
            if (string.IsNullOrWhiteSpace(participation.AppUserId) && options.SkipAccountlessParticipants)
            {
                continue;
            }
            if (!string.IsNullOrWhiteSpace(participation.AppUserId) && !creditedAppUserIds.Add(participation.AppUserId))
            {
                continue;
            }
            var durationHours = CalculateAccumulatedDurationHours(participation, endTimeUtc, eventEntity.CommencementStartTime);
            int? windowsAttended = isWindowed
                ? windowsAttendedTally.For(participation)
                : (int?)null;
            // Windowed pays per window attended; timed pays for the ACTUAL time present; a member
            // an officer marked Absent gets 0 and no Active credit -- see
            // EventAttendanceDkpCalculator.ForParticipant.
            // (LootCouncilPaysNoDkp then zeroes it on the app's close.)
            var credit = EventAttendanceDkpCalculator.ForParticipant(
                participation.IsVerified,
                eventEntity.CountsTowardActive,
                isWindowed,
                windowsAttended ?? 0,
                durationHours,
                eventEntity.DkpPerHour ?? 0,
                DkpRounding.StepFor(eventEntity.Linkshell?.DkpRoundingIncrement));
            var eventDkp = paysNoDkp ? 0 : credit.Dkp;

            participation.Duration = durationHours;

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
                Duration = durationHours,
                EventDkp = eventDkp,
                ActiveCredit = credit.ActiveCredit,
                // Null on a timed event. On a windowed one this is the numerator of the DKP row
                // above, so the closed event can explain its own payout.
                WindowsAttended = windowsAttended
            });

            if (!paysNoDkp &&
                !string.IsNullOrWhiteSpace(participation.AppUserId) &&
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
                        Details: credit.LedgerDetails,
                        EventHistory: history),
                    CancellationToken.None);
            }
        }

        dbContext.EventHistories.Add(history);
        var hasLootDeductions = false;
        foreach (var lootDetail in eventEntity.EventLootDetails.OrderBy(detail => detail.Id))
        {
            if (isLootCouncil || lootDetail.WinningDkpSpent.GetValueOrDefault() <= 0)
            {
                continue;
            }

            // Already charged -- loot added to a past event, or before loot on a live event waited
            // for the close (ManualLootService) -- so without this the close would take the same
            // DKP off the same winner a second time. Null on everything still owed its debit here.
            if (lootDetail.DkpDebitedAt is not null)
            {
                continue;
            }

            var winnerMembership = ResolveLootWinnerMembership(
                lootDetail,
                membershipsByAppUserId,
                participantsByCharacterName,
                rosterByName);
            if (winnerMembership is null || string.IsNullOrWhiteSpace(winnerMembership.AppUserId))
            {
                continue;
            }

            // The pool the row was entered against, if any; otherwise the one the event earned
            // into -- you buy this event's loot with this event's DKP.
            var lootPool = lootDetail.DkpPoolId is int pinned ? DkpPoolRef.Pinned(pinned) : eventPool;
            double amount;
            string lootDetailsText;
            if (lootStructure == "Hybrid")
            {
                var pct = Math.Clamp(lootDetail.WinningDkpSpent.GetValueOrDefault(), 0, 100);
                var poolId = lootDetail.DkpPoolId
                    ?? await dkpPools.ResolveAsync(eventEntity.LinkshellId, eventEntity.EventType, CancellationToken.None);
                var balance = Math.Max(0, await dkpLedger.GetPoolBalanceAsync(winnerMembership, poolId, CancellationToken.None));
                amount = -LootDkpCalculator.ComputeHybridDebit(
                    balance, pct, DkpRounding.StepFor(eventEntity.Linkshell?.DkpRoundingIncrement));
                lootDetailsText = $"Hybrid DKP spent ({pct}%) on loot: {lootDetail.ItemName ?? "Unknown item"}.";
            }
            else
            {
                amount = -lootDetail.WinningDkpSpent.GetValueOrDefault();
                lootDetailsText = $"DKP spent on loot: {lootDetail.ItemName ?? "Unknown item"}.";
            }

            // Stamp the actual deducted amount onto the loot row so future
            // Loot History edits can refund precisely.
            lootDetail.ActualDeductedDkp = Math.Abs(amount);
            lootDetail.DkpDebitedAt = endTimeUtc;

            await dkpLedger.AppendAsync(
                winnerMembership,
                "LootSpent",
                amount,
                endTimeUtc,
                lootPool,
                new DkpEntryContext(
                    CharacterName: winnerMembership.CharacterName,
                    EventName: eventEntity.EventName,
                    EventType: eventEntity.EventType,
                    EventLocation: eventEntity.EventLocation,
                    ItemName: lootDetail.ItemName,
                    Details: lootDetailsText,
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
        // actually exist.
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
        // fail the request. Status re-derives on the
        // next close / roster load.
        try
        {
            await new MemberActivityService(dbContext).ApplyComputedStatusAsync(eventEntity.LinkshellId, CancellationToken.None);
        }
        catch (Exception ex)
        {
            // Swallow — the event is ended; activity status will recompute next time. But LOG
            // it (resolving the app logger from the DbContext, since this is a static helper)
            // so a persistently-failing recompute isn't invisible.
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

    // The winner's membership: by the row's ItemWinnerAppUserId when it has one, else by name.
    // `rosterByName` is CharacterResolver.ForMemberships over the linkshell's memberships, built
    // once per close rather than once per loot row.
    internal static AppUserLinkshell? ResolveLootWinnerMembership(
        EventLootDetail loot,
        IReadOnlyDictionary<string, AppUserLinkshell> membershipsByAppUserId,
        IReadOnlyDictionary<string, AppUserEvent> participantsByCharacterName,
        CharacterResolver<AppUserLinkshell> rosterByName)
    {
        // A recorded account is the answer, even if a different member now has that name. If that
        // account has left the linkshell there is nobody to charge -- never fall back to a name.
        if (!string.IsNullOrWhiteSpace(loot.ItemWinnerAppUserId))
        {
            return membershipsByAppUserId.TryGetValue(loot.ItemWinnerAppUserId, out var recorded) ? recorded : null;
        }

        return ResolveLootWinnerMembership(
            loot.ItemWinner, membershipsByAppUserId, participantsByCharacterName, rosterByName);
    }

    internal static AppUserLinkshell? ResolveLootWinnerMembership(
        string? itemWinner,
        IReadOnlyDictionary<string, AppUserLinkshell> membershipsByAppUserId,
        IReadOnlyDictionary<string, AppUserEvent> participantsByCharacterName,
        CharacterResolver<AppUserLinkshell> rosterByName)
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

        // Otherwise any of the member's names (alts share the main's account, so loot won on an
        // alt deducts from the one account balance). The shared rule -- see CharacterResolver.
        // This used to skip the account's own CharacterName, so loot won under it was never
        // charged here while LootEditService still refunded it on an edit.
        return rosterByName.Resolve(normalizedWinner);
    }

    internal static string? NormalizeLookupKey(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }
}
