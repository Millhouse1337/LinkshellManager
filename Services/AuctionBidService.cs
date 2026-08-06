using LinkshellManagerDiscordApp.Data;
using LinkshellManagerDiscordApp.Models;
using Microsoft.EntityFrameworkCore;

namespace LinkshellManagerDiscordApp.Services;

public sealed record AuctionBidResult(bool Success, string? Error, string? ItemName, int Amount);

// Places a bid on an auction item with the same validation as the Activity/web
// bid endpoints (auction live, above the minimum, within available DKP). Used by
// the Discord interactions endpoint for inline modal bidding, where there's no
// controller context to reuse. The MVC/Activity endpoints keep their own
// guild-lock-aware path; this service does a plain linkshell-membership check,
// which is the right gate for a click that originated inside the guild's channel.
public static class AuctionBidService
{
    // custom_id prefixes routed by DiscordInteractionsController:
    //   auc:bid:{auctionItemId}      — button → opens the bid modal
    //   auc:bidmodal:{auctionItemId} — modal submit → places the bid
    public const string BidButtonPrefix = "auc:bid:";
    public const string BidModalPrefix = "auc:bidmodal:";
    public const string BidAmountFieldId = "amount";

    private const int MaxBidAmount = 1_000_000;

    public static async Task<AuctionBidResult> PlaceBidAsync(
        ApplicationDbContext db,
        DkpPoolResolver dkpPools,
        DkpPoolBalanceService dkpPoolBalances,
        string appUserId,
        string characterNameFallback,
        int itemId,
        int bidAmount,
        CancellationToken cancellationToken)
    {
        var item = await db.AuctionItems
            .Include(auctionItem => auctionItem.Auction)
            .Include(auctionItem => auctionItem.Bids)
            .FirstOrDefaultAsync(auctionItem => auctionItem.Id == itemId, cancellationToken);
        if (item is null || item.Auction is null)
        {
            return Fail("That auction item was not found.");
        }

        var membership = await db.AppUserLinkshells
            .FirstOrDefaultAsync(
                link => link.AppUserId == appUserId && link.LinkshellId == item.Auction.LinkshellId,
                cancellationToken);
        if (membership is null)
        {
            return Fail("You're not a member of this linkshell, so you can't bid on its auctions.");
        }

        if (!await CanRankBidAsync(db, item.Auction.LinkshellId, membership.Rank, cancellationToken))
        {
            return Fail("Your role isn't allowed to place bids on auctions.");
        }

        var auctionsLocked = await db.Linkshells
            .Where(l => l.Id == item.Auction.LinkshellId)
            .Select(l => l.AuctionsLocked)
            .FirstOrDefaultAsync(cancellationToken);
        if (auctionsLocked)
        {
            return Fail("Bidding is locked by leadership right now.");
        }

        var now = DateTime.UtcNow;
        var started = item.Auction.StartedAt.HasValue
            || (item.Auction.StartTime.HasValue && now >= item.Auction.StartTime.Value);
        var ended = item.Auction.EndTime.HasValue && now >= item.Auction.EndTime.Value;
        if (!started)
        {
            return Fail("This auction hasn't started yet.");
        }
        if (ended)
        {
            return Fail("This auction has already ended.");
        }

        if (bidAmount <= 0)
        {
            return Fail("Bid amount must be a positive number.");
        }
        if (bidAmount > MaxBidAmount)
        {
            return Fail($"Bid amount cannot exceed {MaxBidAmount:N0}.");
        }

        // The starting bid is the floor — no bid may come in under it. Once there's
        // a high bid, each new bid must also beat that by at least 1.
        var currentHigh = item.CurrentHighestBid ?? 0;
        var startingBid = item.StartingBidDkp ?? 0;
        var minimumBid = Math.Max(startingBid, currentHigh + 1);
        if (bidAmount < minimumBid)
        {
            return Fail(currentHigh > 0 && minimumBid == currentHigh + 1
                ? $"Bid must be greater than the current high bid of {currentHigh}."
                : $"Bid must be at least the starting bid of {startingBid} DKP.");
        }

        // Bids are paid out of the DKP pool the officer picked when they created the auction, so
        // that's the balance to validate against — not the bidder's grand total.
        var map = await dkpPools.GetMapAsync(item.Auction.LinkshellId, cancellationToken);
        var auctionPoolId = item.Auction.DkpPoolId ?? map.DefaultPoolId;
        var availableDkp = await AuctionDkpService.ComputePoolAvailableDkpAsync(
            db, dkpPoolBalances, appUserId, item.Auction.LinkshellId, auctionPoolId,
            cancellationToken, excludeAuctionItemId: itemId);
        if (bidAmount > availableDkp)
        {
            var poolLabel = map.HasMultiplePools ? $" {map.NameFor(auctionPoolId)}" : string.Empty;
            return Fail($"Insufficient available{poolLabel} DKP — you have {availableDkp:0.##} available "
                + "(the rest is locked by bids you're currently winning).");
        }

        var characterName = membership.CharacterName ?? characterNameFallback;
        item.Bids.Add(new Bid
        {
            AuctionItemId = itemId,
            AppUserId = appUserId,
            CharacterName = characterName,
            BidAmount = bidAmount,
            CreatedAt = now
        });
        item.CurrentHighestBid = bidAmount;
        item.CurrentHighestBidder = characterName;
        item.CurrentHighestBidderAppUserId = appUserId;
        item.Status = "BidPlaced";

        await db.SaveChangesAsync(cancellationToken);
        return new AuctionBidResult(true, null, item.ItemName, bidAmount);
    }

    private static AuctionBidResult Fail(string error) => new(false, error, null, 0);

    // True if the given rank may place bids in the linkshell. Defaults to true when
    // the role row is missing (legacy linkshells whose roles were never seeded) so
    // bidding is never accidentally blocked — only an explicit CanBid=false (e.g.
    // the built-in Trial role) stops it. Shared by the web + Discord bid paths.
    public static async Task<bool> CanRankBidAsync(
        ApplicationDbContext db, int linkshellId, string? rank, CancellationToken cancellationToken)
    {
        var rankName = string.IsNullOrWhiteSpace(rank) ? LinkshellRanks.Member : rank.Trim();
        var canBid = await db.LinkshellRoles
            .Where(r => r.LinkshellId == linkshellId && r.Name == rankName)
            .Select(r => (bool?)r.CanBid)
            .FirstOrDefaultAsync(cancellationToken);
        return canBid != false;
    }
}
