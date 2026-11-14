using System.Text.Json;
using LinkshellManagerDiscordApp.Data;
using LinkshellManagerDiscordApp.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace LinkshellManagerDiscordApp.Controllers.Discord;

// The auction card in Discord: the item picker and Bid button (both open the bid-amount modal),
// "My bids", and the bid modal's submit, which places the bid.
public sealed class AuctionInteractionHandler : DiscordInteractionHandlerBase
{
    private readonly ApplicationDbContext _db;
    private readonly DkpPoolResolver _dkpPools;
    private readonly DkpPoolBalanceService _dkpPoolBalances;

    public AuctionInteractionHandler(
        DiscordInteractionContext interaction,
        ApplicationDbContext db,
        DkpPoolResolver dkpPools,
        DkpPoolBalanceService dkpPoolBalances)
        : base(interaction)
    {
        _db = db;
        _dkpPools = dkpPools;
        _dkpPoolBalances = dkpPoolBalances;
    }

    // Returns a Discord modal (type 9) asking for the bid amount. custom_id
    // carries the auction item id so the submit handler knows what to bid on.
    //
    // The label and placeholder name the DKP pool this auction draws from and how much the bidder
    // has in it — otherwise someone in a linkshell with separate Sky and Sea wallets has no way to
    // know which one they're spending, and finds out only when the bid is rejected.
    internal async Task<IActionResult> BidModalAsync(int itemId, string? appUserId, CancellationToken cancellationToken)
    {
        if (itemId <= 0)
        {
            return Ephemeral("That auction item isn't recognized.");
        }

        // Both default to the plain, pool-free text this modal has always shown. A linkshell with
        // one pool never sees a difference, and if we can't resolve the caller we quietly fall back
        // rather than block the bid.
        var label = "Your bid (DKP)";
        var placeholder = "e.g. 100";

        var auction = await _db.AuctionItems
            .Where(item => item.Id == itemId)
            .Select(item => new { item.Auction!.LinkshellId, item.Auction.DkpPoolId })
            .FirstOrDefaultAsync(cancellationToken);

        if (auction is not null && !string.IsNullOrWhiteSpace(appUserId))
        {
            var map = await _dkpPools.GetMapAsync(auction.LinkshellId, cancellationToken);
            if (map.HasMultiplePools)
            {
                var poolId = auction.DkpPoolId ?? map.DefaultPoolId;
                var poolName = map.NameFor(poolId);
                var available = await AuctionDkpService.ComputePoolAvailableDkpAsync(
                    _db, _dkpPoolBalances, appUserId, auction.LinkshellId, poolId, cancellationToken);

                // Discord caps a text input's label at 45 chars and its placeholder at 100.
                label = Truncate($"Your bid ({poolName} DKP)", 45);
                placeholder = Truncate($"You have {available:0.##} {poolName} DKP available", 100);
            }
        }

        return Ok(new
        {
            type = ResponseModal,
            data = new
            {
                custom_id = $"{AuctionBidService.BidModalPrefix}{itemId}",
                title = "Place a bid",
                components = new object[]
                {
                    new
                    {
                        type = 1, // action row
                        components = new object[]
                        {
                            new
                            {
                                type = 4, // text input
                                custom_id = AuctionBidService.BidAmountFieldId,
                                label,
                                style = 1, // short
                                min_length = 1,
                                max_length = 7,
                                required = true,
                                placeholder
                            }
                        }
                    }
                }
            }
        });
    }

    // The auction card's "My bids": an ephemeral list of the clicker's bids on that auction and
    // whether each still leads. Needs a linked account -- bids are recorded against it.
    internal async Task<IActionResult> MyAuctionBidsAsync(int auctionId, string? appUserId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(appUserId))
        {
            return Ephemeral("Open LSM and sign in with Discord once to link your account, then try again.");
        }

        // Only this member's own bids are loaded; the card's numbering needs every item.
        var items = await _db.AuctionItems
            .AsNoTracking()
            .Include(item => item.Bids.Where(bid => bid.AppUserId == appUserId))
            .Where(item => item.AuctionId == auctionId)
            .ToListAsync(cancellationToken);
        if (items.Count == 0)
        {
            return Ephemeral("That auction has closed — see its results in the app.");
        }

        return Ephemeral(AuctionBoardMessageBuilder.BuildMyBidsText(items, appUserId));
    }

    private static string Truncate(string value, int maxLength)
        => value.Length <= maxLength ? value : value[..maxLength];

    // The bid modal's submit, for the submitter's linked LSM account.
    internal async Task<IActionResult> HandleBidModalAsync(
        string customId, JsonElement data, DiscordLinkedAccount account, CancellationToken cancellationToken)
    {
        var itemId = ParseTrailingId(customId, AuctionBidService.BidModalPrefix);
        var amountText = ExtractModalValue(data, AuctionBidService.BidAmountFieldId)?.Trim();
        if (!int.TryParse(amountText, out var amount))
        {
            return Ephemeral("Enter a whole number for your bid.");
        }

        var fallbackName = account.CharacterName ?? account.UserName ?? "User";
        var result = await AuctionBidService.PlaceBidAsync(
            _db, _dkpPools, _dkpPoolBalances, account.IdentityUserId!, fallbackName, itemId, amount, cancellationToken);

        return Ephemeral(result.Success
            ? $"✅ Bid placed: {result.Amount} DKP on {result.ItemName ?? "the item"}."
            : result.Error ?? "Placing your bid failed.");
    }
}
