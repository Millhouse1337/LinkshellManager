using LinkshellManagerDiscordApp.Data;
using LinkshellManagerDiscordApp.Models;
using LinkshellManagerDiscordApp.ViewModels;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NodaTime;

namespace LinkshellManagerDiscordApp.Controllers;

[Authorize]
public class AuctionHistoryController : Controller
{
    private readonly ApplicationDbContext _context;
    private readonly UserManager<AppUser> _userManager;
    private readonly IDateTimeZoneProvider _dateTimeZoneProvider;

    public AuctionHistoryController(
        ApplicationDbContext context,
        UserManager<AppUser> userManager,
        IDateTimeZoneProvider dateTimeZoneProvider)
    {
        _context = context;
        _userManager = userManager;
        _dateTimeZoneProvider = dateTimeZoneProvider;
    }

    public async Task<IActionResult> Index()
    {
        var user = await _userManager.GetUserAsync(User);
        if (user is null)
        {
            return Challenge();
        }

        var linkshellIds = await _context.AppUserLinkshells
            .Where(link => link.AppUserId == user.Id)
            .Select(link => link.LinkshellId)
            .Distinct()
            .ToListAsync();

        var selectedLinkshellId = user.PrimaryLinkshellId ?? linkshellIds.FirstOrDefault();
        var auctionHistories = selectedLinkshellId == 0
            ? new List<AuctionHistory>()
            : await _context.AuctionHistories
                .Include(history => history.AuctionItems.OrderBy(item => item.Id))
                .Where(history => history.LinkshellId == selectedLinkshellId)
                .OrderByDescending(history => history.ClosedAt)
                .ToListAsync();

        var viewModel = auctionHistories.Select(history => new AuctionHistoryViewModel
        {
            AuctionHistory = new AuctionHistory
            {
                Id = history.Id,
                AuctionTitle = history.AuctionTitle,
                CreatedBy = history.CreatedBy,
                CreatedByUserId = history.CreatedByUserId,
                StartTime = ConvertUtcToUserTimeZone(history.StartTime, user.TimeZone),
                EndTime = ConvertUtcToUserTimeZone(history.EndTime, user.TimeZone),
                StartedAt = ConvertUtcToUserTimeZone(history.StartedAt, user.TimeZone),
                ClosedAt = ConvertUtcToUserTimeZone(history.ClosedAt, user.TimeZone) ?? history.ClosedAt
            },
            AuctionItems = history.AuctionItems.Select(item => new AuctionItem
            {
                Id = item.Id,
                ItemName = item.ItemName,
                ItemType = item.ItemType,
                StartingBidDkp = item.StartingBidDkp,
                EndingBidDkp = item.EndingBidDkp,
                CurrentHighestBid = item.CurrentHighestBid,
                CurrentHighestBidder = item.CurrentHighestBidder,
                StartTime = ConvertUtcToUserTimeZone(item.StartTime, user.TimeZone),
                EndTime = ConvertUtcToUserTimeZone(item.EndTime, user.TimeZone),
                Status = item.Status,
                Notes = item.Notes
            }).ToList(),
            StartTime = ConvertUtcToUserTimeZone(history.StartTime, user.TimeZone),
            EndTime = ConvertUtcToUserTimeZone(history.EndTime, user.TimeZone)
        }).ToList();

        return View(viewModel);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> UpdateItemStatus(int itemId)
    {
        var user = await _userManager.GetUserAsync(User);
        if (user is null)
        {
            return Challenge();
        }

        var auctionItem = await _context.AuctionItems
            .Include(item => item.AuctionHistory)
            .FirstOrDefaultAsync(item => item.Id == itemId);
        if (auctionItem is null || auctionItem.AuctionHistory is null)
        {
            return NotFound();
        }

        if (!await HasLinkshellAccessAsync(user.Id, auctionItem.AuctionHistory.LinkshellId))
        {
            return Forbid();
        }

        auctionItem.Status = "Received";
        await _context.SaveChangesAsync();
        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> UndoItemStatus(int itemId)
    {
        var user = await _userManager.GetUserAsync(User);
        if (user is null)
        {
            return Challenge();
        }

        var auctionItem = await _context.AuctionItems
            .Include(item => item.AuctionHistory)
            .FirstOrDefaultAsync(item => item.Id == itemId);
        if (auctionItem is null || auctionItem.AuctionHistory is null)
        {
            return NotFound();
        }

        if (!await HasLinkshellAccessAsync(user.Id, auctionItem.AuctionHistory.LinkshellId))
        {
            return Forbid();
        }

        auctionItem.Status = "Pending";
        await _context.SaveChangesAsync();
        return RedirectToAction(nameof(Index));
    }

    private async Task<bool> HasLinkshellAccessAsync(string appUserId, int linkshellId)
    {
        return await _context.AppUserLinkshells
            .AnyAsync(link => link.AppUserId == appUserId && link.LinkshellId == linkshellId);
    }

    private DateTime? ConvertUtcToUserTimeZone(DateTime? utcDateTime, string? timeZoneId)
    {
        if (!utcDateTime.HasValue)
        {
            return null;
        }

        var instant = Instant.FromDateTimeUtc(DateTime.SpecifyKind(utcDateTime.Value, DateTimeKind.Utc));
        var zone = ResolveTimeZone(timeZoneId);
        return instant.InZone(zone).ToDateTimeUnspecified();
    }

    private DateTimeZone ResolveTimeZone(string? timeZoneId)
    {
        if (!string.IsNullOrWhiteSpace(timeZoneId) && _dateTimeZoneProvider.Ids.Contains(timeZoneId))
        {
            return _dateTimeZoneProvider[timeZoneId];
        }

        return DateTimeZone.Utc;
    }
}
