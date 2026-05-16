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

    public sealed record UndoBidOutcome(
        bool Ok,
        string? Error,
        string? NewWinnerCharacterName,
        string? NewWinnerAppUserId,
        int? NewWinningBid);

    // Removes the caller's current winning bid on an item, promotes the next
    // highest remaining bid to winner ("2nd place"), and raises the per-member
    // in-game loot block. Callers must already have verified the auction is
    // live and that `appUserId` is the current high bidder; `item.Bids` must
    // be eager-loaded and `membership` must be the caller's row for the
    // auction's linkshell. The caller saves the context.
    public static UndoBidOutcome UndoWinningBid(
        ApplicationDbContext db,
        AuctionItem item,
        string appUserId,
        AppUserLinkshell membership,
        int cooldownHours,
        DateTime nowUtc)
    {
        if (item.CurrentHighestBidderAppUserId != appUserId)
        {
            return new UndoBidOutcome(false,
                "You are not the current high bidder on this item.", null, null, null);
        }

        var winningBid = item.Bids
            .Where(b => b.AppUserId == appUserId)
            .OrderByDescending(b => b.BidAmount)
            .ThenByDescending(b => b.CreatedAt)
            .FirstOrDefault();
        if (winningBid is null)
        {
            return new UndoBidOutcome(false,
                "No bid of yours to undo on this item.", null, null, null);
        }

        db.Bids.Remove(winningBid);
        item.Bids.Remove(winningBid);

        var next = item.Bids
            .OrderByDescending(b => b.BidAmount)
            .ThenByDescending(b => b.CreatedAt)
            .FirstOrDefault();

        if (next is null)
        {
            item.CurrentHighestBid = null;
            item.CurrentHighestBidder = null;
            item.CurrentHighestBidderAppUserId = null;
            item.Status = "Pending";
        }
        else
        {
            item.CurrentHighestBid = next.BidAmount;
            item.CurrentHighestBidder = next.CharacterName;
            item.CurrentHighestBidderAppUserId = next.AppUserId;
            item.Status = "BidPlaced";
        }

        // Anti-abuse: block the undoing member from in-game loot wins for the
        // configured window. cooldownHours <= 0 disables the block entirely.
        if (cooldownHours > 0)
        {
            membership.LootBiddingBlockedUntil = nowUtc.AddHours(cooldownHours);
        }

        return new UndoBidOutcome(true, null,
            item.CurrentHighestBidder, item.CurrentHighestBidderAppUserId,
            item.CurrentHighestBid);
    }
}
