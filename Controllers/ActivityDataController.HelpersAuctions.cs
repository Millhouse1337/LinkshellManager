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

public sealed partial class ActivityDataController
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
}
