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

    private readonly DkpLedgerWriter _dkpLedger;
    private readonly DkpPoolResolver _dkpPools;

    public LootEditService(
        ApplicationDbContext db,
        DkpLedgerWriter dkpLedger,
        DkpPoolResolver dkpPools,
        ILogger<LootEditService> logger)
    {
        _db = db;
        _dkpLedger = dkpLedger;
        _dkpPools = dkpPools;
        _logger = logger;
    }

    // Resolve a loot winner's membership by MAIN OR ALT character name.
    //
    // Event close (EventController.ResolveLootWinnerMembership) and the affordability guard
    // (LootDkpGuard) both resolve alts. This service used to match on CharacterName alone, so an
    // item won on an alt was debited at close but its edit/delete refund silently found nobody and
    // logged a warning — the member was permanently short the DKP.
    private async Task<AppUserLinkshell?> ResolveWinnerAsync(
        int linkshellId, string? winnerName, CancellationToken cancellationToken)
    {
        var name = winnerName?.Trim();
        if (string.IsNullOrEmpty(name))
        {
            return null;
        }

        // Compared in memory so the match isn't at the mercy of the DB collation.
        var members = await _db.AppUserLinkshells
            .Include(link => link.AppUser)
            .Where(link => link.LinkshellId == linkshellId && link.AppUserId != null)
            .ToListAsync(cancellationToken);

        return members.FirstOrDefault(link =>
            string.Equals(link.CharacterName, name, StringComparison.OrdinalIgnoreCase)
            || string.Equals(link.AppUser?.CharacterName, name, StringComparison.OrdinalIgnoreCase)
            || string.Equals(link.AppUser?.AltCharacterName1, name, StringComparison.OrdinalIgnoreCase)
            || string.Equals(link.AppUser?.AltCharacterName2, name, StringComparison.OrdinalIgnoreCase));
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
            // ToD loot is PINNED to the pool it was originally paid from, so an edit refunds and
            // re-debits the same wallet even if HNM has been remapped since.
            poolRef: DkpPoolRef.Pinned(detail.DkpPoolId ?? await DefaultPoolIdAsync(linkshellId, cancellationToken)),
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
            // Event loot FOLLOWS its event type, so a remap moves the original debit and these
            // compensating rows together — the refund can never be stranded in the old pool.
            poolRef: DkpPoolRef.Derived(eventType),
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
        _logger.LogInformation(
            "Loot edit applied (Event #{LootDetailId}) by {Actor}: '{OldWinner}' ({OldDkp} DKP) -> '{NewWinner}' ({NewDkp} DKP).",
            detail.Id, actor.Id, oldWinnerName, oldDkpValue, newItemWinner, request.WinningDkpSpent);
        return new LootEditResult(true, null, detail.Id, "Event");
    }

    // Removes a ToD loot row entirely and refunds the winner the DKP that was
    // debited for it (mirrors the "refund the OLD debit" half of an edit, with
    // no replacement debit). LootCouncil rows carry no DKP, so they're just
    // removed. The backing ToD is left intact — it may hold other loot or be a
    // real ToD; the ManualPoints day column self-corrects on recompute.
    public async Task<LootEditResult> DeleteTodLootAsync(
        int lootDetailId,
        AppUser actor,
        string reason,
        DateTime occurredAtUtc,
        CancellationToken cancellationToken = default)
    {
        var detail = await _db.TodLootDetails
            .Include(item => item.Tod)
                .ThenInclude(tod => tod!.Linkshell)
            .FirstOrDefaultAsync(item => item.Id == lootDetailId, cancellationToken);

        if (detail is null || detail.Tod is null)
        {
            return new LootEditResult(false, "Loot record not found.");
        }

        var linkshellId = detail.Tod.LinkshellId;
        var todId = detail.Tod.Id;
        var structure = ActivityDataController.NormalizeLootStructure(detail.Tod.Linkshell?.LootStructure ?? "Dkp");
        var monsterName = detail.Tod.MonsterName ?? "Unknown monster";
        var winnerName = (detail.ItemWinner ?? string.Empty).Trim();
        var itemName = (detail.ItemName ?? string.Empty).Trim();
        var dkpRaw = detail.WinningDkpSpent.GetValueOrDefault();

        if (structure != "LootCouncil")
        {
            await RefundDeletedLootAsync(
                linkshellId: linkshellId,
                winnerName: winnerName,
                dkpRaw: dkpRaw,
                actualDeducted: detail.ActualDeductedDkp,
                isHybrid: structure == "Hybrid",
                poolRef: DkpPoolRef.Pinned(detail.DkpPoolId ?? await DefaultPoolIdAsync(linkshellId, cancellationToken)),
                sourceTodLootDetailId: detail.Id,
                sourceEventLootDetailId: null,
                eventName: monsterName,
                eventType: null,
                eventLocation: null,
                eventStartTime: null,
                eventEndTime: null,
                itemName: itemName,
                reason: reason,
                occurredAtUtc: occurredAtUtc,
                cancellationToken: cancellationToken);
        }

        _db.TodLootDetails.Remove(detail);
        await _db.SaveChangesAsync(cancellationToken);
        _logger.LogInformation(
            "Loot deleted (ToD #{LootDetailId}) by {Actor}: '{Winner}' ({Dkp} DKP). Reason: {Reason}",
            lootDetailId, actor.Id, winnerName, dkpRaw, reason);
        return new LootEditResult(true, null, lootDetailId, "Tod");
    }

    // Event counterpart of DeleteTodLootAsync. Resolves the parent context
    // (active Event vs. closed EventHistory) the same way EditEventLootAsync
    // does. Event loot is always flat DKP (Hybrid is ToD-only).
    public async Task<LootEditResult> DeleteEventLootAsync(
        int lootDetailId,
        AppUser actor,
        string reason,
        DateTime occurredAtUtc,
        CancellationToken cancellationToken = default)
    {
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
        var winnerName = (detail.ItemWinner ?? string.Empty).Trim();
        var itemName = (detail.ItemName ?? string.Empty).Trim();
        var dkpRaw = detail.WinningDkpSpent.GetValueOrDefault();

        if (structure != "LootCouncil")
        {
            await RefundDeletedLootAsync(
                linkshellId: linkshellId,
                winnerName: winnerName,
                dkpRaw: dkpRaw,
                actualDeducted: detail.ActualDeductedDkp,
                isHybrid: false,
                poolRef: DkpPoolRef.Derived(eventType),
                sourceTodLootDetailId: null,
                sourceEventLootDetailId: detail.Id,
                eventName: eventName,
                eventType: eventType,
                eventLocation: eventLocation,
                eventStartTime: eventStartTime,
                eventEndTime: eventEndTime,
                itemName: itemName,
                reason: reason,
                occurredAtUtc: occurredAtUtc,
                cancellationToken: cancellationToken);
        }

        var eventHistoryId = detail.EventHistoryId;
        _db.EventLootDetails.Remove(detail);
        await _db.SaveChangesAsync(cancellationToken);
        _logger.LogInformation(
            "Loot deleted (Event #{LootDetailId}) by {Actor}: '{Winner}' ({Dkp} DKP). Reason: {Reason}",
            lootDetailId, actor.Id, winnerName, dkpRaw, reason);
        return new LootEditResult(true, null, lootDetailId, "Event");
    }

    // --- internals ---

    // The grid step (0.25 / 0.5) for the linkshell's DKP rounding setting, so
    // Hybrid loot spends/refunds land on the same increment as every other DKP
    // value. One lightweight lookup per loot edit (an infrequent action).
    private async Task<int> DefaultPoolIdAsync(int linkshellId, CancellationToken cancellationToken)
        => (await _dkpPools.GetMapAsync(linkshellId, cancellationToken)).DefaultPoolId;

    private async Task<double> GetRoundingStepAsync(int linkshellId, CancellationToken cancellationToken)
    {
        var increment = await _db.Linkshells
            .Where(linkshell => linkshell.Id == linkshellId)
            .Select(linkshell => linkshell.DkpRoundingIncrement)
            .FirstOrDefaultAsync(cancellationToken);
        return DkpRounding.StepFor(increment);
    }

    // Credits the winner back the DKP a now-deleted loot row debited and writes
    // a single "LootDeleteRefund" ledger entry (positive amount, so it never
    // triggers the DKP-spend Discord post). Refund amount mirrors the refund
    // branch of ReconcileLootDkpAsync. If the winner has since left the
    // linkshell there's no balance to credit — the row is still deleted; the
    // skipped refund is logged.
    private async Task RefundDeletedLootAsync(
        int linkshellId,
        string winnerName,
        int dkpRaw,
        double? actualDeducted,
        bool isHybrid,
        DkpPoolRef poolRef,
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
        if (string.IsNullOrWhiteSpace(winnerName) || dkpRaw <= 0)
        {
            return;
        }

        var membership = await ResolveWinnerAsync(linkshellId, winnerName, cancellationToken);
        if (membership is null || string.IsNullOrWhiteSpace(membership.AppUserId))
        {
            _logger.LogWarning(
                "Loot delete refund skipped: winner '{Winner}' is no longer a member of linkshell {LinkshellId}.",
                winnerName, linkshellId);
            return;
        }

        double refundAmount;
        if (actualDeducted.HasValue)
        {
            refundAmount = Math.Abs(actualDeducted.Value);
        }
        else if (isHybrid)
        {
            var pct = Math.Clamp((double)dkpRaw, 0, 100);
            if (pct >= 100d)
            {
                refundAmount = 0;
            }
            else
            {
                // Legacy row with no stored actual: that debit came out of the member's TOTAL
                // (pools didn't exist), so the inverse works off the total too.
                var roundingStep = await GetRoundingStepAsync(linkshellId, cancellationToken);
                var legacyBalance = Math.Max(0, membership.LinkshellDkp ?? 0);
                refundAmount = LootDkpCalculator.ComputeHybridRefund(legacyBalance, pct, roundingStep);
            }
        }
        else
        {
            refundAmount = dkpRaw;
        }

        if (refundAmount <= 0)
        {
            return;
        }

        await _dkpLedger.AppendAsync(
            membership,
            "LootDeleteRefund",
            refundAmount,
            occurredAtUtc,
            poolRef,
            new DkpEntryContext(
                CharacterName: membership.CharacterName,
                EventName: eventName,
                EventType: eventType,
                EventLocation: eventLocation,
                EventStartTime: eventStartTime,
                EventEndTime: eventEndTime,
                ItemName: itemName,
                Details: $"Loot deleted: record removed and DKP refunded. Reason: {Truncate(reason, 800)}",
                EditReason: Truncate(reason, 512),
                SourceTodLootDetailId: sourceTodLootDetailId,
                SourceEventLootDetailId: sourceEventLootDetailId),
            cancellationToken);
    }

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
        DkpPoolRef poolRef,
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

        var oldMembership = await ResolveWinnerAsync(linkshellId, oldWinnerName, cancellationToken);
        var newMembership = await ResolveWinnerAsync(linkshellId, newWinnerName, cancellationToken);

        if (newMembership is null)
        {
            throw new InvalidOperationException(
                $"Winner '{newWinnerName}' is not a member of the linkshell. Pick an active linkshell member.");
        }

        var roundingStep = isHybrid
            ? await GetRoundingStepAsync(linkshellId, cancellationToken)
            : DkpRounding.QuarterStep;

        // The refund and the re-debit go through the SAME poolRef, so an edit always cancels out
        // inside one wallet. Crucially the ref is passed through UNCHANGED rather than resolved to
        // a pinned id: for event loot it's Derived(eventType), so a later remap moves the original
        // debit AND these compensating rows together and the symmetry survives for free. Pinning
        // them here would strand the refund in the old pool.
        //
        // The resolved id below is only used to READ a balance (the Hybrid percentage base).
        var poolId = poolRef.PinnedPoolId
            ?? await _dkpPools.ResolveAsync(linkshellId, poolRef.DerivedFromEventType, cancellationToken);

        var entryContext = new DkpEntryContext(
            EventName: eventName,
            EventType: eventType,
            EventLocation: eventLocation,
            EventStartTime: eventStartTime,
            EventEndTime: eventEndTime,
            ItemName: itemName,
            EditReason: Truncate(reason, 512),
            SourceTodLootDetailId: sourceTodLootDetailId,
            SourceEventLootDetailId: sourceEventLootDetailId);

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
                // No stored actual — a legacy row. That debit was taken as a percentage of the
                // member's TOTAL balance (pools didn't exist), so the inverse has to work off the
                // total too, or it under-refunds.
                var pct = Math.Clamp((double)oldDkpRaw, 0, 100);
                if (pct >= 100d)
                {
                    refundAmount = 0;
                }
                else
                {
                    var legacyBalance = Math.Max(0, oldMembership.LinkshellDkp ?? 0);
                    refundAmount = LootDkpCalculator.ComputeHybridRefund(legacyBalance, pct, roundingStep);
                }
            }
            else
            {
                refundAmount = oldDkpRaw;
            }

            if (refundAmount > 0)
            {
                await _dkpLedger.AppendAsync(
                    oldMembership,
                    "LootEditRefund",
                    refundAmount,
                    occurredAtUtc,
                    poolRef,
                    entryContext with
                    {
                        CharacterName = oldMembership.CharacterName,
                        Details = $"Edit refund: previous loot record corrected. Reason: {Truncate(reason, 800)}",
                    },
                    cancellationToken);
            }
        }

        // ----- Apply the NEW debit -----
        if (newDkpRaw > 0)
        {
            double debitAmount;
            if (isHybrid)
            {
                // Hybrid takes a percentage of a wallet, and the wallet is the pool. The writer's
                // in-request view already includes the refund staged above, so a same-winner edit
                // correctly charges the post-refund balance (and a winner-change edit charges the
                // new winner's untouched one) — the same sequencing the old in-memory read had.
                var pct = Math.Clamp((double)newDkpRaw, 0, 100);
                var poolBalance = Math.Max(0, await _dkpLedger.GetPoolBalanceAsync(newMembership, poolId, cancellationToken));
                debitAmount = LootDkpCalculator.ComputeHybridDebit(poolBalance, pct, roundingStep);
            }
            else
            {
                debitAmount = newDkpRaw;
            }

            if (debitAmount > 0)
            {
                newActualDeducted = debitAmount;
                await _dkpLedger.AppendAsync(
                    newMembership,
                    "LootEditSpent",
                    -debitAmount,
                    occurredAtUtc,
                    poolRef,
                    entryContext with
                    {
                        CharacterName = newMembership.CharacterName,
                        Details = $"Edit spend: new loot record applied. Reason: {Truncate(reason, 800)}",
                    },
                    cancellationToken);
            }
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
