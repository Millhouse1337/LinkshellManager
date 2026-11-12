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
// Only event loot (EventLootDetail) exists: a ToD carries no loot. The DKP math mirrors how that
// loot was charged when it was created -- EventEndService.EndEventCoreAsync, End Camp and
// ManualLootService charge a Hybrid linkshell a percentage of the winner's pool balance and
// everyone else the flat DKP. An edit has to use the same rule, or fixing a Hybrid item's name
// refunds the real cost and re-charges the percent number as flat DKP.
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

    // Resolve a loot winner's membership by any of their names -- the same rule the close, End
    // Camp and the affordability guard charge by (LootDkpGuard.ResolveWinnerAsync, which uses the
    // shared CharacterResolver). This service once matched the roster name alone, so an item won
    // on an alt was debited at close but its edit/delete refund found nobody: the member was
    // permanently short the DKP. One resolver means a refund always finds whoever was charged.
    private Task<AppUserLinkshell?> ResolveWinnerAsync(
        int linkshellId, string? winnerName, CancellationToken cancellationToken)
        => LootDkpGuard.ResolveWinnerAsync(_db, linkshellId, winnerName, cancellationToken);

    // A loot row's winner: by the recorded account (EventLootDetail.ItemWinnerAppUserId) when there
    // is one, so a refund reaches the member who actually paid even after a rename; by name only
    // for rows without it (rows the backfill could not match). An account that has left
    // the linkshell finds nobody -- never a different member who happens to have the name.
    private async Task<AppUserLinkshell?> ResolveRowWinnerAsync(
        int linkshellId, string? winnerAppUserId, string? winnerName, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(winnerAppUserId))
        {
            return await ResolveWinnerAsync(linkshellId, winnerName, cancellationToken);
        }

        return await _db.AppUserLinkshells
            .Include(link => link.AppUser)
            .Where(link => link.LinkshellId == linkshellId && link.AppUserId == winnerAppUserId)
            .OrderBy(link => link.Id)
            .FirstOrDefaultAsync(cancellationToken);
    }

    // Re-run from the top when the winner's balance changed under it (Data/ConcurrencyRetry.cs). A
    // second edit of the same row at the same moment lands here too, and its re-run reads the row as
    // the first edit left it, so the refund is never paid twice.
    public Task<LootEditResult> EditEventLootAsync(
        int lootDetailId,
        LootEditRequest request,
        AppUser actor,
        DateTime occurredAtUtc,
        CancellationToken cancellationToken = default) =>
        ConcurrencyRetry.RunAsync(_db, () => EditEventLootOnceAsync(lootDetailId, request, actor, occurredAtUtc, cancellationToken));

    private async Task<LootEditResult> EditEventLootOnceAsync(
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
        else if (detail.LinkshellId is int ownerLinkshellId)
        {
            // A "No event" row: hand-entered loot the officer chose not to file under anything.
            // It carries its own linkshell precisely so it is editable like any other row, and it
            // has no event metadata to denormalize onto the compensating ledger entries.
            linkshellId = ownerLinkshellId;
            lootStructure = await _db.Linkshells
                .Where(item => item.Id == ownerLinkshellId)
                .Select(item => item.LootStructure)
                .FirstOrDefaultAsync(cancellationToken);
            eventName = null;
            eventType = null;
            eventLocation = null;
            eventStartTime = null;
            eventEndTime = null;
        }
        else
        {
            return new LootEditResult(false, "Loot record is orphaned (no parent event).");
        }

        var structure = ActivityDataController.NormalizeLootStructure(lootStructure ?? "Dkp");

        // The new winner's account. Keeping the same name keeps the recorded account, so a price
        // fix still finds a winner who has since renamed; a new name is matched to a member.
        var keptWinnerAppUserId = string.Equals(newItemWinner, (detail.ItemWinner ?? string.Empty).Trim(), StringComparison.OrdinalIgnoreCase)
            ? detail.ItemWinnerAppUserId
            : null;
        var newWinner = await ResolveRowWinnerAsync(linkshellId, keptWinnerAppUserId, newItemWinner, cancellationToken);

        if (structure == "LootCouncil")
        {
            // Loot Council charges nothing, so any name is allowed; the account is kept when known.
            detail.ItemWinnerAppUserId = newWinner?.AppUserId;
            await ApplyLootCouncilEditAsync(detail, newItemName, newItemWinner, request, actor, occurredAtUtc, cancellationToken);
            return new LootEditResult(true, null, detail.Id, "Event");
        }

        if (newWinner?.AppUserId is null)
        {
            return new LootEditResult(false,
                $"Winner '{newItemWinner}' is not a member of the linkshell. Pick an active linkshell member.");
        }

        // Not charged yet: change the row and move no DKP. ReconcileLootDkpAsync below assumes a
        // debit exists to refund -- on a row the close has not charged it would refund DKP nobody
        // paid and charge the new amount early.
        if (IsPendingCharge(detail))
        {
            var pendingOldWinner = (detail.ItemWinner ?? string.Empty).Trim();
            var pendingOldDkp = detail.WinningDkpSpent.GetValueOrDefault();
            detail.ItemName = newItemName;
            detail.ItemWinner = newItemWinner;
            detail.ItemWinnerAppUserId = newWinner.AppUserId;
            detail.WinningDkpSpent = request.WinningDkpSpent;
            detail.EditedAt = occurredAtUtc;
            detail.EditedByAppUserId = actor.Id;
            detail.EditedByCharacterName = actor.CharacterName ?? actor.UserName;
            detail.LastEditReason = request.Reason.Trim();
            await _db.SaveChangesAsync(cancellationToken);
            _logger.LogInformation(
                "Loot edit applied (Event #{LootDetailId}, not yet charged) by {Actor}: '{OldWinner}' ({OldDkp} DKP) -> '{NewWinner}' ({NewDkp} DKP).",
                detail.Id, actor.Id, pendingOldWinner, pendingOldDkp, newItemWinner, request.WinningDkpSpent);
            return new LootEditResult(true, null, detail.Id, "Event");
        }

        var oldWinnerName = (detail.ItemWinner ?? string.Empty).Trim();
        var oldDkpValue = detail.WinningDkpSpent.GetValueOrDefault();
        var oldActual = detail.ActualDeductedDkp;
        // Event loot is charged a percentage of the pool balance under Hybrid
        // (EventEndService, End Camp, ManualLootService), flat DKP otherwise.
        var isHybrid = structure == "Hybrid";

        var newActualDeducted = await ReconcileLootDkpAsync(
            linkshellId: linkshellId,
            oldWinnerName: oldWinnerName,
            oldDkpRaw: oldDkpValue,
            oldActualDeducted: oldActual,
            newWinnerName: newItemWinner,
            newDkpRaw: request.WinningDkpSpent.Value,
            isHybrid: isHybrid,
            oldWinnerAppUserId: detail.ItemWinnerAppUserId,
            newWinnerAppUserId: newWinner.AppUserId,
            // Event loot FOLLOWS its event type, so a remap moves the original debit and these
            // compensating rows together — the refund can never be stranded in the old pool.
            //
            // Hand-entered loot is the exception: it pins a pool at entry (a "No event" row has no
            // event type to follow at all), so the refund has to go back to the wallet the debit
            // actually came out of rather than to whatever the default resolves to today.
            poolRef: detail.DkpPoolId is int pinnedPool
                ? DkpPoolRef.Pinned(pinnedPool)
                : DkpPoolRef.Derived(eventType),
            sourceEventLootDetailId: detail.Id,
            // Filed under the past event too, so deleting that event reverses this edit with it.
            eventHistoryId: detail.EventHistoryId,
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
        detail.ItemWinnerAppUserId = newWinner.AppUserId;
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

    // Removes an event loot row entirely and refunds the winner the DKP that was debited for it
    // (the "refund the OLD debit" half of an edit, with no replacement debit). LootCouncil rows
    // carry no DKP, so they're just removed. Resolves the parent context
    // (closed EventHistory, active Event, or a "No event" row's own linkshell)
    // the same way EditEventLootAsync does. Under Hybrid the refund is the stored
    // ActualDeductedDkp; only a legacy row without it falls back to the percent inverse.
    // Re-run on a lost balance race, like EditEventLootAsync. A second delete at the same moment
    // re-runs, finds the row gone and refunds nothing.
    public Task<LootEditResult> DeleteEventLootAsync(
        int lootDetailId,
        AppUser actor,
        string reason,
        DateTime occurredAtUtc,
        CancellationToken cancellationToken = default) =>
        ConcurrencyRetry.RunAsync(_db, () => DeleteEventLootOnceAsync(lootDetailId, actor, reason, occurredAtUtc, cancellationToken));

    private async Task<LootEditResult> DeleteEventLootOnceAsync(
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
        else if (detail.LinkshellId is int ownerLinkshellId)
        {
            // A "No event" row: hand-entered loot filed under nothing. It carries its own
            // linkshell for exactly this, and has no event metadata for the refund entry.
            linkshellId = ownerLinkshellId;
            lootStructure = await _db.Linkshells
                .Where(item => item.Id == ownerLinkshellId)
                .Select(item => item.LootStructure)
                .FirstOrDefaultAsync(cancellationToken);
            eventName = null;
            eventType = null;
            eventLocation = null;
            eventStartTime = null;
            eventEndTime = null;
        }
        else
        {
            return new LootEditResult(false, "Loot record is orphaned (no parent event).");
        }

        var structure = ActivityDataController.NormalizeLootStructure(lootStructure ?? "Dkp");
        var winnerName = (detail.ItemWinner ?? string.Empty).Trim();
        var itemName = (detail.ItemName ?? string.Empty).Trim();
        var dkpRaw = detail.WinningDkpSpent.GetValueOrDefault();

        // A row the close has not charged yet has no debit to refund.
        if (structure != "LootCouncil" && !IsPendingCharge(detail))
        {
            await RefundDeletedLootAsync(
                linkshellId: linkshellId,
                winnerName: winnerName,
                winnerAppUserId: detail.ItemWinnerAppUserId,
                dkpRaw: dkpRaw,
                actualDeducted: detail.ActualDeductedDkp,
                isHybrid: structure == "Hybrid",
                // Same rule as EditEventLootAsync: hand-entered loot pinned the pool it was paid
                // from, so the refund goes back there; event loot follows its event type.
                poolRef: detail.DkpPoolId is int pinnedPool
                    ? DkpPoolRef.Pinned(pinnedPool)
                    : DkpPoolRef.Derived(eventType),
                sourceEventLootDetailId: detail.Id,
                // Filed under the past event too, so deleting that event takes this refund back with
                // the charge it undoes -- otherwise the winner kept the refund for an event that is gone.
                eventHistoryId: detail.EventHistoryId,
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
        int? sourceEventLootDetailId,
        int? eventHistoryId,
        string? eventName,
        string? eventType,
        string? eventLocation,
        DateTime? eventStartTime,
        DateTime? eventEndTime,
        string itemName,
        string reason,
        DateTime occurredAtUtc,
        CancellationToken cancellationToken,
        string? winnerAppUserId = null)
    {
        if (string.IsNullOrWhiteSpace(winnerName) || dkpRaw <= 0)
        {
            return;
        }

        var membership = await ResolveRowWinnerAsync(linkshellId, winnerAppUserId, winnerName, cancellationToken);
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
                SourceEventLootDetailId: sourceEventLootDetailId,
                EventHistoryId: eventHistoryId),
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
        EventLootDetail detail,
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

    // Performs the refund/spend ledger pair + AppUserLinkshell.LinkshellDkp
    // updates that an edit implies. Operates on a single row with explicit old/new fields, and tags
    // each ledger entry with the edit reason so the DKP history view can
    // render an "Edited" badge.
    // Returns the new ActualDeducted DKP amount to stamp on the loot row
    // (null when nothing was debited, e.g. when newDkpRaw is 0).
    // Loot on a LIVE event that has not been charged yet. It is charged when the event ends -- by
    // the close paths, or End Camp for an HNM camp -- not when it is posted (ManualLootService), so
    // until then there is no debit behind it: an edit or a delete moves no DKP, and the close
    // charges whatever the row says by then. A closed row (EventHistory set) or one already
    // charged (DkpDebitedAt set) is not pending.
    private static bool IsPendingCharge(EventLootDetail detail)
        => detail.EventHistory is null && detail.Event is not null && detail.DkpDebitedAt is null;

    private async Task<double?> ReconcileLootDkpAsync(
        int linkshellId,
        string oldWinnerName,
        int oldDkpRaw,
        double? oldActualDeducted,
        string newWinnerName,
        int newDkpRaw,
        bool isHybrid,
        DkpPoolRef poolRef,
        int? sourceEventLootDetailId,
        int? eventHistoryId,
        string? eventName,
        string? eventType,
        string? eventLocation,
        DateTime? eventStartTime,
        DateTime? eventEndTime,
        string itemName,
        string reason,
        DateTime occurredAtUtc,
        CancellationToken cancellationToken,
        string? oldWinnerAppUserId = null,
        string? newWinnerAppUserId = null)
    {
        double? newActualDeducted = null;

        var oldMembership = await ResolveRowWinnerAsync(linkshellId, oldWinnerAppUserId, oldWinnerName, cancellationToken);
        var newMembership = await ResolveRowWinnerAsync(linkshellId, newWinnerAppUserId, newWinnerName, cancellationToken);

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
            SourceEventLootDetailId: sourceEventLootDetailId,
            EventHistoryId: eventHistoryId);

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
                    cancellationToken,
                    // Officer intent: correcting an already-awarded item's price. Refusing the
                    // correction because the member has since spent down would leave the ledger
                    // wrong instead of merely overdrawn.
                    DkpOverdraft.Allow);
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
