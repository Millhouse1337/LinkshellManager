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

        return total - locked;
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
}
