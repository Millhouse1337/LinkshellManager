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
public sealed class EventHistoryEditService
{
    private const string EventEarnedEntryType = "EventEarned";

    private readonly ApplicationDbContext _db;

    public EventHistoryEditService(ApplicationDbContext db)
    {
        _db = db;
    }

    // Update event metadata and, if DkpPerHour changed, rescale every member's
    // earned DKP. Returns false when the history row doesn't exist.
    public async Task<bool> EditEventAsync(int historyId, EventHistoryEditInput input, CancellationToken cancellationToken)
    {
        var history = await _db.EventHistories
            .Include(h => h.AppUserEventHistories)
            .FirstOrDefaultAsync(h => h.Id == historyId, cancellationToken);
        if (history is null) return false;

        if (!string.IsNullOrWhiteSpace(input.EventName)) history.EventName = input.EventName.Trim();
        history.EventType = Clean(input.EventType);
        history.EventLocation = Clean(input.EventLocation);
        history.Details = Clean(input.Details);
        if (input.Duration.HasValue) history.Duration = input.Duration.Value;

        var oldRate = history.DkpPerHour ?? 0;
        var newRate = input.DkpPerHour ?? oldRate;
        if (newRate != oldRate)
        {
            var step = await StepForAsync(history.LinkshellId, cancellationToken);
            var memberships = await MembershipsAsync(history.LinkshellId, cancellationToken);
            var earnedEntries = await EarnedEntriesAsync(history.Id, cancellationToken);

            foreach (var p in history.AppUserEventHistories)
            {
                var oldEarned = p.EventDkp ?? 0;
                var newEarned = oldRate > 0
                    ? DkpRounding.Round(oldEarned * newRate / oldRate, step)
                    : DkpRounding.Round((p.Duration ?? 0) * newRate, step);
                ApplyEarnedChange(p, oldEarned, newEarned, memberships, earnedEntries,
                    $"DKP earned from completed event (edited: rate {oldRate} -> {newRate}).");
            }
            history.DkpPerHour = newRate;
        }

        history.EventDkp = history.AppUserEventHistories.Sum(p => p.EventDkp ?? 0);
        await _db.SaveChangesAsync(cancellationToken);
        return true;
    }

    // Set one member's earned DKP to a specific value (officer correction).
    public async Task<bool> SetParticipantDkpAsync(int historyId, int participantId, double amount, CancellationToken cancellationToken)
    {
        var history = await _db.EventHistories
            .Include(h => h.AppUserEventHistories)
            .FirstOrDefaultAsync(h => h.Id == historyId, cancellationToken);
        var participant = history?.AppUserEventHistories.FirstOrDefault(p => p.Id == participantId);
        if (history is null || participant is null) return false;

        var step = await StepForAsync(history.LinkshellId, cancellationToken);
        var memberships = await MembershipsAsync(history.LinkshellId, cancellationToken);
        var earnedEntries = await EarnedEntriesAsync(history.Id, cancellationToken);

        ApplyEarnedChange(participant, participant.EventDkp ?? 0, DkpRounding.Round(amount, step),
            memberships, earnedEntries, "DKP earned from completed event (edited).");

        history.EventDkp = history.AppUserEventHistories.Sum(p => p.EventDkp ?? 0);
        await _db.SaveChangesAsync(cancellationToken);
        return true;
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
    public async Task<bool> RemoveParticipantAsync(int historyId, int participantId, CancellationToken cancellationToken)
    {
        var history = await _db.EventHistories
            .Include(h => h.AppUserEventHistories)
            .FirstOrDefaultAsync(h => h.Id == historyId, cancellationToken);
        var participant = history?.AppUserEventHistories.FirstOrDefault(p => p.Id == participantId);
        if (history is null || participant is null) return false;

        var earned = participant.EventDkp ?? 0;
        if (!string.IsNullOrWhiteSpace(participant.AppUserId))
        {
            var memberships = await MembershipsAsync(history.LinkshellId, cancellationToken);
            if (memberships.TryGetValue(participant.AppUserId, out var membership))
            {
                membership.LinkshellDkp = (membership.LinkshellDkp ?? 0) - earned;
            }
            var earnedEntries = await EarnedEntriesAsync(history.Id, cancellationToken);
            if (earnedEntries.TryGetValue(participant.AppUserId, out var entry))
            {
                _db.DkpLedgerEntries.Remove(entry);
            }
        }

        _db.Remove(participant);
        history.EventDkp = history.AppUserEventHistories
            .Where(p => p.Id != participantId).Sum(p => p.EventDkp ?? 0);
        await _db.SaveChangesAsync(cancellationToken);

        // Attendance changed → recompute the activity streak (no-op if tracking off).
        await new MemberActivityService(_db).ApplyComputedStatusAsync(history.LinkshellId, cancellationToken);
        return true;
    }

    // Delete a CLOSED event entirely and undo everything it did to DKP. The member's
    // LinkshellDkp balance was built by SUMMING every ledger amount this event added
    // (positive EventEarned + negative LootSpent), so we reverse by subtracting each
    // entry's amount, then delete those ledger rows, the event's loot rows, its
    // attendance rows, and the history itself (discussion comments cascade in the DB).
    // Recomputes active status after. Returns false when the history doesn't exist.
    public async Task<bool> DeleteEventAsync(int historyId, CancellationToken cancellationToken)
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
        foreach (var entry in ledgerEntries)
        {
            if (!string.IsNullOrWhiteSpace(entry.AppUserId)
                && memberships.TryGetValue(entry.AppUserId, out var membership))
            {
                membership.LinkshellDkp = (membership.LinkshellDkp ?? 0) - entry.Amount;
            }
        }
        _db.DkpLedgerEntries.RemoveRange(ledgerEntries);

        // The event's loot rows were re-parented to this history at close (FK is
        // SetNull on history delete, so remove them explicitly to avoid orphans).
        var lootDetails = await _db.EventLootDetails
            .Where(d => d.EventHistoryId == historyId)
            .ToListAsync(cancellationToken);
        _db.EventLootDetails.RemoveRange(lootDetails);

        _db.RemoveRange(history.AppUserEventHistories);
        _db.EventHistories.Remove(history);
        await _db.SaveChangesAsync(cancellationToken);

        // Attendance is gone → recompute the activity streak (no-op if tracking off).
        await new MemberActivityService(_db).ApplyComputedStatusAsync(linkshellId, cancellationToken);
        return true;
    }

    // ---- helpers ------------------------------------------------------------

    private static void ApplyEarnedChange(
        AppUserEventHistory participant,
        double oldEarned,
        double newEarned,
        IReadOnlyDictionary<string, AppUserLinkshell> memberships,
        IReadOnlyDictionary<string, DkpLedgerEntry> earnedEntries,
        string ledgerDetails)
    {
        participant.EventDkp = newEarned;
        var delta = newEarned - oldEarned;
        if (Math.Abs(delta) < 0.0001) return;
        if (string.IsNullOrWhiteSpace(participant.AppUserId)) return;

        if (memberships.TryGetValue(participant.AppUserId, out var membership))
        {
            membership.LinkshellDkp = (membership.LinkshellDkp ?? 0) + delta;
        }
        if (earnedEntries.TryGetValue(participant.AppUserId, out var entry))
        {
            entry.Amount = newEarned;
            entry.Details = ledgerDetails;
        }
    }

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

public sealed record EventHistoryEditInput(
    string? EventName,
    string? EventType,
    string? EventLocation,
    string? Details,
    double? Duration,
    int? DkpPerHour);
