using System.Globalization;
using System.Net.Http.Headers;
using LinkshellManagerDiscordApp.Data;
using LinkshellManagerDiscordApp.Models;
using LinkshellManagerDiscordApp.Services;
using LinkshellManagerDiscordApp.ViewModels;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NodaTime;

namespace LinkshellManagerDiscordApp.Controllers;

public sealed partial class ActivityLootController
{
    private static List<ActivityAuctionItemInput> NormalizeAuctionItems(IReadOnlyList<ActivityAuctionItemInput>? items)
    {
        return (items ?? Array.Empty<ActivityAuctionItemInput>())
            .Where(item => !string.IsNullOrWhiteSpace(item.ItemName) || item.StartingBidDkp.HasValue || (item.GilAmount.HasValue && item.GilAmount.Value > 0))
            .ToList();
    }

    // Gil items display as "<amount> gil"; everything else uses the typed name.
    private static string? ResolveAuctionItemName(ActivityAuctionItemInput item)
    {
        if (item.GilAmount.HasValue && item.GilAmount.Value > 0)
        {
            return $"{item.GilAmount.Value:N0} gil";
        }

        return item.ItemName?.Trim();
    }

    private static string? ValidateAuctionRequest(
        string? title,
        DateTime? startTimeUtc,
        DateTime? endTimeUtc,
        IReadOnlyList<ActivityAuctionItemInput> items)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return "Auction title is required.";
        }

        if (!startTimeUtc.HasValue)
        {
            return "Auction start time is required.";
        }

        if (!endTimeUtc.HasValue)
        {
            return "Auction end time is required.";
        }

        if (endTimeUtc <= startTimeUtc)
        {
            return "Auction end time must be after its start time.";
        }

        if (items.Count == 0)
        {
            return "Add at least one auction item.";
        }

        foreach (var item in items)
        {
            var isGil = item.GilAmount.HasValue && item.GilAmount.Value > 0;

            // Gil items are auto-named "<amount> gil"; no typed name needed.
            if (!isGil && string.IsNullOrWhiteSpace(item.ItemName))
            {
                return "Each auction item needs a name.";
            }

            if (item.GilAmount.HasValue && item.GilAmount.Value <= 0)
            {
                return "Gil amount must be greater than 0.";
            }

            if (!item.StartingBidDkp.HasValue || item.StartingBidDkp < 0)
            {
                return "Each auction item needs a starting bid of 0 or higher.";
            }
        }

        return null;
    }

    // Gil items sell treasury gil for DKP, and Close pays that gil out. Block an auction that sells
    // more gil than the treasury holds. Create and Edit both run this: an Edit could otherwise
    // raise a gil item past the balance after Create had checked a smaller amount.
    private async Task<string?> ValidateAuctionGilAsync(
        int linkshellId,
        IReadOnlyList<ActivityAuctionItemInput> items,
        CancellationToken cancellationToken)
    {
        var requestedGil = SumAuctionGil(items);
        if (requestedGil <= 0)
        {
            return null;
        }

        var availableGil = await _treasury.GetCashOnHandAsync(linkshellId, cancellationToken);
        return DescribeAuctionGilShortfall(requestedGil, availableGil);
    }

    internal static long SumAuctionGil(IReadOnlyList<ActivityAuctionItemInput> items)
    {
        return items
            .Where(item => item.GilAmount is > 0)
            .Sum(item => item.GilAmount!.Value);
    }

    // Null when the treasury covers the gil being auctioned, otherwise the error to show.
    internal static string? DescribeAuctionGilShortfall(long requestedGil, long availableGil)
    {
        if (requestedGil <= 0)
        {
            return null;
        }
        if (availableGil <= 0)
        {
            return "This linkshell has no gil in its treasury to auction.";
        }
        if (requestedGil > availableGil)
        {
            return $"Not enough gil in the treasury: {availableGil:N0} available, but {requestedGil:N0} is being auctioned.";
        }
        return null;
    }

    // A treasury item an auction sells must belong to the auction's OWN linkshell. Close draws the
    // linked item's stock down, so an id from another linkshell would point a sale at someone
    // else's inventory (and an unknown id is just a broken link). Gil items never carry one.
    internal static async Task<string?> ValidateAuctionSourceItemsAsync(
        ApplicationDbContext db,
        int linkshellId,
        IReadOnlyList<ActivityAuctionItemInput> items,
        CancellationToken cancellationToken)
    {
        var requestedIds = items
            .Where(item => !item.GilAmount.HasValue && item.SourceItemId.HasValue)
            .Select(item => item.SourceItemId!.Value)
            .Distinct()
            .ToList();
        if (requestedIds.Count == 0)
        {
            return null;
        }

        var ownCount = await db.Items
            .CountAsync(item => requestedIds.Contains(item.Id) && item.LinkshellId == linkshellId, cancellationToken);
        return ownCount == requestedIds.Count
            ? null
            : "One of the treasury items isn't in this linkshell's inventory. Refresh and pick it again.";
    }

    // Who may run an EXISTING auction (edit, start, end, close, delete): anyone whose role has Manage
    // auctions in the auction's OWN linkshell (the Leader and the admin override always pass).
    // Being the creator is deliberately NOT enough: Edit can add treasury gil or inventory items and
    // Close pays them out, so a creator who was demoted - or a plain member who made an auction
    // before create needed the permission - must not keep that power. Nobody loses anything
    // legitimate: creating already needs Manage auctions.
    internal static bool CanControlAuction(bool isMemberOfAuctionLinkshell, bool canManageAuctions)
    {
        return isMemberOfAuctionLinkshell && canManageAuctions;
    }

    // The caller's standing in the given linkshell. Callers pass the auction's own LinkshellId,
    // never one from the request body. CanAsync already lets the Leader and the admin override through.
    private async Task<(bool IsMember, bool CanManageAuctions)> GetAuctionRightsAsync(
        string appUserId, int linkshellId, CancellationToken cancellationToken)
    {
        var membership = await GetMembershipAsync(appUserId, linkshellId, cancellationToken);
        var canManage = await CanAsync(membership, role => role.CanManageAuctions, cancellationToken);
        return (membership is not null, canManage);
    }

    // canControl is CanControlAuction(...) for the caller.
    internal static bool CanEditAuction(bool canControl, Auction auction, DateTime referenceUtc)
    {
        return canControl
               && !HasAuctionEnded(auction, referenceUtc);
    }

    internal static bool CanStartAuction(bool canControl, Auction auction, DateTime referenceUtc)
    {
        return canControl
               && !auction.StartedAt.HasValue
               && (!auction.StartTime.HasValue || referenceUtc < auction.StartTime.Value)
               && !HasAuctionEnded(auction, referenceUtc);
    }

    private static bool HasAuctionStarted(Auction auction, DateTime referenceUtc)
    {
        return auction.StartedAt.HasValue
               || (auction.StartTime.HasValue && referenceUtc >= auction.StartTime.Value);
    }

    private static bool HasAuctionEnded(Auction auction, DateTime referenceUtc)
    {
        return auction.EndTime.HasValue && referenceUtc >= auction.EndTime.Value;
    }

    private static bool IsAuctionLive(Auction auction, DateTime referenceUtc)
    {
        return HasAuctionStarted(auction, referenceUtc) && !HasAuctionEnded(auction, referenceUtc);
    }

    private static TimeSpan ResolveAuctionDuration(Auction auction, DateTime referenceUtc)
    {
        if (auction.StartTime.HasValue && auction.EndTime.HasValue && auction.EndTime > auction.StartTime)
        {
            return auction.EndTime.Value - auction.StartTime.Value;
        }

        if (auction.EndTime.HasValue && auction.EndTime > referenceUtc)
        {
            return auction.EndTime.Value - referenceUtc;
        }

        return TimeSpan.Zero;
    }

    private static ActivityAuctionDto MapAuctionDto(
        Auction auction, bool canControl, DateTime nowUtc, double? availableDkp = null, bool auctionsLocked = false,
        int? dkpPoolId = null, string? dkpPoolName = null)
    {
        // canControl = CanControlAuction(...): Manage auctions in this linkshell.
        var status = HasAuctionEnded(auction, nowUtc)
            ? "Ended"
            : HasAuctionStarted(auction, nowUtc)
                ? "Live"
                : "Pending";

        return new ActivityAuctionDto(
            auction.Id,
            auction.LinkshellId,
            auction.AuctionTitle,
            auction.CreatedBy,
            auction.StartTime,
            auction.EndTime,
            auction.StartedAt,
            status,
            CanEditAuction(canControl, auction, nowUtc),
            CanStartAuction(canControl, auction, nowUtc),
            // CanEnd: the controller can stop bidding while the auction is live (no
            // archive yet — the run lingers in the active board with status
            // Ended until they Close it).
            canControl && IsAuctionLive(auction, nowUtc),
            // CanClose: only after the timer expires. Closing runs the delivery
            // confirmation + archive + inventory-removal flow.
            canControl && HasAuctionEnded(auction, nowUtc),
            canControl,
            auction.AuctionItems
                .OrderBy(item => item.Id)
                .Select(item => new ActivityAuctionItemDto(
                    item.Id,
                    item.ItemName,
                    item.ItemType,
                    item.StartingBidDkp,
                    item.CurrentHighestBid,
                    item.CurrentHighestBidder,
                    item.CurrentHighestBidderAppUserId,
                    item.Status,
                    item.Notes,
                    item.Bids.Count,
                    item.SourceItemId,
                    item.GilAmount))
                .ToList(),
            availableDkp,
            auctionsLocked,
            dkpPoolId ?? auction.DkpPoolId,
            dkpPoolName);
    }

    private static ActivityAuctionHistoryDto MapAuctionHistoryDto(AuctionHistory history)
    {
        return new ActivityAuctionHistoryDto(
            history.Id,
            history.LinkshellId,
            history.AuctionTitle,
            history.CreatedBy,
            history.StartTime,
            history.EndTime,
            history.StartedAt,
            history.ClosedAt,
            history.AuctionItems
                .OrderBy(item => item.Id)
                .Select(item => new ActivityAuctionItemDto(
                    item.Id,
                    item.ItemName,
                    item.ItemType,
                    item.StartingBidDkp,
                    item.CurrentHighestBid,
                    item.CurrentHighestBidder,
                    item.CurrentHighestBidderAppUserId,
                    item.Status,
                    item.Notes,
                    0,
                    item.SourceItemId,
                    item.GilAmount))
                .ToList());
    }
}
