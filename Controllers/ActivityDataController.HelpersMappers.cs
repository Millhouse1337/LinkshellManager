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
    private static ActivityLinkshellSettingsDto MapLinkshellSettingsDto(Linkshell? linkshell)
    {
        if (linkshell is null)
        {
            return new ActivityLinkshellSettingsDto("Dkp", true, true, true, true, true, true, true, true, true, "Quarter");
        }

        return new ActivityLinkshellSettingsDto(
            string.IsNullOrWhiteSpace(linkshell.LootStructure) ? "Dkp" : linkshell.LootStructure,
            linkshell.EnableHnmSection,
            linkshell.EnableMissions,
            linkshell.EnableAuctions,
            linkshell.EnableToDs,
            linkshell.EnableEndgame,
            linkshell.EnableEvents,
            linkshell.EnableDkp,
            linkshell.EnableItems,
            linkshell.EnableRevenue,
            NormalizeDkpRounding(linkshell.DkpRoundingIncrement));
    }

    private static string NormalizeDkpRounding(string? raw)
    {
        if (string.Equals(raw, "Half", StringComparison.OrdinalIgnoreCase)) return "Half";
        return "Quarter";
    }

    private static ActivityPermissionsDto? ResolvePermissionsFor(
        int linkshellId,
        string? rank,
        Dictionary<int, Dictionary<string, LinkshellRole>> rolesByLinkshellAndName)
    {
        if (!rolesByLinkshellAndName.TryGetValue(linkshellId, out var rolesByName))
        {
            return null;
        }

        var rankName = string.IsNullOrWhiteSpace(rank) ? "Member" : rank.Trim();
        if (!rolesByName.TryGetValue(rankName, out var role))
        {
            if (!rolesByName.TryGetValue("Member", out role))
            {
                return null;
            }
        }

        return new ActivityPermissionsDto(
            role.CanManageRoles,
            role.CanManageMembers,
            role.CanManageEvents,
            role.CanModerateLiveEvent,
            role.CanAddLoot,
            role.CanManageInventory,
            role.CanManageTreasury,
            role.CanManageRules,
            role.CanManageAnnouncements,
            role.CanManageTods,
            role.CanAuditDkp,
            role.CanManageAuctions,
            role.CanCustomizeLinkshell);
    }

    private static ActivityLinkshellRoleDto MapLinkshellRoleDto(LinkshellRole role)
    {
        return new ActivityLinkshellRoleDto(
            role.Id,
            role.Name,
            role.IsSystem,
            role.SortOrder,
            role.CanManageRoles,
            role.CanManageMembers,
            role.CanManageEvents,
            role.CanModerateLiveEvent,
            role.CanAddLoot,
            role.CanManageInventory,
            role.CanManageTreasury,
            role.CanManageRules,
            role.CanManageAnnouncements,
            role.CanManageTods,
            role.CanAuditDkp,
            role.CanManageAuctions,
            role.CanCustomizeLinkshell);
    }

    private static ActivityTodDto MapTodDto(Tod tod)
    {
        return new ActivityTodDto(
            tod.Id,
            tod.LinkshellId,
            tod.MonsterName ?? "Unknown monster",
            tod.DayNumber,
            tod.Time,
            tod.Claim,
            tod.Cooldown,
            tod.RepopTime,
            tod.Interval,
            tod.TodLootDetails.Count,
            tod.TodLootDetails
                .OrderBy(detail => detail.Id)
                .Select(detail => new ActivityTodLootDto(
                    detail.Id,
                    detail.ItemName,
                    detail.ItemWinner,
                    detail.WinningDkpSpent))
                .ToList(),
            tod.ImagePath);
    }

    private static ActivityAuctionDto MapAuctionDto(Auction auction, string currentUserId, DateTime nowUtc)
    {
        var isCreator = IsAuctionCreator(currentUserId, auction);
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
            CanEditAuction(currentUserId, auction, nowUtc),
            CanStartAuction(currentUserId, auction, nowUtc),
            isCreator && auction.StartedAt.HasValue && (!auction.EndTime.HasValue || nowUtc >= auction.EndTime.Value),
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
                    item.StartTime,
                    item.EndTime,
                    item.Status,
                    item.Notes,
                    item.Bids.Count,
                    item.SourceItemId))
                .ToList());
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
                    item.StartTime,
                    item.EndTime,
                    item.Status,
                    item.Notes,
                    0,
                    item.SourceItemId))
                .ToList());
    }
}
