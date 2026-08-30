using LinkshellManagerDiscordApp.Data;
using LinkshellManagerDiscordApp.Models;
using Microsoft.EntityFrameworkCore;

namespace LinkshellManagerDiscordApp.Services;

// Which event a hand-entered loot row belongs to.
//
// Three cases, and they are genuinely different rows in the database rather than three readings of
// one nullable column: a LIVE event is an Event, a PAST one is an EventHistory, and None is neither
// (which is why EventLootDetail carries its own LinkshellId now — there would otherwise be nothing
// to tie such a row to a linkshell).
public enum ManualLootTargetKind
{
    None,
    LiveEvent,
    PastEvent,
}

public sealed record ManualLootTarget(ManualLootTargetKind Kind, int? EventId, int? EventHistoryId)
{
    public static readonly ManualLootTarget None = new(ManualLootTargetKind.None, null, null);

    // Parses what the client sent. Anything unrecognised is None, which is the harmless answer:
    // the loot is still recorded and still charged, it just is not filed under an event.
    public static ManualLootTarget Parse(string? kind, int? eventId, int? eventHistoryId)
        => kind?.Trim().ToLowerInvariant() switch
        {
            "live" when eventId is > 0 => new(ManualLootTargetKind.LiveEvent, eventId, null),
            "past" when eventHistoryId is > 0 => new(ManualLootTargetKind.PastEvent, null, eventHistoryId),
            _ => None,
        };
}

public sealed record ManualLootResult(bool Success, string? Error, EventLootDetail? Detail);

// Hand-entered loot from the Loot System.
//
// It used to be written as a TodLootDetail hanging off a SYNTHETIC Tod row whose MonsterName was
// whatever free text the officer typed — which is why every manually added drop showed up in the
// history as source "ToD", and why the ToD Tracker had to be defended against those rows hijacking
// a monster's card. Loot is now filed against a real event (live or past) or against nothing, and
// stored as an EventLootDetail like all other event loot.
//
// THE ONE THING TO KNOW: this debits the winner IMMEDIATELY. Ordinary event loot is charged when
// the event closes; the Add loot form says it deducts now, and loot attached to a PAST event has no
// close left to ride. So the row is stamped DkpDebitedAt and both close paths skip stamped rows —
// otherwise attaching hand-entered loot to a live event would charge the winner twice.
public sealed class ManualLootService
{
    private readonly ApplicationDbContext _db;
    private readonly DkpLedgerWriter _dkpLedger;
    private readonly DkpPoolResolver _dkpPools;
    private readonly DkpPoolBalanceService _dkpPoolBalances;

    public ManualLootService(
        ApplicationDbContext db,
        DkpLedgerWriter dkpLedger,
        DkpPoolResolver dkpPools,
        DkpPoolBalanceService dkpPoolBalances)
    {
        _db = db;
        _dkpLedger = dkpLedger;
        _dkpPools = dkpPools;
        _dkpPoolBalances = dkpPoolBalances;
    }

    // Adds one loot row and charges it. Returns an error string for anything the officer can fix;
    // NOTHING is saved when an error comes back.
    public async Task<ManualLootResult> AddAsync(
        int linkshellId,
        ManualLootTarget target,
        string? itemName,
        string? itemWinner,
        int dkpSpent,
        int? dkpPoolId,
        CancellationToken cancellationToken)
    {
        itemName = itemName?.Trim();
        itemWinner = itemWinner?.Trim();

        if (string.IsNullOrWhiteSpace(itemName))
        {
            return new ManualLootResult(false, "Item name is required.", null);
        }
        if (string.IsNullOrWhiteSpace(itemWinner))
        {
            return new ManualLootResult(false, "A winner is required.", null);
        }
        if (dkpSpent < 0)
        {
            return new ManualLootResult(false, "DKP spent can't be negative.", null);
        }

        // The event has to belong to THIS linkshell. The id arrives from a client, and filing one
        // shell's loot under another's event would put it in the wrong history.
        var resolved = await ResolveTargetAsync(linkshellId, target, cancellationToken);
        if (resolved.Error is not null)
        {
            return new ManualLootResult(false, resolved.Error, null);
        }

        var members = await _db.AppUserLinkshells
            .Include(link => link.AppUser)
            .Where(link => link.LinkshellId == linkshellId && link.AppUserId != null)
            .ToListAsync(cancellationToken);

        // Main OR either alt, matched in memory so the comparison doesn't depend on DB collation.
        // Mirrors SubmitLootDetails: the typed name is stored as-is so the log shows who actually
        // won, while the DKP comes off the owning account.
        var winner = members.FirstOrDefault(link =>
            string.Equals(link.CharacterName, itemWinner, StringComparison.OrdinalIgnoreCase)
            || string.Equals(link.AppUser?.AltCharacterName1, itemWinner, StringComparison.OrdinalIgnoreCase)
            || string.Equals(link.AppUser?.AltCharacterName2, itemWinner, StringComparison.OrdinalIgnoreCase));
        if (winner?.AppUserId is null)
        {
            return new ManualLootResult(false, "Winner must be a current linkshell member.", null);
        }

        var linkshell = await _db.Linkshells
            .AsNoTracking()
            .FirstOrDefaultAsync(item => item.Id == linkshellId, cancellationToken);
        var isLootCouncil = string.Equals(linkshell?.LootStructure, "LootCouncil", StringComparison.OrdinalIgnoreCase);
        var isHybrid = string.Equals(linkshell?.LootStructure, "Hybrid", StringComparison.OrdinalIgnoreCase);

        var map = await _dkpPools.GetMapAsync(linkshellId, cancellationToken);
        // An explicit pick wins; otherwise follow the event's own type, and fall back to whatever
        // HNM maps to for a row with no event — which is what manual loot has always defaulted to.
        var poolId = dkpPoolId is int chosen && map.Pools.Any(pool => pool.Id == chosen)
            ? chosen
            : map.Resolve(resolved.EventType ?? "HNM");

        var detail = new EventLootDetail
        {
            LinkshellId = linkshellId,
            EventId = resolved.EventId,
            EventHistoryId = resolved.EventHistoryId,
            ItemName = itemName,
            ItemWinner = winner.CharacterName ?? itemWinner,
            WinningDkpSpent = dkpSpent,
            DkpPoolId = poolId,
        };

        // Loot Council records history and spends nothing, so there is no balance to check and no
        // debit to stamp. Leaving DkpDebitedAt null there would be wrong for a different reason —
        // a close would then try to charge it — so it is stamped either way.
        var nowUtc = DateTime.UtcNow;
        detail.DkpDebitedAt = nowUtc;

        if (!isLootCouncil && dkpSpent > 0)
        {
            // Same computation the event-loot guard uses, so a hand-entered row and an event-awarded
            // one agree about what a member can afford: balance minus auction-locked DKP minus loot
            // already promised in still-open events of the same pool.
            var available = await AuctionDkpService.ComputePoolAvailableDkpAsync(
                _db, _dkpPoolBalances, winner.AppUserId!, linkshellId, poolId, cancellationToken);

            double amount;
            string details;
            if (isHybrid)
            {
                var pct = Math.Clamp((double)dkpSpent, 0, 100);
                var roundingStep = DkpRounding.StepFor(linkshell?.DkpRoundingIncrement);
                var balance = Math.Max(0, await _dkpLedger.GetPoolBalanceAsync(winner, poolId, cancellationToken));
                amount = -LootDkpCalculator.ComputeHybridDebit(balance, pct, roundingStep);
                details = $"Hybrid DKP spent ({pct}%, {Math.Abs(amount):0.##} DKP) on loot: {itemName}.";
            }
            else
            {
                amount = -(double)dkpSpent;
                details = $"DKP spent on loot: {itemName}.";
            }

            // Checked here rather than left to DkpLedgerWriter's backstop so the officer gets a
            // sentence they can act on instead of an exception.
            if (Math.Abs(amount) > available + 0.0001)
            {
                return new ManualLootResult(
                    false,
                    $"{itemWinner} only has {available:0.##} DKP available in that pool "
                    + $"(this costs {Math.Abs(amount):0.##}).",
                    null);
            }

            detail.ActualDeductedDkp = Math.Abs(amount);

            _db.EventLootDetails.Add(detail);
            await _dkpLedger.AppendAsync(
                winner,
                "LootSpent",
                amount,
                nowUtc,
                // PINNED, not derived: a "No event" row has no event type to follow, and a refund
                // has to credit the same wallet the debit came out of even after a remap.
                DkpPoolRef.Pinned(poolId),
                new DkpEntryContext(
                    CharacterName: winner.CharacterName,
                    EventName: resolved.EventName,
                    EventType: resolved.EventType,
                    EventHistoryId: resolved.EventHistoryId,
                    ItemName: itemName,
                    Details: details,
                    SourceEventLootDetailId: null),
                cancellationToken);
        }
        else
        {
            _db.EventLootDetails.Add(detail);
        }

        await _db.SaveChangesAsync(cancellationToken);
        return new ManualLootResult(true, null, detail);
    }

    private sealed record ResolvedTarget(
        int? EventId, int? EventHistoryId, string? EventName, string? EventType, string? Error);

    private async Task<ResolvedTarget> ResolveTargetAsync(
        int linkshellId, ManualLootTarget target, CancellationToken cancellationToken)
    {
        switch (target.Kind)
        {
            case ManualLootTargetKind.LiveEvent:
            {
                var evt = await _db.Events
                    .AsNoTracking()
                    .Where(item => item.Id == target.EventId && item.LinkshellId == linkshellId)
                    .Select(item => new { item.Id, item.EventName, item.EventType })
                    .FirstOrDefaultAsync(cancellationToken);
                return evt is null
                    ? new ResolvedTarget(null, null, null, null, "That event no longer exists.")
                    : new ResolvedTarget(evt.Id, null, evt.EventName, evt.EventType, null);
            }
            case ManualLootTargetKind.PastEvent:
            {
                var history = await _db.EventHistories
                    .AsNoTracking()
                    .Where(item => item.Id == target.EventHistoryId && item.LinkshellId == linkshellId)
                    .Select(item => new { item.Id, item.EventName, item.EventType })
                    .FirstOrDefaultAsync(cancellationToken);
                return history is null
                    ? new ResolvedTarget(null, null, null, null, "That past event no longer exists.")
                    : new ResolvedTarget(null, history.Id, history.EventName, history.EventType, null);
            }
            default:
                return new ResolvedTarget(null, null, null, null, null);
        }
    }
}
