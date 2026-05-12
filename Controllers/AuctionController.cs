using LinkshellManagerDiscordApp.Data;
using LinkshellManagerDiscordApp.Models;
using LinkshellManagerDiscordApp.Services;
using LinkshellManagerDiscordApp.ViewModels;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace LinkshellManagerDiscordApp.Controllers;

[Authorize]
public partial class AuctionController : Controller
{
    private readonly ApplicationDbContext _context;
    private readonly UserManager<AppUser> _userManager;
    private readonly TimeZoneConversionService _timeZones;

    public AuctionController(
        ApplicationDbContext context,
        UserManager<AppUser> userManager,
        TimeZoneConversionService timeZones)
    {
        _context = context;
        _userManager = userManager;
        _timeZones = timeZones;
    }

    private async Task<AuctionViewModel> BuildAuctionViewModelAsync(AppUser user, AuctionViewModel? source = null)
    {
        var linkshells = await _context.AppUserLinkshells
            .Where(link => link.AppUserId == user.Id)
            .Select(link => link.Linkshell!)
            .OrderBy(link => link.LinkshellName)
            .ToListAsync();

        var sourceLinkshellId = source?.LinkshellId > 0 ? source.LinkshellId : (int?)null;
        var selectedLinkshellId = sourceLinkshellId
            ?? user.PrimaryLinkshellId
            ?? linkshells.FirstOrDefault()?.Id
            ?? 0;
        if (selectedLinkshellId > 0 && linkshells.All(link => link.Id != selectedLinkshellId))
        {
            selectedLinkshellId = linkshells.FirstOrDefault()?.Id ?? 0;
        }

        var sourceItems = selectedLinkshellId > 0
            ? await _context.Items
                .AsNoTracking()
                .Where(item => item.LinkshellId == selectedLinkshellId)
                .OrderBy(item => item.ItemName)
                .Select(item => new AuctionSourceItemOption
                {
                    Id = item.Id,
                    ItemName = item.ItemName,
                    ItemType = item.ItemType,
                    Quantity = item.Quantity
                })
                .ToListAsync()
            : new List<AuctionSourceItemOption>();

        return new AuctionViewModel
        {
            LinkshellId = selectedLinkshellId,
            Linkshells = linkshells,
            Auction = source?.Auction ?? new Auction(),
            AuctionItems = source?.AuctionItems?.Count > 0 ? source.AuctionItems : new List<AuctionItem> { new() },
            SourceItems = sourceItems
        };
    }

    private async Task<AppUser?> RequireCurrentUserAsync() => await _userManager.GetUserAsync(User);

    private async Task<AppUserLinkshell?> GetMembershipAsync(string appUserId, int linkshellId)
    {
        return await _context.AppUserLinkshells
            .FirstOrDefaultAsync(link => link.AppUserId == appUserId && link.LinkshellId == linkshellId);
    }

    private async Task<bool> HasLinkshellAccessAsync(string appUserId, int linkshellId)
    {
        return await _context.AppUserLinkshells
            .AnyAsync(link => link.AppUserId == appUserId && link.LinkshellId == linkshellId);
    }

    private async Task<int> ResolveActiveLinkshellIdAsync(AppUser user)
    {
        var linkshellIds = await _context.AppUserLinkshells
            .Where(link => link.AppUserId == user.Id)
            .OrderBy(link => link.Linkshell!.LinkshellName)
            .Select(link => link.LinkshellId)
            .ToListAsync();

        if (user.PrimaryLinkshellId.HasValue && linkshellIds.Contains(user.PrimaryLinkshellId.Value))
        {
            return user.PrimaryLinkshellId.Value;
        }

        return linkshellIds.FirstOrDefault();
    }

    private static bool CanEditAuction(string currentUserId, Auction auction, DateTime referenceUtc)
    {
        return IsAuctionCreator(currentUserId, auction) &&
               !HasAuctionStarted(auction, referenceUtc) &&
               !HasAuctionEnded(auction, referenceUtc);
    }

    private static bool CanStartAuction(string currentUserId, Auction auction, DateTime referenceUtc)
    {
        return IsAuctionCreator(currentUserId, auction) &&
               !auction.StartedAt.HasValue &&
               (!auction.StartTime.HasValue || referenceUtc < auction.StartTime.Value) &&
               !HasAuctionEnded(auction, referenceUtc);
    }

    private static bool HasAuctionStarted(Auction auction, DateTime referenceUtc)
    {
        return auction.StartedAt.HasValue ||
               (auction.StartTime.HasValue && referenceUtc >= auction.StartTime.Value);
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

    private static bool IsAuctionCreator(string currentUserId, Auction auction)
    {
        return string.Equals(auction.CreatedByUserId, currentUserId, StringComparison.OrdinalIgnoreCase);
    }

    private void NormalizeAuctionItems(AuctionViewModel model)
    {
        model.AuctionItems = model.AuctionItems
            .Where(item => !string.IsNullOrWhiteSpace(item.ItemName) || item.StartingBidDkp.HasValue)
            .ToList();

        if (model.AuctionItems.Count == 0)
        {
            model.AuctionItems.Add(new AuctionItem());
        }
    }

    private void ValidateAuction(AuctionViewModel model)
    {
        if (string.IsNullOrWhiteSpace(model.Auction.AuctionTitle))
        {
            ModelState.AddModelError("Auction.AuctionTitle", "Auction title is required.");
        }

        if (!model.Auction.StartTime.HasValue)
        {
            ModelState.AddModelError("Auction.StartTime", "Start time is required.");
        }

        if (!model.Auction.EndTime.HasValue)
        {
            ModelState.AddModelError("Auction.EndTime", "End time is required.");
        }

        if (model.Auction.StartTime.HasValue && model.Auction.EndTime.HasValue && model.Auction.EndTime <= model.Auction.StartTime)
        {
            ModelState.AddModelError("Auction.EndTime", "End time must be after the start time.");
        }

        // Build a fast set of inventory ids posted on the rebuilt model so
        // we can reject SourceItemIds that don't belong to this linkshell —
        // the dropdown only offers in-linkshell items, but the form is just
        // HTML and a bad actor could spoof a different id in the POST.
        var allowedSourceIds = model.SourceItems.Select(option => option.Id).ToHashSet();

        for (var index = 0; index < model.AuctionItems.Count; index++)
        {
            var item = model.AuctionItems[index];
            if (string.IsNullOrWhiteSpace(item.ItemName))
            {
                ModelState.AddModelError($"AuctionItems[{index}].ItemName", "Item name is required.");
            }

            if (!item.StartingBidDkp.HasValue || item.StartingBidDkp < 0)
            {
                ModelState.AddModelError($"AuctionItems[{index}].StartingBidDkp", "Starting bid must be 0 or higher.");
            }

            if (item.SourceItemId.HasValue && !allowedSourceIds.Contains(item.SourceItemId.Value))
            {
                ModelState.AddModelError($"AuctionItems[{index}].SourceItemId", "Selected inventory item is not available for this linkshell.");
            }
        }
    }

    private DateTime? ConvertUtcToUserTimeZone(DateTime? utcDateTime, string? timeZoneId)
        => _timeZones.ToUserTime(utcDateTime, timeZoneId);

    private DateTime? ConvertUserTimeZoneToUtc(DateTime? localDateTime, string? timeZoneId)
        => _timeZones.ToUtc(localDateTime, timeZoneId);
}
