using LinkshellManagerDiscordApp.Data;
using LinkshellManagerDiscordApp.Models;
using Microsoft.EntityFrameworkCore;

namespace LinkshellManagerDiscordApp.Services;

// Available DKP = total (AppUserLinkshell.LinkshellDkp) minus the DKP locked
// by the bids the member is *currently winning* across all not-yet-closed
// auction items in the linkshell. Being outbid releases the lock because
// CurrentHighestBidderAppUserId no longer points at the member; closing an
// auction deletes its items so they stop contributing. Nothing is stored —
// the value is always derived so there is no reconciliation risk.
public static class AuctionDkpService
{
    public static async Task<double> ComputeAvailableDkpAsync(
        ApplicationDbContext db,
        string appUserId,
        int linkshellId,
        CancellationToken cancellationToken,
        int? excludeAuctionItemId = null)
    {
        var total = await db.AppUserLinkshells
            .Where(m => m.AppUserId == appUserId && m.LinkshellId == linkshellId)
            .Select(m => m.LinkshellDkp ?? 0d)
            .FirstOrDefaultAsync(cancellationToken);

        var locked = await db.AuctionItems
            .Where(ai =>
                ai.Auction != null
                && ai.Auction.LinkshellId == linkshellId
                && ai.CurrentHighestBidderAppUserId == appUserId
                && ai.CurrentHighestBid != null
                && ai.CurrentHighestBid > 0
                && (excludeAuctionItemId == null || ai.Id != excludeAuctionItemId.Value))
            .SumAsync(ai => (double)(ai.CurrentHighestBid ?? 0), cancellationToken);

        // DKP spent on loot during a still-live event isn't deducted from the ledger until
        // the event ends (EndEventCoreAsync). Block that pending spend from biddable now so
        // members can't bid DKP they've already committed to loot mid-event.
        var pendingLootSpend = await ComputePendingLiveLootSpendAsync(db, appUserId, linkshellId, cancellationToken);
        return total - locked - pendingLootSpend;
    }

    // DKP a member has SPENT on loot in currently-live (commenced, not-yet-ended) events
    // that hasn't been committed to the ledger yet. It's "pending" — surfaced on the DKP
    // page as a deduction — and already removed from biddable power (ComputeAvailableDkpAsync)
    // so they can't double-spend it before the event closes. Returns 0 when nothing pends.
    public static async Task<double> ComputePendingLiveLootSpendAsync(
        ApplicationDbContext db,
        string appUserId,
        int linkshellId,
        CancellationToken cancellationToken)
    {
        var byUser = await ComputePendingLiveLootSpendByUserAsync(db, linkshellId, cancellationToken);
        return byUser.GetValueOrDefault(appUserId);
    }

    // Batch ComputePendingLiveLootSpendAsync for a whole linkshell: AppUserId -> pending
    // (uncommitted) live-event loot spend. Event loot winners are stored by CHARACTER NAME,
    // so each winner is resolved to an account via the membership's main + alt names (alts
    // share the account — mirrors EndEventCoreAsync's ResolveLootWinnerMembership). Only
    // members with a non-zero pending spend appear.
    public static async Task<Dictionary<string, double>> ComputePendingLiveLootSpendByUserAsync(
        ApplicationDbContext db,
        int linkshellId,
        CancellationToken cancellationToken)
    {
        // WinningDkpSpent is an absolute DKP cost only under the "Dkp" loot structure.
        // Under Hybrid it's a PERCENTAGE (real cost depends on the live balance, which
        // can't be cheaply projected) and LootCouncil has no DKP economy — so pending-loot
        // blocking doesn't apply to those; skip to avoid mis-valuing biddable/affordability.
        var lootStructure = await db.Linkshells
            .Where(l => l.Id == linkshellId)
            .Select(l => l.LootStructure)
            .FirstOrDefaultAsync(cancellationToken);
        if (!string.Equals(lootStructure, "Dkp", StringComparison.OrdinalIgnoreCase))
        {
            return new Dictionary<string, double>(StringComparer.Ordinal);
        }

        var pendingLoot = await db.EventLootDetails
            .Where(l => l.Event != null
                && l.Event.LinkshellId == linkshellId
                && l.Event.CommencementStartTime != null   // live: commenced, not yet ended
                && l.WinningDkpSpent != null && l.WinningDkpSpent > 0
                && l.ItemWinner != null)
            .Select(l => new { l.ItemWinner, Dkp = l.WinningDkpSpent!.Value })
            .ToListAsync(cancellationToken);
        if (pendingLoot.Count == 0)
        {
            return new Dictionary<string, double>(StringComparer.Ordinal);
        }

        var memberships = await db.AppUserLinkshells
            .Include(m => m.AppUser)
            .Where(m => m.LinkshellId == linkshellId && m.AppUserId != null)
            .ToListAsync(cancellationToken);
        var appUserIdByName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var m in memberships)
        {
            void Map(string? name)
            {
                var key = name?.Trim();
                if (!string.IsNullOrEmpty(key) && !appUserIdByName.ContainsKey(key))
                {
                    appUserIdByName[key] = m.AppUserId!;
                }
            }
            Map(m.CharacterName);
            Map(m.AppUser?.CharacterName);
            Map(m.AppUser?.AltCharacterName1);
            Map(m.AppUser?.AltCharacterName2);
        }

        var result = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var loot in pendingLoot)
        {
            if (appUserIdByName.TryGetValue(loot.ItemWinner!.Trim(), out var appUserId))
            {
                result[appUserId] = result.GetValueOrDefault(appUserId) + loot.Dkp;
            }
        }
        return result;
    }

    // Batch version of the "locked" half of ComputeAvailableDkpAsync for a whole
    // linkshell: AppUserId -> DKP committed to bids the member is currently
    // winning on not-yet-closed auctions. Used by the DKP template export to fill
    // the "Biddable DKP" column (= Current DKP minus this) in a single query.
    public static async Task<Dictionary<string, double>> ComputeCommittedDkpByUserAsync(
        ApplicationDbContext db,
        int linkshellId,
        CancellationToken cancellationToken)
    {
        var rows = await db.AuctionItems
            .Where(ai =>
                ai.Auction != null
                && ai.Auction.LinkshellId == linkshellId
                && ai.CurrentHighestBidderAppUserId != null
                && ai.CurrentHighestBid != null
                && ai.CurrentHighestBid > 0)
            .GroupBy(ai => ai.CurrentHighestBidderAppUserId!)
            .Select(g => new { AppUserId = g.Key, Locked = g.Sum(ai => (double)(ai.CurrentHighestBid ?? 0)) })
            .ToListAsync(cancellationToken);

        return rows.ToDictionary(r => r.AppUserId, r => r.Locked, StringComparer.Ordinal);
    }

    // AppUserId -> BIDDABLE DKP for a whole linkshell: the single source of truth used by
    // the roster + live-event UIs. Biddable = committed balance (LinkshellDkp) − DKP locked
    // by bids the member is currently winning − DKP they've spent on loot in a still-live
    // event (not yet committed). Matches the per-member ComputeAvailableDkpAsync. Every
    // member appears (defaulting to their committed balance when nothing is locked/pending).
    public static async Task<Dictionary<string, double>> ComputeBiddableDkpByUserAsync(
        ApplicationDbContext db,
        int linkshellId,
        CancellationToken cancellationToken)
    {
        var committed = await db.AppUserLinkshells
            .Where(m => m.LinkshellId == linkshellId && m.AppUserId != null)
            .Select(m => new { m.AppUserId, Dkp = m.LinkshellDkp ?? 0d })
            .ToListAsync(cancellationToken);
        var locked = await ComputeCommittedDkpByUserAsync(db, linkshellId, cancellationToken);
        var pendingLoot = await ComputePendingLiveLootSpendByUserAsync(db, linkshellId, cancellationToken);

        var result = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var m in committed)
        {
            result[m.AppUserId!] = m.Dkp
                - locked.GetValueOrDefault(m.AppUserId!)
                - pendingLoot.GetValueOrDefault(m.AppUserId!);
        }
        return result;
    }

    // ---- pool-scoped ------------------------------------------------------------------
    //
    // The same three ideas, narrowed to ONE DKP pool: what a member can actually spend when the
    // thing they're spending on draws from a specific wallet.
    //
    // A pool can never let you spend more than your linkshell-wide available — hence the Math.Min
    // clamp. With a single pool that's Math.Min(x, x) == x, so these return exactly what the
    // linkshell-wide versions above return, and every guard, bid and loot award behaves as it did
    // before pools existed. The clamp is also the safety net for members whose ledger predates
    // their current membership (a kick-and-rejoin), where the derived pool balance could otherwise
    // exceed the balance they actually have.

    public static async Task<double> ComputePoolAvailableDkpAsync(
        ApplicationDbContext db,
        DkpPoolBalanceService balances,
        string appUserId,
        int linkshellId,
        int dkpPoolId,
        CancellationToken cancellationToken,
        int? excludeAuctionItemId = null)
    {
        var linkshellWide = await ComputeAvailableDkpAsync(db, appUserId, linkshellId, cancellationToken, excludeAuctionItemId);

        var poolBalance = await balances.GetCommittedAsync(linkshellId, appUserId, dkpPoolId, cancellationToken);
        var poolLocked = await ComputePoolCommittedDkpByUserAsync(db, linkshellId, dkpPoolId, cancellationToken, excludeAuctionItemId);
        var poolPending = await ComputePoolPendingLiveLootSpendByUserAsync(db, linkshellId, dkpPoolId, cancellationToken);

        var poolAvailable = poolBalance
            - poolLocked.GetValueOrDefault(appUserId)
            - poolPending.GetValueOrDefault(appUserId);

        return Math.Min(poolAvailable, linkshellWide);
    }

    // Batch pool-scoped biddable for a whole linkshell — the roster / live-event / DKP-sheet path.
    public static async Task<Dictionary<string, double>> ComputePoolBiddableDkpByUserAsync(
        ApplicationDbContext db,
        DkpPoolBalanceService balances,
        int linkshellId,
        int dkpPoolId,
        CancellationToken cancellationToken)
    {
        var linkshellWide = await ComputeBiddableDkpByUserAsync(db, linkshellId, cancellationToken);
        var poolBalances = await balances.GetByAppUserAsync(linkshellId, cancellationToken);
        var poolLocked = await ComputePoolCommittedDkpByUserAsync(db, linkshellId, dkpPoolId, cancellationToken);
        var poolPending = await ComputePoolPendingLiveLootSpendByUserAsync(db, linkshellId, dkpPoolId, cancellationToken);

        var result = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var (appUserId, wide) in linkshellWide)
        {
            var poolBalance = poolBalances.TryGetValue(appUserId, out var byPool)
                ? byPool.GetValueOrDefault(dkpPoolId)
                : 0d;
            var poolAvailable = poolBalance
                - poolLocked.GetValueOrDefault(appUserId)
                - poolPending.GetValueOrDefault(appUserId);
            result[appUserId] = Math.Min(poolAvailable, wide);
        }
        return result;
    }

    // DKP locked in winning bids on auctions that draw from THIS pool. A null Auction.DkpPoolId
    // means the default pool, so it only counts when this IS the default pool.
    private static async Task<Dictionary<string, double>> ComputePoolCommittedDkpByUserAsync(
        ApplicationDbContext db,
        int linkshellId,
        int dkpPoolId,
        CancellationToken cancellationToken,
        int? excludeAuctionItemId = null)
    {
        var defaultPoolId = await db.DkpPools
            .Where(pool => pool.LinkshellId == linkshellId && pool.IsDefault)
            .Select(pool => (int?)pool.Id)
            .FirstOrDefaultAsync(cancellationToken);
        var poolIsDefault = defaultPoolId == dkpPoolId;

        var rows = await db.AuctionItems
            .Where(ai =>
                ai.Auction != null
                && ai.Auction.LinkshellId == linkshellId
                && ai.CurrentHighestBidderAppUserId != null
                && ai.CurrentHighestBid != null
                && ai.CurrentHighestBid > 0
                && (excludeAuctionItemId == null || ai.Id != excludeAuctionItemId.Value)
                && (ai.Auction.DkpPoolId == dkpPoolId || (poolIsDefault && ai.Auction.DkpPoolId == null)))
            .GroupBy(ai => ai.CurrentHighestBidderAppUserId!)
            .Select(g => new { AppUserId = g.Key, Locked = g.Sum(ai => (double)(ai.CurrentHighestBid ?? 0)) })
            .ToListAsync(cancellationToken);

        return rows.ToDictionary(r => r.AppUserId, r => r.Locked, StringComparer.Ordinal);
    }

    // Pending live-event loot spend, but only from events whose type maps to THIS pool.
    private static async Task<Dictionary<string, double>> ComputePoolPendingLiveLootSpendByUserAsync(
        ApplicationDbContext db,
        int linkshellId,
        int dkpPoolId,
        CancellationToken cancellationToken)
    {
        // Same early-out as the linkshell-wide version: WinningDkpSpent is only an absolute cost
        // under "Dkp". Under Hybrid it's a percentage whose real value can't be cheaply projected,
        // and LootCouncil has no DKP economy at all.
        var lootStructure = await db.Linkshells
            .Where(l => l.Id == linkshellId)
            .Select(l => l.LootStructure)
            .FirstOrDefaultAsync(cancellationToken);
        if (!string.Equals(lootStructure, "Dkp", StringComparison.OrdinalIgnoreCase))
        {
            return new Dictionary<string, double>(StringComparer.Ordinal);
        }

        var pendingLoot = await db.EventLootDetails
            .Where(l => l.Event != null
                && l.Event.LinkshellId == linkshellId
                && l.Event.CommencementStartTime != null   // live: commenced, not yet ended
                && l.WinningDkpSpent != null && l.WinningDkpSpent > 0
                && l.ItemWinner != null)
            .Select(l => new { l.ItemWinner, Dkp = l.WinningDkpSpent!.Value, EventType = l.Event!.EventType })
            .ToListAsync(cancellationToken);
        if (pendingLoot.Count == 0)
        {
            return new Dictionary<string, double>(StringComparer.Ordinal);
        }

        // Resolve each live event's type to a pool with the linkshell's current mapping.
        var defaultPoolId = await db.DkpPools
            .Where(pool => pool.LinkshellId == linkshellId && pool.IsDefault)
            .Select(pool => (int?)pool.Id)
            .FirstOrDefaultAsync(cancellationToken) ?? 0;
        var mappings = await db.DkpPoolEventTypes
            .Where(mapping => mapping.LinkshellId == linkshellId)
            .Select(mapping => new { mapping.NormalizedEventType, mapping.DkpPoolId })
            .ToListAsync(cancellationToken);
        var poolByEventType = mappings.ToDictionary(
            mapping => mapping.NormalizedEventType, mapping => mapping.DkpPoolId, StringComparer.Ordinal);

        int PoolFor(string? eventType)
        {
            var key = DkpPoolEventType.Normalize(eventType);
            return key.Length > 0 && poolByEventType.TryGetValue(key, out var id) ? id : defaultPoolId;
        }

        var memberships = await db.AppUserLinkshells
            .Include(m => m.AppUser)
            .Where(m => m.LinkshellId == linkshellId && m.AppUserId != null)
            .ToListAsync(cancellationToken);
        var appUserIdByName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var m in memberships)
        {
            void Map(string? name)
            {
                var key = name?.Trim();
                if (!string.IsNullOrEmpty(key) && !appUserIdByName.ContainsKey(key))
                {
                    appUserIdByName[key] = m.AppUserId!;
                }
            }
            Map(m.CharacterName);
            Map(m.AppUser?.CharacterName);
            Map(m.AppUser?.AltCharacterName1);
            Map(m.AppUser?.AltCharacterName2);
        }

        var result = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var loot in pendingLoot)
        {
            if (PoolFor(loot.EventType) != dkpPoolId)
            {
                continue;
            }
            if (appUserIdByName.TryGetValue(loot.ItemWinner!.Trim(), out var appUserId))
            {
                result[appUserId] = result.GetValueOrDefault(appUserId) + loot.Dkp;
            }
        }
        return result;
    }
}
