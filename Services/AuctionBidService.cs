using LinkshellManagerDiscordApp.Data;
using LinkshellManagerDiscordApp.Models;
using Microsoft.EntityFrameworkCore;

namespace LinkshellManagerDiscordApp.Services;

public sealed record AuctionBidResult(bool Success, string? Error, string? ItemName, int Amount, Bid? Bid = null);

// Places a bid on an auction item. Used by the Discord interactions endpoint for inline modal
// bidding (PlaceBidAsync: a plain linkshell-membership check, which is the right gate for a click
// that originated inside the guild's channel) and by the Activity bid endpoint (which runs its own
// guild-lock-aware gate first). Both then go through CommitBidAsync, so the amount rules and the
// locking live in one place.
public static class AuctionBidService
{
    // custom_id prefixes routed by AuctionInteractionHandler (Controllers/Discord):
    //   auc:pick:{auctionId}         — the card's item select → opens the bid modal for the pick
    //   auc:mybids:{auctionId}       — button → the clicker's bids on this auction (ephemeral)
    //   auc:bid:{auctionItemId}      — the old per-item button (cards posted before the select;
    //                                  they switch over on their next edit) → opens the bid modal
    //   auc:bidmodal:{auctionItemId} — modal submit → places the bid
    public const string PickSelectPrefix = "auc:pick:";
    public const string MyBidsPrefix = "auc:mybids:";
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
        // Read-only: CommitBidAsync reads the item again under its lock.
        var item = await db.AuctionItems
            .AsNoTracking()
            .Include(auctionItem => auctionItem.Auction)
            .FirstOrDefaultAsync(auctionItem => auctionItem.Id == itemId, cancellationToken);
        if (item is null || item.Auction is null)
        {
            return Fail("That auction item was not found.");
        }

        var membership = await db.AppUserLinkshells
            .AsNoTracking()
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

        var characterName = membership.CharacterName ?? characterNameFallback;
        return await CommitBidAsync(
            db, dkpPools, dkpPoolBalances, appUserId, characterName,
            item.Auction.LinkshellId, itemId, bidAmount, now, cancellationToken);
    }

    // The part of a bid that can race: check the amount against the item's current high bid and
    // the bidder's available DKP, then save. The caller has already checked who may bid and that
    // the auction is live.
    //
    // Two row locks, held until the bid commits (in-memory tests have neither, and bid one at a time):
    //   1. the bidder's membership. One member's bids take turns, so two bids at once on two items
    //      can't each see the full balance and together commit more DKP than the member has.
    //   2. the item. Bids on one item take turns, so the later one reads the high bid the earlier
    //      one saved. Before, Alice (60) and Bob (55) could both beat a high of 50; if Bob's save
    //      landed last the item showed Bob at 55, and Bob won with the lower bid.
    // Every bid takes them in that order and holds one of each, so two bids never wait on each
    // other in a circle. A row lock does not change xmin, so it never trips the membership's
    // concurrency token for anyone else.
    public static async Task<AuctionBidResult> CommitBidAsync(
        ApplicationDbContext db,
        DkpPoolResolver dkpPools,
        DkpPoolBalanceService dkpPoolBalances,
        string appUserId,
        string characterName,
        int linkshellId,
        int itemId,
        int bidAmount,
        DateTime nowUtc,
        CancellationToken cancellationToken)
    {
        if (bidAmount <= 0)
        {
            return Fail("Bid amount must be a positive number.");
        }
        if (bidAmount > MaxBidAmount)
        {
            return Fail($"Bid amount cannot exceed {MaxBidAmount:N0}.");
        }

        var relational = db.Database.IsRelational();
        await using var transaction = relational
            ? await db.Database.BeginTransactionAsync(cancellationToken)
            : null;
        if (relational)
        {
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"""SELECT 1 FROM "AppUserLinkshells" WHERE "AppUserId" = {appUserId} AND "LinkshellId" = {linkshellId} FOR UPDATE""",
                cancellationToken);
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"""SELECT 1 FROM "AuctionItems" WHERE "Id" = {itemId} FOR UPDATE""",
                cancellationToken);
        }

        // The caller may have read the item before the lock. Forget any copy it is tracking so this
        // reads the high bid as it is now (another bid may have just committed), not a moment ago.
        db.ChangeTracker.Clear();

        var item = await db.AuctionItems
            .Include(auctionItem => auctionItem.Auction)
            .FirstOrDefaultAsync(auctionItem => auctionItem.Id == itemId, cancellationToken);
        // Closing an auction deletes its items, and an officer may have ended it while this bid waited.
        if (item is null || item.Auction is null || item.Auction.LinkshellId != linkshellId)
        {
            return Fail("That auction item was not found.");
        }
        // Read the clock again: nowUtc was taken before this bid waited for its locks. Ending an
        // auction early also writes its items, so a bid queued behind that End wakes up to an
        // EndTime later than nowUtc, and the old clock would let it in after the auction ended.
        var utcNow = DateTime.UtcNow;
        var checkedAt = utcNow > nowUtc ? utcNow : nowUtc;
        if (item.Auction.EndTime.HasValue && checkedAt >= item.Auction.EndTime.Value)
        {
            return Fail("This auction has already ended.");
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
        // that's the balance to validate against — not the bidder's grand total. Available = that
        // balance minus DKP locked by bids the member is winning on OTHER items. This item is left
        // out so raising a bid you already hold compares against the replacement, not old + new.
        var map = await dkpPools.GetMapAsync(linkshellId, cancellationToken);
        var auctionPoolId = item.Auction.DkpPoolId ?? map.DefaultPoolId;
        var availableDkp = await AuctionDkpService.ComputePoolAvailableDkpAsync(
            db, dkpPoolBalances, appUserId, linkshellId, auctionPoolId,
            cancellationToken, excludeAuctionItemId: itemId);
        if (bidAmount > availableDkp)
        {
            var poolLabel = map.HasMultiplePools ? $" {map.NameFor(auctionPoolId)}" : string.Empty;
            return Fail($"Insufficient available{poolLabel} DKP — you have {availableDkp:0.##} available "
                + "(the rest is locked by bids you're currently winning).");
        }

        var bid = new Bid
        {
            AuctionItemId = itemId,
            AppUserId = appUserId,
            CharacterName = characterName,
            BidAmount = bidAmount,
            CreatedAt = nowUtc
        };
        db.Bids.Add(bid);
        item.CurrentHighestBid = bidAmount;
        item.CurrentHighestBidder = characterName;
        item.CurrentHighestBidderAppUserId = appUserId;
        item.Status = "BidPlaced";

        if (transaction is null)
        {
            await db.SaveChangesAsync(cancellationToken);
            return new AuctionBidResult(true, null, item.ItemName, bidAmount, bid);
        }

        // The Discord card edit and live refresh for this bid wait for the COMMIT, so nothing reads
        // the item before the new high bid is visible (ApplicationDbContext.HoldBidAnnouncements).
        db.HoldBidAnnouncements();
        try
        {
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            await db.ReleaseHeldBidAnnouncementsAsync();
        }
        finally
        {
            db.DropHeldBidAnnouncements();
        }
        return new AuctionBidResult(true, null, item.ItemName, bidAmount, bid);
    }

    private static AuctionBidResult Fail(string error) => new(false, error, null, 0);

    // True if the given rank may place bids in the linkshell. Defaults to true when
    // the role row is missing (legacy linkshells whose roles were never seeded) so
    // bidding is never accidentally blocked — only an explicit CanBid=false (e.g.
    // the built-in Trial role) stops it.
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
