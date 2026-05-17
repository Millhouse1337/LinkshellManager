using LinkshellManagerDiscordApp.Controllers;
using LinkshellManagerDiscordApp.Data;
using LinkshellManagerDiscordApp.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace LinkshellManagerDiscordApp.Services;

public sealed record LootEditRequest(
    string? ItemName,
    string? ItemWinner,
    int? WinningDkpSpent,
    string Reason);

public sealed record LootEditResult(
    bool Success,
    string? ErrorMessage = null,
    int? LootDetailId = null,
    string? Source = null);

// Centralises the "officer corrects a recorded loot row" flow used by the
// Loot History feature. Reverses the previous DKP debit on the OLD winner,
// applies a fresh debit to the NEW winner (which may be the same player at
// a different amount), updates the loot row with the new values + audit
// stamps, and writes a refund/spend ledger pair so DKP history surfaces the
// edit with the officer's reason.
//
// The DKP math mirrors the existing create-time helpers exactly:
//   - ToD: ActivityDataController.HelpersTods.AdjustTodLootDkpAsync (handles
//     DKP / Hybrid / LootCouncil structures via the linkshell's LootStructure).
//   - Event: EventController.Lifecycle.EndEventCoreAsync (flat DKP only;
//     event loot has no Hybrid variant by design).
//
// LootCouncil linkshells skip all DKP work — only the loot row's metadata
// changes. The edit reason is still required so a rename is auditable.
public sealed class LootEditService
{
    private readonly ApplicationDbContext _db;
    private readonly ILogger<LootEditService> _logger;
    private readonly SheetSyncQueue _sheetSync;

    public LootEditService(
        ApplicationDbContext db,
        ILogger<LootEditService> logger,
        SheetSyncQueue sheetSync)
    {
        _db = db;
        _logger = logger;
        _sheetSync = sheetSync;
    }

    public async Task<LootEditResult> EditTodLootAsync(
        int lootDetailId,
        LootEditRequest request,
        AppUser actor,
        DateTime occurredAtUtc,
        CancellationToken cancellationToken = default)
    {
        var validation = ValidateRequest(request);
        if (validation is not null)
        {
            return new LootEditResult(false, validation);
        }

        var detail = await _db.TodLootDetails
            .Include(item => item.Tod)
                .ThenInclude(tod => tod!.Linkshell)
            .FirstOrDefaultAsync(item => item.Id == lootDetailId, cancellationToken);

        if (detail is null || detail.Tod is null)
        {
            return new LootEditResult(false, "Loot record not found.");
        }

        var newItemName = (request.ItemName ?? string.Empty).Trim();
        var newItemWinner = (request.ItemWinner ?? string.Empty).Trim();
        if (string.IsNullOrEmpty(newItemName) || string.IsNullOrEmpty(newItemWinner) || !request.WinningDkpSpent.HasValue)
        {
            return new LootEditResult(false, "Item, winner, and DKP amount are all required.");
        }

        var linkshellId = detail.Tod.LinkshellId;
        var structure = ActivityDataController.NormalizeLootStructure(detail.Tod.Linkshell?.LootStructure ?? "Dkp");
        var monsterName = detail.Tod.MonsterName ?? "Unknown monster";

        if (structure == "LootCouncil")
        {
            await ApplyLootCouncilEditAsync(detail, newItemName, newItemWinner, request, actor, occurredAtUtc, cancellationToken);
            return new LootEditResult(true, null, detail.Id, "Tod");
        }

        var oldWinnerName = (detail.ItemWinner ?? string.Empty).Trim();
        var oldDkpValue = detail.WinningDkpSpent.GetValueOrDefault();
        var oldActual = detail.ActualDeductedDkp;
        var isHybrid = structure == "Hybrid";

        var newActualDeducted = await ReconcileLootDkpAsync(
            linkshellId: linkshellId,
            oldWinnerName: oldWinnerName,
            oldDkpRaw: oldDkpValue,
            oldActualDeducted: oldActual,
            newWinnerName: newItemWinner,
            newDkpRaw: request.WinningDkpSpent.Value,
            isHybrid: isHybrid,
            sourceTodLootDetailId: detail.Id,
            sourceEventLootDetailId: null,
            eventName: monsterName,
            eventType: null,
            eventLocation: null,
            eventStartTime: null,
            eventEndTime: null,
            itemName: newItemName,
            reason: request.Reason,
            occurredAtUtc: occurredAtUtc,
            cancellationToken: cancellationToken);

        detail.ItemName = newItemName;
        detail.ItemWinner = newItemWinner;
        detail.WinningDkpSpent = request.WinningDkpSpent;
        detail.ActualDeductedDkp = newActualDeducted;
        detail.EditedAt = occurredAtUtc;
        detail.EditedByAppUserId = actor.Id;
        detail.EditedByCharacterName = actor.CharacterName ?? actor.UserName;
        detail.LastEditReason = request.Reason.Trim();

        await _db.SaveChangesAsync(cancellationToken);
        // Recompute this ToD's day column on the ManualPoints tab so the edit
        // is reflected as a correction (the recompute reads the now-updated
        // loot rows). detail.Tod is loaded above (linkshellId came from it).
        await _sheetSync.EnqueueTodLootDeductionsAsync(detail.Tod.Id, cancellationToken);
        _logger.LogInformation(
            "Loot edit applied (ToD #{LootDetailId}) by {Actor}: '{OldWinner}' ({OldDkp} DKP) -> '{NewWinner}' ({NewDkp} DKP).",
            detail.Id, actor.Id, oldWinnerName, oldDkpValue, newItemWinner, request.WinningDkpSpent);
        return new LootEditResult(true, null, detail.Id, "Tod");
    }

    public async Task<LootEditResult> EditEventLootAsync(
        int lootDetailId,
        LootEditRequest request,
        AppUser actor,
        DateTime occurredAtUtc,
        CancellationToken cancellationToken = default)
    {
        var validation = ValidateRequest(request);
        if (validation is not null)
        {
            return new LootEditResult(false, validation);
        }

        var detail = await _db.EventLootDetails
            .Include(item => item.Event)
                .ThenInclude(evt => evt!.Linkshell)
            .Include(item => item.EventHistory)
                .ThenInclude(history => history!.Linkshell)
            .FirstOrDefaultAsync(item => item.Id == lootDetailId, cancellationToken);

        if (detail is null)
        {
            return new LootEditResult(false, "Loot record not found.");
        }

        var newItemName = (request.ItemName ?? string.Empty).Trim();
        var newItemWinner = (request.ItemWinner ?? string.Empty).Trim();
        if (string.IsNullOrEmpty(newItemName) || string.IsNullOrEmpty(newItemWinner) || !request.WinningDkpSpent.HasValue)
        {
            return new LootEditResult(false, "Item, winner, and DKP amount are all required.");
        }

        // Resolve the parent context (active Event vs. closed EventHistory).
        // Linkshell + identifying metadata come from whichever parent is
        // present. EventLootDetails for active events still have EventId set;
        // post-close they have EventHistoryId instead.
        int linkshellId;
        string? lootStructure;
        string? eventName;
        string? eventType;
        string? eventLocation;
        DateTime? eventStartTime;
        DateTime? eventEndTime;

        if (detail.EventHistory is not null)
        {
            linkshellId = detail.EventHistory.LinkshellId;
            lootStructure = detail.EventHistory.Linkshell?.LootStructure;
            eventName = detail.EventHistory.EventName;
            eventType = detail.EventHistory.EventType;
            eventLocation = detail.EventHistory.EventLocation;
            eventStartTime = detail.EventHistory.StartTime;
            eventEndTime = detail.EventHistory.EndTime;
        }
        else if (detail.Event is not null)
        {
            linkshellId = detail.Event.LinkshellId;
            lootStructure = detail.Event.Linkshell?.LootStructure;
            eventName = detail.Event.EventName;
            eventType = detail.Event.EventType;
            eventLocation = detail.Event.EventLocation;
            eventStartTime = detail.Event.StartTime;
            eventEndTime = detail.Event.EndTime;
        }
        else
        {
            return new LootEditResult(false, "Loot record is orphaned (no parent event).");
        }

        var structure = ActivityDataController.NormalizeLootStructure(lootStructure ?? "Dkp");

        if (structure == "LootCouncil")
        {
            await ApplyLootCouncilEditAsync(detail, newItemName, newItemWinner, request, actor, occurredAtUtc, cancellationToken);
            return new LootEditResult(true, null, detail.Id, "Event");
        }

        var oldWinnerName = (detail.ItemWinner ?? string.Empty).Trim();
        var oldDkpValue = detail.WinningDkpSpent.GetValueOrDefault();
        var oldActual = detail.ActualDeductedDkp;
        // Event loot has always been flat DKP; Hybrid is a ToD-only concept.
        const bool isHybrid = false;

        var newActualDeducted = await ReconcileLootDkpAsync(
            linkshellId: linkshellId,
            oldWinnerName: oldWinnerName,
            oldDkpRaw: oldDkpValue,
            oldActualDeducted: oldActual,
            newWinnerName: newItemWinner,
            newDkpRaw: request.WinningDkpSpent.Value,
            isHybrid: isHybrid,
            sourceTodLootDetailId: null,
            sourceEventLootDetailId: detail.Id,
            eventName: eventName,
            eventType: eventType,
            eventLocation: eventLocation,
            eventStartTime: eventStartTime,
            eventEndTime: eventEndTime,
            itemName: newItemName,
            reason: request.Reason,
            occurredAtUtc: occurredAtUtc,
            cancellationToken: cancellationToken);

        detail.ItemName = newItemName;
        detail.ItemWinner = newItemWinner;
        detail.WinningDkpSpent = request.WinningDkpSpent;
        detail.ActualDeductedDkp = newActualDeducted;
        detail.EditedAt = occurredAtUtc;
        detail.EditedByAppUserId = actor.Id;
        detail.EditedByCharacterName = actor.CharacterName ?? actor.UserName;
        detail.LastEditReason = request.Reason.Trim();

        await _db.SaveChangesAsync(cancellationToken);
        await _sheetSync.EnqueueAsync(linkshellId, cancellationToken);
        _logger.LogInformation(
            "Loot edit applied (Event #{LootDetailId}) by {Actor}: '{OldWinner}' ({OldDkp} DKP) -> '{NewWinner}' ({NewDkp} DKP).",
            detail.Id, actor.Id, oldWinnerName, oldDkpValue, newItemWinner, request.WinningDkpSpent);
        return new LootEditResult(true, null, detail.Id, "Event");
    }

    // --- internals ---

    private static string? ValidateRequest(LootEditRequest request)
    {
        if (request is null) return "Request body is required.";
        if (string.IsNullOrWhiteSpace(request.Reason)) return "An edit reason is required.";
        if (request.Reason.Trim().Length > 512) return "Edit reason must be 512 characters or fewer.";
        if (request.WinningDkpSpent.HasValue && request.WinningDkpSpent.Value < 0)
        {
            return "DKP amount cannot be negative.";
        }
        return null;
    }

    private async Task ApplyLootCouncilEditAsync(
        TodLootDetail detail,
        string newItemName,
        string newItemWinner,
        LootEditRequest request,
        AppUser actor,
        DateTime occurredAtUtc,
        CancellationToken cancellationToken)
    {
        // LootCouncil: no DKP math, just metadata + audit. The edit reason
        // is still required (set by ValidateRequest), so the rename has an
        // auditable trail on the row itself.
        detail.ItemName = newItemName;
        detail.ItemWinner = newItemWinner;
        detail.WinningDkpSpent = request.WinningDkpSpent;
        detail.EditedAt = occurredAtUtc;
        detail.EditedByAppUserId = actor.Id;
        detail.EditedByCharacterName = actor.CharacterName ?? actor.UserName;
        detail.LastEditReason = request.Reason.Trim();
        await _db.SaveChangesAsync(cancellationToken);
    }

    private async Task ApplyLootCouncilEditAsync(
        EventLootDetail detail,
        string newItemName,
        string newItemWinner,
        LootEditRequest request,
        AppUser actor,
        DateTime occurredAtUtc,
        CancellationToken cancellationToken)
    {
        detail.ItemName = newItemName;
        detail.ItemWinner = newItemWinner;
        detail.WinningDkpSpent = request.WinningDkpSpent;
        detail.EditedAt = occurredAtUtc;
        detail.EditedByAppUserId = actor.Id;
        detail.EditedByCharacterName = actor.CharacterName ?? actor.UserName;
        detail.LastEditReason = request.Reason.Trim();
        await _db.SaveChangesAsync(cancellationToken);
    }

    // Performs the refund/spend ledger pair + AppUserLinkshell.LinkshellDkp
    // updates that an edit implies. Mirrors AdjustTodLootDkpAsync's structure
    // but operates on a single row with explicit old/new fields, and tags
    // each ledger entry with the edit reason so the DKP history view can
    // render an "Edited" badge.
    // Returns the new ActualDeducted DKP amount to stamp on the loot row
    // (null when nothing was debited, e.g. when newDkpRaw is 0).
    private async Task<double?> ReconcileLootDkpAsync(
        int linkshellId,
        string oldWinnerName,
        int oldDkpRaw,
        double? oldActualDeducted,
        string newWinnerName,
        int newDkpRaw,
        bool isHybrid,
        int? sourceTodLootDetailId,
        int? sourceEventLootDetailId,
        string? eventName,
        string? eventType,
        string? eventLocation,
        DateTime? eventStartTime,
        DateTime? eventEndTime,
        string itemName,
        string reason,
        DateTime occurredAtUtc,
        CancellationToken cancellationToken)
    {
        double? newActualDeducted = null;

        // Load both winner memberships in one query so a same-winner edit
        // can mutate the row in place without a second round-trip.
        var winnerNames = new[] { oldWinnerName, newWinnerName }
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var memberships = winnerNames.Count == 0
            ? new List<AppUserLinkshell>()
            : await _db.AppUserLinkshells
                .Where(link => link.LinkshellId == linkshellId
                            && link.AppUserId != null
                            && winnerNames.Contains(link.CharacterName!))
                .ToListAsync(cancellationToken);

        var membershipsByCharacter = memberships
            .Where(link => !string.IsNullOrWhiteSpace(link.CharacterName) && !string.IsNullOrWhiteSpace(link.AppUserId))
            .GroupBy(link => link.CharacterName!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

        var oldMembership = !string.IsNullOrWhiteSpace(oldWinnerName) && membershipsByCharacter.TryGetValue(oldWinnerName, out var resolvedOld)
            ? resolvedOld
            : null;
        var newMembership = membershipsByCharacter.TryGetValue(newWinnerName, out var resolvedNew)
            ? resolvedNew
            : null;

        if (newMembership is null)
        {
            throw new InvalidOperationException(
                $"Winner '{newWinnerName}' is not a member of the linkshell. Pick an active linkshell member.");
        }

        // We need a per-AppUser ledger sequence number for any new entries.
        // Fetch the current max for each affected user in one query.
        var affectedAppUserIds = new[] { oldMembership?.AppUserId, newMembership.AppUserId }
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList()!;

        var nextSequenceByAppUserId = await _db.DkpLedgerEntries
            .Where(entry => entry.LinkshellId == linkshellId
                         && entry.AppUserId != null
                         && affectedAppUserIds.Contains(entry.AppUserId))
            .GroupBy(entry => entry.AppUserId!)
            .Select(group => new { AppUserId = group.Key, NextSequence = group.Max(entry => entry.Sequence) + 1 })
            .ToDictionaryAsync(item => item.AppUserId, item => item.NextSequence, StringComparer.OrdinalIgnoreCase, cancellationToken);

        var ledgerEntries = new List<DkpLedgerEntry>();

        // ----- Refund the OLD debit -----
        if (oldMembership is not null && oldDkpRaw > 0)
        {
            double refundAmount;
            if (oldActualDeducted.HasValue)
            {
                refundAmount = Math.Abs(oldActualDeducted.Value);
            }
            else if (isHybrid)
            {
                // No stored actual; approximate from the percent + current
                // balance the same way AdjustTodLootDkpAsync does on refund.
                var pct = Math.Clamp((double)oldDkpRaw, 0, 100);
                if (pct >= 100d)
                {
                    refundAmount = 0;
                }
                else
                {
                    var currentBalance = Math.Max(0, oldMembership.LinkshellDkp ?? 0);
                    refundAmount = Math.Round(currentBalance * pct / (100d - pct), 2);
                }
            }
            else
            {
                refundAmount = oldDkpRaw;
            }

            if (refundAmount > 0)
            {
                oldMembership.LinkshellDkp = (oldMembership.LinkshellDkp ?? 0d) + refundAmount;
                var refundSeq = NextSequence(nextSequenceByAppUserId, oldMembership.AppUserId!);
                ledgerEntries.Add(new DkpLedgerEntry
                {
                    AppUserId = oldMembership.AppUserId,
                    LinkshellId = linkshellId,
                    EntryType = "LootEditRefund",
                    Amount = refundAmount,
                    Sequence = refundSeq,
                    OccurredAt = occurredAtUtc,
                    CharacterName = oldMembership.CharacterName,
                    EventName = eventName,
                    EventType = eventType,
                    EventLocation = eventLocation,
                    EventStartTime = eventStartTime,
                    EventEndTime = eventEndTime,
                    ItemName = itemName,
                    Details = $"Edit refund: previous loot record corrected. Reason: {Truncate(reason, 800)}",
                    EditReason = Truncate(reason, 512),
                    SourceTodLootDetailId = sourceTodLootDetailId,
                    SourceEventLootDetailId = sourceEventLootDetailId
                });
            }
        }

        // ----- Apply the NEW debit -----
        if (newDkpRaw > 0)
        {
            double debitAmount;
            if (isHybrid)
            {
                // Hybrid debit = pct of current balance AFTER the refund above
                // has settled into newMembership.LinkshellDkp (refund only
                // touched the OLD membership row, so this is unchanged for
                // winner-change edits and correctly post-refund for same-
                // winner edits since they share the same membership row).
                var pct = Math.Clamp((double)newDkpRaw, 0, 100);
                var currentBalance = Math.Max(0, newMembership.LinkshellDkp ?? 0);
                debitAmount = Math.Round(currentBalance * pct / 100d, 2);
            }
            else
            {
                debitAmount = newDkpRaw;
            }

            if (debitAmount > 0)
            {
                newMembership.LinkshellDkp = (newMembership.LinkshellDkp ?? 0d) - debitAmount;
                newActualDeducted = debitAmount;
                var debitSeq = NextSequence(nextSequenceByAppUserId, newMembership.AppUserId!);
                ledgerEntries.Add(new DkpLedgerEntry
                {
                    AppUserId = newMembership.AppUserId,
                    LinkshellId = linkshellId,
                    EntryType = "LootEditSpent",
                    Amount = -debitAmount,
                    Sequence = debitSeq,
                    OccurredAt = occurredAtUtc,
                    CharacterName = newMembership.CharacterName,
                    EventName = eventName,
                    EventType = eventType,
                    EventLocation = eventLocation,
                    EventStartTime = eventStartTime,
                    EventEndTime = eventEndTime,
                    ItemName = itemName,
                    Details = $"Edit spend: new loot record applied. Reason: {Truncate(reason, 800)}",
                    EditReason = Truncate(reason, 512),
                    SourceTodLootDetailId = sourceTodLootDetailId,
                    SourceEventLootDetailId = sourceEventLootDetailId
                });
            }
        }

        if (ledgerEntries.Count > 0)
        {
            await _db.DkpLedgerEntries.AddRangeAsync(ledgerEntries, cancellationToken);
        }

        return newActualDeducted;
    }

    private static int NextSequence(IDictionary<string, int> map, string appUserId)
    {
        var current = map.TryGetValue(appUserId, out var existing) ? existing : 1;
        map[appUserId] = current + 1;
        return current;
    }

    private static string Truncate(string value, int max)
        => value.Length <= max ? value : value[..max];
}
