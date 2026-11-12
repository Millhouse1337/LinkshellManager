using LinkshellManagerDiscordApp.Data;
using LinkshellManagerDiscordApp.Models;
using Microsoft.EntityFrameworkCore;

namespace LinkshellManagerDiscordApp.Services;

// Edits a CLOSED event (EventHistory) and keeps DKP consistent. Unlike loot
// edits (which append refund/spend ledger rows), a finished event's per-member
// "EventEarned" ledger row is the canonical record of what that member earned —
// so we mutate it in place (Amount + Details), adjust the member's LinkshellDkp
// by the delta, and update the AppUserEventHistory.EventDkp. This keeps BOTH the
// spendable balance and the lifetime Total (which sums ledger amounts by sign)
// correct without misclassifying a downward edit as a "spend".
//
// Only DkpPerHour drives earnings (each member earned their own duration × rate),
// so a rate change rescales every member; the event-level Duration field is
// display metadata. Loot on the event is edited separately via LootEditService
// (Loot History), which already refunds/re-spends correctly.
//
// HNM CAMP ARCHIVES ARE READ-ONLY HERE for anything that moves DKP. A camp is paid from its
// review card as "SnapshotEarned" rows, not "EventEarned" ones, so these edits could not see the
// money they claimed to change: Remove dropped the attendee but kept their DKP, Save only changed
// the label, and Add paid a second time on top of the card. The card's own re-sync then rewrote
// the attendee list from the review anyway. Officers fix a camp on its review card (Edit Posted)
// or with a DKP audit instead. Active credit, absences and Delete still work on a camp.
public sealed class EventHistoryEditService
{
    private const string EventEarnedEntryType = "EventEarned";

    // The 400 the Past Events routes return for a DKP edit on a camp archive.
    public const string CampArchiveMessage = "Edit this camp from its review card (Edit Posted) or the DKP audit.";

    private readonly ApplicationDbContext _db;
    private readonly DkpLedgerWriter _dkpLedger;
    private readonly DkpPoolResolver _dkpPools;

    public EventHistoryEditService(ApplicationDbContext db, DkpLedgerWriter dkpLedger, DkpPoolResolver dkpPools)
    {
        _db = db;
        _dkpLedger = dkpLedger;
        _dkpPools = dkpPools;
    }

    // The review card (Window Event) this Past Event is the archive of, or null for an ordinary
    // timed event. A camp archive's DKP lives on that card, not here -- see the class comment.
    public Task<int?> CampWindowEventIdAsync(int historyId, CancellationToken cancellationToken) =>
        _db.WindowEvents
            .Where(w => w.CampEventHistoryId == historyId)
            .OrderBy(w => w.Id)
            .Select(w => (int?)w.Id)
            .FirstOrDefaultAsync(cancellationToken);

    // Update event metadata and, if DkpPerHour changed, rescale every member's
    // earned DKP. On a camp archive, name/type/location/details still save, but a new
    // Duration or DkpPerHour is refused (CampArchive) before anything changes.
    // Every DKP-moving edit below re-runs from the top when a member's balance changed under it
    // (Data/ConcurrencyRetry.cs), so the edit lands on top of the other change instead of erasing it.
    public Task<EventHistoryEditOutcome> EditEventAsync(int historyId, EventHistoryEditInput input, CancellationToken cancellationToken) =>
        ConcurrencyRetry.RunAsync(_db, () => EditEventOnceAsync(historyId, input, cancellationToken));

    private async Task<EventHistoryEditOutcome> EditEventOnceAsync(int historyId, EventHistoryEditInput input, CancellationToken cancellationToken)
    {
        var history = await _db.EventHistories
            .Include(h => h.AppUserEventHistories)
            .FirstOrDefaultAsync(h => h.Id == historyId, cancellationToken);
        if (history is null) return EventHistoryEditOutcome.NotFound;

        // Only an actual CHANGE is refused: the form sends the stored values back unchanged on
        // every Save, and a rename of a camp must still go through.
        var rateChanges = input.DkpPerHour.HasValue && input.DkpPerHour.Value != (history.DkpPerHour ?? 0);
        var durationChanges = input.Duration.HasValue && input.Duration.Value != history.Duration;
        if ((rateChanges || durationChanges)
            && await CampWindowEventIdAsync(historyId, cancellationToken) is not null)
        {
            return EventHistoryEditOutcome.CampArchive;
        }

        var oldEventType = history.EventType;
        if (!string.IsNullOrWhiteSpace(input.EventName)) history.EventName = input.EventName.Trim();
        history.EventType = Clean(input.EventType);
        history.EventLocation = Clean(input.EventLocation);
        history.Details = Clean(input.Details);
        if (input.Duration.HasValue) history.Duration = input.Duration.Value;

        // Retyping a closed event (Sky -> Sea) moves its DKP to the other event type's pool. The
        // ledger rows carry their own denormalized EventType, so they have to move with it —
        // otherwise the event says "Sea" while its DKP still sits in Sky's wallet. Pinned rows
        // (an adjustment someone pinned to this event) are left alone by Repoint.
        if (!string.Equals(oldEventType, history.EventType, StringComparison.OrdinalIgnoreCase))
        {
            var newPoolId = await _dkpPools.ResolveAsync(history.LinkshellId, history.EventType, cancellationToken);
            var eventRows = await _db.DkpLedgerEntries
                .Where(entry => entry.EventHistoryId == historyId)
                .ToListAsync(cancellationToken);
            foreach (var entry in eventRows)
            {
                entry.EventType = history.EventType;
                _dkpLedger.Repoint(entry, newPoolId);
            }
        }

        var oldRate = history.DkpPerHour ?? 0;
        var newRate = input.DkpPerHour ?? oldRate;
        if (newRate != oldRate)
        {
            var step = await StepForAsync(history.LinkshellId, cancellationToken);
            var memberships = await MembershipsAsync(history.LinkshellId, cancellationToken);
            var earnedEntries = await EarnedEntriesAsync(history.Id, cancellationToken);
            var audits = await AuditsByCorrectedRowAsync(earnedEntries.Values, cancellationToken);

            foreach (var p in history.AppUserEventHistories)
            {
                // Scaled from what the member really has from this event (earned row + its audit
                // corrections), not from the stored EventDkp: a DKP audit does not patch that copy
                // (Past Events reads the ledger, PastEventDkp), so it can say 10 while the member
                // has 8, and scaling the 10 set the total to 20 -- erasing the officer's -2.
                var oldEarned = LedgerTotal(p, earnedEntries, audits) ?? p.EventDkp ?? 0;
                var newEarned = oldRate > 0
                    ? DkpRounding.Round(oldEarned * newRate / oldRate, step)
                    : DkpRounding.Round((p.Duration ?? 0) * newRate, step);
                ApplyEarnedChange(p, newEarned, memberships, earnedEntries, audits,
                    $"DKP earned from completed event (edited: rate {oldRate} -> {newRate}).");
            }
            history.DkpPerHour = newRate;
        }

        history.EventDkp = history.AppUserEventHistories.Sum(p => p.EventDkp ?? 0);
        await _db.SaveChangesAsync(cancellationToken);
        return EventHistoryEditOutcome.Done;
    }

    // Set one member's earned DKP to a specific value (officer correction).
    // Re-run on a lost balance race (see EditEventAsync).
    public Task<EventHistoryEditOutcome> SetParticipantDkpAsync(
        int historyId,
        int participantId,
        double amount,
        CancellationToken cancellationToken) =>
        ConcurrencyRetry.RunAsync(_db, () => SetParticipantDkpOnceAsync(historyId, participantId, amount, cancellationToken));

    private async Task<EventHistoryEditOutcome> SetParticipantDkpOnceAsync(int historyId, int participantId, double amount, CancellationToken cancellationToken)
    {
        var history = await _db.EventHistories
            .Include(h => h.AppUserEventHistories)
            .FirstOrDefaultAsync(h => h.Id == historyId, cancellationToken);
        var participant = history?.AppUserEventHistories.FirstOrDefault(p => p.Id == participantId);
        if (history is null || participant is null) return EventHistoryEditOutcome.NotFound;
        if (await CampWindowEventIdAsync(historyId, cancellationToken) is not null) return EventHistoryEditOutcome.CampArchive;

        var step = await StepForAsync(history.LinkshellId, cancellationToken);
        var memberships = await MembershipsAsync(history.LinkshellId, cancellationToken);
        var earnedEntries = await EarnedEntriesAsync(history.Id, cancellationToken);
        var audits = await AuditsByCorrectedRowAsync(earnedEntries.Values, cancellationToken);

        ApplyEarnedChange(participant, DkpRounding.Round(amount, step),
            memberships, earnedEntries, audits, "DKP earned from completed event (edited).");

        history.EventDkp = history.AppUserEventHistories.Sum(p => p.EventDkp ?? 0);
        await _db.SaveChangesAsync(cancellationToken);
        return EventHistoryEditOutcome.Done;
    }

    // Add a member to a CLOSED event after the fact and grant them DKP — tied into the
    // DKP data exactly like an original attendee: creates the attendance row, the canonical
    // per-member "EventEarned" ledger entry, and adds the DKP to their spendable balance
    // (and lifetime Total via the ledger sum). Returns Rejected if they aren't a member or
    // are already on the event. Recomputes the activity streak after.
    // Re-run on a lost balance race (see EditEventAsync).
    public Task<EventHistoryEditOutcome> AddParticipantAsync(
        int historyId,
        string appUserId,
        double dkp,
        string? jobType,
        string? jobName,
        string? subJobName,
        bool activeCredit,
        CancellationToken cancellationToken) =>
        ConcurrencyRetry.RunAsync(_db, () => AddParticipantOnceAsync(historyId, appUserId, dkp, jobType, jobName, subJobName, activeCredit, cancellationToken));

    private async Task<EventHistoryEditOutcome> AddParticipantOnceAsync(
        int historyId,
        string appUserId,
        double dkp,
        string? jobType,
        string? jobName,
        string? subJobName,
        bool activeCredit,
        CancellationToken cancellationToken)
    {
        var history = await _db.EventHistories
            .Include(h => h.AppUserEventHistories)
            .FirstOrDefaultAsync(h => h.Id == historyId, cancellationToken);
        if (history is null) return EventHistoryEditOutcome.NotFound;
        if (await CampWindowEventIdAsync(historyId, cancellationToken) is not null) return EventHistoryEditOutcome.CampArchive;
        if (history.AppUserEventHistories.Any(p => string.Equals(p.AppUserId, appUserId, StringComparison.Ordinal)))
        {
            return EventHistoryEditOutcome.Rejected; // already on the event — correct their DKP via SetParticipantDkp instead
        }

        var membership = await _db.AppUserLinkshells
            .Include(m => m.AppUser)
            .FirstOrDefaultAsync(m => m.LinkshellId == history.LinkshellId && m.AppUserId == appUserId, cancellationToken);
        if (membership is null) return EventHistoryEditOutcome.Rejected; // not a member of this linkshell

        var step = await StepForAsync(history.LinkshellId, cancellationToken);
        // Clamp to >= 0 on the SERVER (the client min=0 is only advisory and can be
        // bypassed) so a granted amount can never silently penalise a member's balance.
        var earned = DkpRounding.Round(Math.Max(0, dkp), step);
        var characterName = membership.CharacterName ?? membership.AppUser?.CharacterName ?? membership.AppUser?.UserName ?? "Unknown member";

        history.AppUserEventHistories.Add(new AppUserEventHistory
        {
            EventHistoryId = history.Id,
            AppUserId = appUserId,
            CharacterName = characterName,
            JobType = Clean(jobType),
            JobName = Clean(jobName),
            SubJobName = Clean(subJobName),
            StartTime = history.StartTime,
            Duration = history.Duration,
            EventDkp = earned,
            IsQuickJoin = true, // added after the fact, not part of the original roster
            IsVerified = true,  // an officer is adding them, so treat as verified
            ActiveCredit = activeCredit,
        });

        // DKP tie-in: add to the spendable balance + record the canonical EventEarned ledger row
        // (one per member per event — edits/removals rely on it existing). It earns into the same
        // pool the event's type maps to, exactly as if they'd been on the original roster.
        await _dkpLedger.AppendAsync(
            membership,
            EventEarnedEntryType,
            earned,
            history.EndTime ?? DateTime.UtcNow,
            DkpPoolRef.Derived(history.EventType),
            new DkpEntryContext(
                CharacterName: characterName,
                EventName: history.EventName,
                EventType: history.EventType,
                EventLocation: history.EventLocation,
                EventStartTime: history.StartTime,
                EventEndTime: history.EndTime,
                Details: "DKP earned from completed event (member added afterward).",
                EventHistoryId: history.Id),
            cancellationToken);

        history.EventDkp = history.AppUserEventHistories.Sum(p => p.EventDkp ?? 0);
        try
        {
            await _db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (ex is not DbUpdateConcurrencyException)
        {
            // Concurrency: another officer added the same member to this event a moment
            // earlier, so this insert hit the unique (EventHistoryId, AppUserId) index.
            // Treat it as a no-op "already added" rather than surfacing a 500.
            // (A lost balance race is not this: AddParticipantAsync re-runs the add for it.)
            return EventHistoryEditOutcome.Rejected;
        }

        await new MemberActivityService(_db).ApplyComputedStatusAsync(history.LinkshellId, cancellationToken);
        return EventHistoryEditOutcome.Done;
    }

    // Toggle a member's active-status credit for this event. Drives the activity
    // streak (and the roster's active-credit column), so recompute statuses after.
    public async Task<bool> SetParticipantActiveCreditAsync(int historyId, int participantId, bool credited, CancellationToken cancellationToken)
    {
        var history = await _db.EventHistories
            .Include(h => h.AppUserEventHistories)
            .FirstOrDefaultAsync(h => h.Id == historyId, cancellationToken);
        var participant = history?.AppUserEventHistories.FirstOrDefault(p => p.Id == participantId);
        if (history is null || participant is null) return false;

        participant.ActiveCredit = credited;
        await _db.SaveChangesAsync(cancellationToken);
        await new MemberActivityService(_db).ApplyComputedStatusAsync(history.LinkshellId, cancellationToken);
        return true;
    }

    // Set active-status credit for EVERY participant of an event in one shot — used
    // by "undo active credit for the whole event" when it was credited by accident.
    // Returns the number of participant rows changed. Recomputes statuses after.
    public async Task<int> SetAllParticipantsActiveCreditAsync(int historyId, bool credited, CancellationToken cancellationToken)
    {
        var history = await _db.EventHistories
            .Include(h => h.AppUserEventHistories)
            .FirstOrDefaultAsync(h => h.Id == historyId, cancellationToken);
        if (history is null) return 0;

        var changed = 0;
        foreach (var participant in history.AppUserEventHistories)
        {
            if (participant.ActiveCredit != credited)
            {
                participant.ActiveCredit = credited;
                changed++;
            }
        }

        if (changed > 0)
        {
            await _db.SaveChangesAsync(cancellationToken);
            await new MemberActivityService(_db).ApplyComputedStatusAsync(history.LinkshellId, cancellationToken);
        }
        return changed;
    }

    // "Undo absences for the whole event" — stop this event counting toward
    // active tracking so members who MISSED it are no longer marked absent for it
    // (absences are derived from counting events a member wasn't credited on, so
    // there's no per-member absence row to clear — toggling the event's
    // CountsTowardActive is the mechanism). Also removes the event from credit
    // counting; pair with "undo active credit" to fully neutralize a mistaken
    // event. Returns true when the flag changed. Recomputes statuses after.
    public async Task<bool> SetEventCountsTowardActiveAsync(int historyId, bool counts, CancellationToken cancellationToken)
    {
        var history = await _db.EventHistories.FirstOrDefaultAsync(h => h.Id == historyId, cancellationToken);
        if (history is null) return false;
        if (history.CountsTowardActive == counts) return false;

        history.CountsTowardActive = counts;
        await _db.SaveChangesAsync(cancellationToken);
        await new MemberActivityService(_db).ApplyComputedStatusAsync(history.LinkshellId, cancellationToken);
        return true;
    }

    // Remove a member from the event: refund their earned DKP (subtract from
    // balance, delete the EventEarned ledger row) and drop the attendance record.
    // Re-run on a lost balance race (see EditEventAsync).
    public Task<EventHistoryEditOutcome> RemoveParticipantAsync(int historyId, int participantId, CancellationToken cancellationToken) =>
        ConcurrencyRetry.RunAsync(_db, () => RemoveParticipantOnceAsync(historyId, participantId, cancellationToken));

    private async Task<EventHistoryEditOutcome> RemoveParticipantOnceAsync(int historyId, int participantId, CancellationToken cancellationToken)
    {
        var history = await _db.EventHistories
            .Include(h => h.AppUserEventHistories)
            .FirstOrDefaultAsync(h => h.Id == historyId, cancellationToken);
        var participant = history?.AppUserEventHistories.FirstOrDefault(p => p.Id == participantId);
        if (history is null || participant is null) return EventHistoryEditOutcome.NotFound;
        if (await CampWindowEventIdAsync(historyId, cancellationToken) is not null) return EventHistoryEditOutcome.CampArchive;

        if (!string.IsNullOrWhiteSpace(participant.AppUserId))
        {
            var memberships = await MembershipsAsync(history.LinkshellId, cancellationToken);
            var earnedEntries = await EarnedEntriesAsync(history.Id, cancellationToken);
            if (earnedEntries.TryGetValue(participant.AppUserId, out var entry))
            {
                // Reverse the LEDGER ROW's amount, not the participant's EventDkp. The two can
                // disagree (the ledger row may have been audited since), and reversing the display
                // field would leave the balance off by the difference — permanently, with nothing
                // left pointing at the discrepancy.
                memberships.TryGetValue(participant.AppUserId, out var membership);
                _dkpLedger.Remove(entry, membership);

                // And every DKP audit correction of that row: it only existed to adjust what this
                // member earned here. Left behind, a -2 audit kept them 2 short for an event they
                // are no longer on.
                var audits = await AuditsByCorrectedRowAsync(new[] { entry }, cancellationToken);
                foreach (var audit in audits[entry.Id])
                {
                    _dkpLedger.Remove(audit, membership);
                }
            }
        }

        _db.Remove(participant);
        history.EventDkp = history.AppUserEventHistories
            .Where(p => p.Id != participantId).Sum(p => p.EventDkp ?? 0);
        await _db.SaveChangesAsync(cancellationToken);

        // Attendance changed → recompute the activity streak (no-op if tracking off).
        await new MemberActivityService(_db).ApplyComputedStatusAsync(history.LinkshellId, cancellationToken);
        return EventHistoryEditOutcome.Done;
    }

    // Delete a CLOSED event entirely and undo everything it did to DKP. The member's
    // LinkshellDkp balance was built by SUMMING every ledger amount this event added
    // (positive EventEarned + negative LootSpent), so we reverse by subtracting each
    // entry's amount, then delete those ledger rows, the event's loot rows, its
    // attendance rows, and the history itself (discussion comments cascade in the DB).
    // Recomputes active status after. Returns false when the history doesn't exist.
    // Re-run on a lost balance race (see EditEventAsync).
    public Task<bool> DeleteEventAsync(int historyId, CancellationToken cancellationToken) =>
        ConcurrencyRetry.RunAsync(_db, () => DeleteEventOnceAsync(historyId, cancellationToken));

    private async Task<bool> DeleteEventOnceAsync(int historyId, CancellationToken cancellationToken)
    {
        var history = await _db.EventHistories
            .Include(h => h.AppUserEventHistories)
            .FirstOrDefaultAsync(h => h.Id == historyId, cancellationToken);
        if (history is null) return false;

        var linkshellId = history.LinkshellId;
        var memberships = await MembershipsAsync(linkshellId, cancellationToken);

        var ledgerEntries = await _db.DkpLedgerEntries
            .Where(e => e.EventHistoryId == historyId)
            .ToListAsync(cancellationToken);
        // DKP audit corrections of those rows go with them. New ones carry this event's id and are
        // already in the list; ones written before audits copied it are found through the row they
        // correct. Without this a deleted event's -2 audit left the member 2 short.
        var auditsByRow = await AuditsByCorrectedRowAsync(ledgerEntries, cancellationToken);
        var listed = ledgerEntries.Select(e => e.Id).ToHashSet();
        ledgerEntries.AddRange(auditsByRow.Values.SelectMany(rows => rows).Where(audit => listed.Add(audit.Id)));
        foreach (var entry in ledgerEntries)
        {
            AppUserLinkshell? membership = null;
            if (!string.IsNullOrWhiteSpace(entry.AppUserId))
            {
                memberships.TryGetValue(entry.AppUserId, out membership);
            }
            _dkpLedger.Remove(entry, membership);
        }

        // The event's loot rows were re-parented to this history at close (FK is
        // SetNull on history delete, so remove them explicitly to avoid orphans).
        var lootDetails = await _db.EventLootDetails
            .Where(d => d.EventHistoryId == historyId)
            .ToListAsync(cancellationToken);
        _db.EventLootDetails.RemoveRange(lootDetails);

        // The Window Event this camp was handed off as (HnmCampReviewHandoffService), if it has
        // been POSTED. Its DKP is the ledger rows reversed above -- every one carries this
        // history's id -- so the card is the last thing saying the camp was paid. It cannot be
        // left standing: WindowEventDkpLedgerService's per-linkshell sweep reads "posted, no
        // archive" as a row from before archives existed and REBUILDS this Past Event from the
        // card's own Camp* copy, re-credits it, and queues an "event ended" summary to Discord
        // for a camp that ended days ago. Deleting the Past Event then never sticks.
        //
        // Deleted the way DeleteWindowEventAsync deletes a card: with its snapshots. Member DKP
        // overrides cascade in the database.
        //
        // An UNPOSTED card is deliberately left alone -- the FK is SetNull for exactly that case.
        // Its DKP has not been paid, so the officer must still be able to review and Post it, and
        // that Post files the camp afresh on purpose.
        var postedCards = await _db.WindowEvents
            .Include(w => w.Snapshots).ThenInclude(s => s.Entries)
            .Where(w => w.CampEventHistoryId == historyId && w.PostedToSheetAt != null)
            .ToListAsync(cancellationToken);
        foreach (var card in postedCards)
        {
            _db.AttendanceSnapshotEntries.RemoveRange(card.Snapshots.SelectMany(s => s.Entries));
            _db.AttendanceSnapshots.RemoveRange(card.Snapshots);
            _db.WindowEvents.Remove(card);
        }

        _db.RemoveRange(history.AppUserEventHistories);
        _db.EventHistories.Remove(history);
        await _db.SaveChangesAsync(cancellationToken);

        // Attendance is gone → recompute the activity streak (no-op if tracking off).
        await new MemberActivityService(_db).ApplyComputedStatusAsync(linkshellId, cancellationToken);
        return true;
    }

    // ---- helpers ------------------------------------------------------------

    // Set a participant's earned DKP to `newEarned`, moving the balance and the canonical
    // EventEarned ledger row together.
    //
    // The ledger row is the source of truth for the delta, not the participant's EventDkp: those
    // two can disagree (an officer can audit the ledger row directly), and taking the delta from
    // the display field would then leave the balance permanently off by the difference.
    //
    // `newEarned` is what the member should end up with from this event IN TOTAL. A DKP audit may
    // already have corrected the row (10 earned, audited -2 = 8); those corrections stay, so the
    // row is set to the rest. Setting the row itself to 8 made the member 6.
    private void ApplyEarnedChange(
        AppUserEventHistory participant,
        double newEarned,
        IReadOnlyDictionary<string, AppUserLinkshell> memberships,
        IReadOnlyDictionary<string, DkpLedgerEntry> earnedEntries,
        IReadOnlyDictionary<int, List<DkpLedgerEntry>> auditsByRow,
        string ledgerDetails)
    {
        participant.EventDkp = newEarned;
        if (string.IsNullOrWhiteSpace(participant.AppUserId)) return;
        if (!earnedEntries.TryGetValue(participant.AppUserId, out var entry)) return;

        var audited = auditsByRow.TryGetValue(entry.Id, out var audits) ? audits.Sum(a => a.Amount) : 0d;
        memberships.TryGetValue(participant.AppUserId, out var membership);
        _dkpLedger.Amend(entry, newEarned - audited, ledgerDetails, membership);
    }

    // What the member actually got from this event in the ledger: their earned row plus every audit
    // correction of it. Null when they have no earned row (then the shown figure is all there is).
    private static double? LedgerTotal(
        AppUserEventHistory participant,
        IReadOnlyDictionary<string, DkpLedgerEntry> earnedEntries,
        IReadOnlyDictionary<int, List<DkpLedgerEntry>> auditsByRow)
    {
        if (string.IsNullOrWhiteSpace(participant.AppUserId)) return null;
        if (!earnedEntries.TryGetValue(participant.AppUserId, out var entry)) return null;
        var audited = auditsByRow.TryGetValue(entry.Id, out var audits) ? audits.Sum(a => a.Amount) : 0d;
        return entry.Amount + audited;
    }

    // Every DKP audit correction of each given row, keyed by that row's id (DkpAuditChain). Tracked,
    // because Remove and Delete reverse these rows.
    private Task<Dictionary<int, List<DkpLedgerEntry>>> AuditsByCorrectedRowAsync(
        IEnumerable<DkpLedgerEntry> rows, CancellationToken cancellationToken) =>
        DkpAuditChain.ByCorrectedRowAsync(_db.DkpLedgerEntries, rows, cancellationToken);

    private async Task<double> StepForAsync(int linkshellId, CancellationToken cancellationToken)
    {
        var increment = await _db.Linkshells
            .Where(l => l.Id == linkshellId)
            .Select(l => l.DkpRoundingIncrement)
            .FirstOrDefaultAsync(cancellationToken);
        return DkpRounding.StepFor(increment);
    }

    private async Task<Dictionary<string, AppUserLinkshell>> MembershipsAsync(int linkshellId, CancellationToken cancellationToken)
    {
        var rows = await _db.AppUserLinkshells
            .Where(m => m.LinkshellId == linkshellId && m.AppUserId != null)
            .ToListAsync(cancellationToken);
        var map = new Dictionary<string, AppUserLinkshell>(StringComparer.Ordinal);
        foreach (var row in rows) { map[row.AppUserId!] = row; }
        return map;
    }

    private async Task<Dictionary<string, DkpLedgerEntry>> EarnedEntriesAsync(int historyId, CancellationToken cancellationToken)
    {
        var rows = await _db.DkpLedgerEntries
            .Where(e => e.EventHistoryId == historyId && e.AppUserId != null && e.EntryType == EventEarnedEntryType)
            .ToListAsync(cancellationToken);
        var map = new Dictionary<string, DkpLedgerEntry>(StringComparer.Ordinal);
        foreach (var row in rows) { map[row.AppUserId!] = row; } // one EventEarned per member per event
        return map;
    }

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

// What a Past Event edit did. CampArchive: the event is an HNM camp's archive and the edit would
// move DKP, which belongs to the camp's review card (see EventHistoryEditService).
public enum EventHistoryEditOutcome
{
    Done,
    NotFound,
    Rejected,
    CampArchive,
}

public sealed record EventHistoryEditInput(
    string? EventName,
    string? EventType,
    string? EventLocation,
    string? Details,
    double? Duration,
    int? DkpPerHour);
