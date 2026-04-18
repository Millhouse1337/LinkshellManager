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
public class AuctionController : Controller
{
    private readonly ApplicationDbContext _context;
    private readonly UserManager<AppUser> _userManager;
    private readonly IDateTimeZoneProvider _dateTimeZoneProvider;

    public AuctionController(
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
        var user = await RequireCurrentUserAsync();
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
        var auctions = selectedLinkshellId == 0
            ? new List<Auction>()
            : await _context.Auctions
                .Include(auction => auction.AuctionItems.OrderBy(item => item.Id))
                    .ThenInclude(item => item.Bids.OrderByDescending(bid => bid.BidAmount))
                .Where(auction => auction.LinkshellId == selectedLinkshellId)
                .OrderBy(auction => auction.StartTime)
                .ToListAsync();

        var viewModels = auctions.Select(auction => new AuctionViewModel
        {
            LinkshellId = auction.LinkshellId,
            Auction = new Auction
            {
                Id = auction.Id,
                LinkshellId = auction.LinkshellId,
                AuctionTitle = auction.AuctionTitle,
                CreatedBy = auction.CreatedBy,
                CreatedByUserId = auction.CreatedByUserId,
                StartTime = ConvertUtcToUserTimeZone(auction.StartTime, user.TimeZone),
                EndTime = ConvertUtcToUserTimeZone(auction.EndTime, user.TimeZone),
                StartedAt = ConvertUtcToUserTimeZone(auction.StartedAt, user.TimeZone)
            },
            AuctionItems = auction.AuctionItems
                .OrderBy(item => item.Id)
                .Select(item => new AuctionItem
                {
                    Id = item.Id,
                    AuctionId = item.AuctionId,
                    ItemName = item.ItemName,
                    ItemType = item.ItemType,
                    StartingBidDkp = item.StartingBidDkp,
                    CurrentHighestBid = item.CurrentHighestBid,
                    CurrentHighestBidder = item.CurrentHighestBidder,
                    CurrentHighestBidderAppUserId = item.CurrentHighestBidderAppUserId,
                    EndingBidDkp = item.EndingBidDkp,
                    StartTime = ConvertUtcToUserTimeZone(item.StartTime, user.TimeZone),
                    EndTime = ConvertUtcToUserTimeZone(item.EndTime, user.TimeZone),
                    Status = item.Status,
                    Notes = item.Notes,
                    Bids = item.Bids
                        .OrderByDescending(bid => bid.BidAmount)
                        .ThenBy(bid => bid.CreatedAt)
                        .Select(bid => new Bid
                        {
                            Id = bid.Id,
                            AuctionItemId = bid.AuctionItemId,
                            AppUserId = bid.AppUserId,
                            CharacterName = bid.CharacterName,
                            BidAmount = bid.BidAmount,
                            CreatedAt = ConvertUtcToUserTimeZone(bid.CreatedAt, user.TimeZone) ?? bid.CreatedAt
                        })
                        .ToList()
                })
                .ToList()
        }).ToList();

        ViewBag.CharacterName = user.CharacterName ?? user.UserName ?? "User";
        ViewBag.CurrentUserId = user.Id;
        ViewBag.CurrentTime = ConvertUtcToUserTimeZone(DateTime.UtcNow, user.TimeZone) ?? DateTime.UtcNow;

        return View(viewModels);
    }

    [HttpGet]
    public async Task<IActionResult> Create()
    {
        var user = await RequireCurrentUserAsync();
        if (user is null)
        {
            return Challenge();
        }

        return View(await BuildAuctionViewModelAsync(user));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(AuctionViewModel model)
    {
        var user = await RequireCurrentUserAsync();
        if (user is null)
        {
            return Challenge();
        }

        model = await BuildAuctionViewModelAsync(user, model);
        var membership = await GetMembershipAsync(user.Id, model.LinkshellId);
        if (membership is null)
        {
            return Forbid();
        }

        NormalizeAuctionItems(model);
        ValidateAuction(model);

        if (!ModelState.IsValid)
        {
            return View(model);
        }

        var auction = new Auction
        {
            AuctionTitle = model.Auction.AuctionTitle?.Trim(),
            LinkshellId = model.LinkshellId,
            CreatedBy = user.CharacterName ?? user.UserName ?? "User",
            CreatedByUserId = user.Id,
            StartTime = ConvertUserTimeZoneToUtc(model.Auction.StartTime, user.TimeZone),
            EndTime = ConvertUserTimeZoneToUtc(model.Auction.EndTime, user.TimeZone),
            StartedAt = null,
            AuctionItems = model.AuctionItems.Select(item => new AuctionItem
            {
                ItemName = item.ItemName?.Trim(),
                ItemType = item.ItemType?.Trim(),
                StartingBidDkp = item.StartingBidDkp,
                CurrentHighestBid = null,
                CurrentHighestBidder = null,
                CurrentHighestBidderAppUserId = null,
                EndingBidDkp = null,
                StartTime = ConvertUserTimeZoneToUtc(model.Auction.StartTime, user.TimeZone),
                EndTime = ConvertUserTimeZoneToUtc(model.Auction.EndTime, user.TimeZone),
                Status = "Pending",
                Notes = item.Notes?.Trim()
            }).ToList()
        };

        _context.Auctions.Add(auction);
        await _context.SaveChangesAsync();

        return RedirectToAction(nameof(Index));
    }

    [HttpGet]
    public async Task<IActionResult> Edit(int id)
    {
        var user = await RequireCurrentUserAsync();
        if (user is null)
        {
            return Challenge();
        }

        var auction = await _context.Auctions
            .Include(item => item.AuctionItems.OrderBy(auctionItem => auctionItem.Id))
            .FirstOrDefaultAsync(item => item.Id == id);
        if (auction is null)
        {
            return NotFound();
        }

        if (!CanEditAuction(user.Id, auction, DateTime.UtcNow))
        {
            return Forbid();
        }

        var model = await BuildAuctionViewModelAsync(user, new AuctionViewModel
        {
            LinkshellId = auction.LinkshellId,
            Auction = new Auction
            {
                Id = auction.Id,
                AuctionTitle = auction.AuctionTitle,
                StartTime = ConvertUtcToUserTimeZone(auction.StartTime, user.TimeZone),
                EndTime = ConvertUtcToUserTimeZone(auction.EndTime, user.TimeZone),
                StartedAt = ConvertUtcToUserTimeZone(auction.StartedAt, user.TimeZone)
            },
            AuctionItems = auction.AuctionItems
                .OrderBy(item => item.Id)
                .Select(item => new AuctionItem
                {
                    Id = item.Id,
                    ItemName = item.ItemName,
                    ItemType = item.ItemType,
                    StartingBidDkp = item.StartingBidDkp,
                    Notes = item.Notes
                })
                .ToList()
        });

        return View(model);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Edit(int id, AuctionViewModel model)
    {
        var user = await RequireCurrentUserAsync();
        if (user is null)
        {
            return Challenge();
        }

        var auction = await _context.Auctions
            .Include(item => item.AuctionItems.OrderBy(auctionItem => auctionItem.Id))
            .FirstOrDefaultAsync(item => item.Id == id);
        if (auction is null)
        {
            return NotFound();
        }

        if (!CanEditAuction(user.Id, auction, DateTime.UtcNow))
        {
            return Forbid();
        }

        model = await BuildAuctionViewModelAsync(user, model);
        NormalizeAuctionItems(model);
        ValidateAuction(model);

        if (!ModelState.IsValid)
        {
            return View(model);
        }

        auction.LinkshellId = model.LinkshellId;
        auction.AuctionTitle = model.Auction.AuctionTitle?.Trim();
        auction.StartTime = ConvertUserTimeZoneToUtc(model.Auction.StartTime, user.TimeZone);
        auction.EndTime = ConvertUserTimeZoneToUtc(model.Auction.EndTime, user.TimeZone);

        var remainingItemsById = auction.AuctionItems.ToDictionary(item => item.Id);
        foreach (var itemModel in model.AuctionItems)
        {
            if (itemModel.Id > 0 && remainingItemsById.TryGetValue(itemModel.Id, out var existingItem))
            {
                existingItem.ItemName = itemModel.ItemName?.Trim();
                existingItem.ItemType = itemModel.ItemType?.Trim();
                existingItem.StartingBidDkp = itemModel.StartingBidDkp;
                existingItem.StartTime = auction.StartTime;
                existingItem.EndTime = auction.EndTime;
                existingItem.Notes = itemModel.Notes?.Trim();
                remainingItemsById.Remove(itemModel.Id);
            }
            else
            {
                auction.AuctionItems.Add(new AuctionItem
                {
                    ItemName = itemModel.ItemName?.Trim(),
                    ItemType = itemModel.ItemType?.Trim(),
                    StartingBidDkp = itemModel.StartingBidDkp,
                    StartTime = auction.StartTime,
                    EndTime = auction.EndTime,
                    Status = "Pending",
                    Notes = itemModel.Notes?.Trim()
                });
            }
        }

        if (remainingItemsById.Count > 0)
        {
            _context.Bids.RemoveRange(remainingItemsById.Values.SelectMany(item => item.Bids));
            _context.AuctionItems.RemoveRange(remainingItemsById.Values);
        }

        await _context.SaveChangesAsync();
        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> StartAuction(int auctionId)
    {
        var user = await RequireCurrentUserAsync();
        if (user is null)
        {
            return Challenge();
        }

        var auction = await _context.Auctions
            .Include(item => item.AuctionItems)
            .FirstOrDefaultAsync(item => item.Id == auctionId);
        if (auction is null)
        {
            return NotFound();
        }

        if (!CanStartAuction(user.Id, auction, DateTime.UtcNow))
        {
            return Forbid();
        }

        var startedAt = DateTime.UtcNow;
        var plannedDuration = ResolveAuctionDuration(auction, startedAt);
        auction.StartedAt = startedAt;
        auction.EndTime = startedAt.Add(plannedDuration);
        foreach (var item in auction.AuctionItems)
        {
            item.StartTime = startedAt;
            item.EndTime = auction.EndTime;
            item.Status = "Live";
        }

        await _context.SaveChangesAsync();
        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> AddItem(AuctionItem newItem, int auctionId)
    {
        var user = await RequireCurrentUserAsync();
        if (user is null)
        {
            return Challenge();
        }

        var auction = await _context.Auctions
            .Include(item => item.AuctionItems)
            .FirstOrDefaultAsync(item => item.Id == auctionId);
        if (auction is null)
        {
            return NotFound();
        }

        if (!CanEditAuction(user.Id, auction, DateTime.UtcNow))
        {
            return Forbid();
        }

        if (string.IsNullOrWhiteSpace(newItem.ItemName) || !newItem.StartingBidDkp.HasValue)
        {
            return RedirectToAction(nameof(Index));
        }

        auction.AuctionItems.Add(new AuctionItem
        {
            ItemName = newItem.ItemName.Trim(),
            ItemType = newItem.ItemType?.Trim(),
            StartingBidDkp = newItem.StartingBidDkp,
            StartTime = auction.StartTime,
            EndTime = auction.EndTime,
            CurrentHighestBid = null,
            CurrentHighestBidder = null,
            CurrentHighestBidderAppUserId = null,
            Status = "Pending",
            Notes = newItem.Notes?.Trim()
        });

        await _context.SaveChangesAsync();
        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> RemoveItem(int id)
    {
        var user = await RequireCurrentUserAsync();
        if (user is null)
        {
            return Challenge();
        }

        var auctionItem = await _context.AuctionItems
            .Include(item => item.Auction)
            .Include(item => item.Bids)
            .FirstOrDefaultAsync(item => item.Id == id);
        if (auctionItem is null || auctionItem.Auction is null)
        {
            return NotFound();
        }

        if (!CanEditAuction(user.Id, auctionItem.Auction, DateTime.UtcNow))
        {
            return Forbid();
        }

        _context.Bids.RemoveRange(auctionItem.Bids);
        _context.AuctionItems.Remove(auctionItem);
        await _context.SaveChangesAsync();
        return RedirectToAction(nameof(Index));
    }

    [HttpGet]
    public async Task<IActionResult> GetBids(int auctionItemId)
    {
        var user = await RequireCurrentUserAsync();
        if (user is null)
        {
            return Challenge();
        }

        var auctionItem = await _context.AuctionItems
            .Include(item => item.Auction)
            .Include(item => item.Bids.OrderByDescending(bid => bid.BidAmount).ThenBy(bid => bid.CreatedAt))
            .FirstOrDefaultAsync(item => item.Id == auctionItemId);
        if (auctionItem is null || auctionItem.Auction is null)
        {
            return NotFound();
        }

        if (!await HasLinkshellAccessAsync(user.Id, auctionItem.Auction.LinkshellId))
        {
            return Forbid();
        }

        return Json(auctionItem.Bids.Select(bid => new
        {
            characterName = bid.CharacterName,
            bidAmount = bid.BidAmount,
            createdAt = ConvertUtcToUserTimeZone(bid.CreatedAt, user.TimeZone)?.ToString("g")
        }));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> MakeBid(int auctionItemId, int bidAmount)
    {
        var user = await RequireCurrentUserAsync();
        if (user is null)
        {
            return Challenge();
        }

        var auctionItem = await _context.AuctionItems
            .Include(item => item.Auction)
            .Include(item => item.Bids)
            .FirstOrDefaultAsync(item => item.Id == auctionItemId);
        if (auctionItem is null || auctionItem.Auction is null)
        {
            return NotFound();
        }

        var membership = await GetMembershipAsync(user.Id, auctionItem.Auction.LinkshellId);
        if (membership is null)
        {
            return Forbid();
        }

        var nowUtc = DateTime.UtcNow;
        if (!IsAuctionLive(auctionItem.Auction, nowUtc))
        {
            TempData["AuctionError"] = "This auction has not started yet.";
            return RedirectToAction(nameof(Index));
        }

        if (HasAuctionEnded(auctionItem.Auction, nowUtc))
        {
            TempData["AuctionError"] = "This auction has already ended.";
            return RedirectToAction(nameof(Index));
        }

        var minimumBid = Math.Max(auctionItem.StartingBidDkp ?? 0, auctionItem.CurrentHighestBid ?? 0);
        if (bidAmount <= minimumBid)
        {
            TempData["AuctionError"] = $"Bid amount must be greater than {minimumBid}.";
            return RedirectToAction(nameof(Index));
        }

        if (bidAmount > (membership.LinkshellDkp ?? 0))
        {
            TempData["AuctionError"] = "You cannot bid more DKP than you currently have.";
            return RedirectToAction(nameof(Index));
        }

        var bid = new Bid
        {
            AuctionItemId = auctionItemId,
            AppUserId = user.Id,
            CharacterName = user.CharacterName ?? user.UserName ?? "User",
            BidAmount = bidAmount,
            CreatedAt = nowUtc
        };

        auctionItem.Bids.Add(bid);
        auctionItem.CurrentHighestBid = bidAmount;
        auctionItem.CurrentHighestBidder = bid.CharacterName;
        auctionItem.CurrentHighestBidderAppUserId = user.Id;
        auctionItem.Status = "BidPlaced";

        await _context.SaveChangesAsync();
        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> EndAuction(int auctionId)
    {
        var user = await RequireCurrentUserAsync();
        if (user is null)
        {
            return Challenge();
        }

        var auction = await _context.Auctions
            .Include(item => item.AuctionItems)
                .ThenInclude(item => item.Bids)
            .FirstOrDefaultAsync(item => item.Id == auctionId);
        if (auction is null)
        {
            return NotFound();
        }

        if (!IsAuctionCreator(user.Id, auction))
        {
            return Forbid();
        }

        if (!HasAuctionStarted(auction, DateTime.UtcNow))
        {
            TempData["AuctionError"] = "An auction must be started before it can be closed.";
            return RedirectToAction(nameof(Index));
        }

        if (!HasAuctionEnded(auction, DateTime.UtcNow))
        {
            TempData["AuctionError"] = "An auction can only be closed after its timer has run out.";
            return RedirectToAction(nameof(Index));
        }

        var auctionHistory = new AuctionHistory
        {
            LinkshellId = auction.LinkshellId,
            AuctionTitle = auction.AuctionTitle,
            CreatedBy = auction.CreatedBy,
            CreatedByUserId = auction.CreatedByUserId,
            StartTime = auction.StartTime,
            EndTime = auction.EndTime,
            StartedAt = auction.StartedAt,
            ClosedAt = DateTime.UtcNow,
            AuctionItems = auction.AuctionItems
                .OrderBy(item => item.Id)
                .Select(item => new AuctionItem
                {
                    ItemName = item.ItemName,
                    ItemType = item.ItemType,
                    StartingBidDkp = item.StartingBidDkp,
                    CurrentHighestBid = item.CurrentHighestBid,
                    CurrentHighestBidder = item.CurrentHighestBidder,
                    CurrentHighestBidderAppUserId = item.CurrentHighestBidderAppUserId,
                    EndingBidDkp = item.CurrentHighestBid,
                    StartTime = item.StartTime,
                    EndTime = item.EndTime,
                    Status = string.IsNullOrWhiteSpace(item.CurrentHighestBidderAppUserId) ? "NoBids" : "Closed",
                    Notes = item.Notes
                })
                .ToList()
        };

        _context.AuctionHistories.Add(auctionHistory);

        foreach (var item in auction.AuctionItems.Where(item => !string.IsNullOrWhiteSpace(item.CurrentHighestBidderAppUserId) && item.CurrentHighestBid.HasValue && item.CurrentHighestBid.Value > 0))
        {
            var winner = await _context.AppUserLinkshells
                .FirstOrDefaultAsync(link => link.AppUserId == item.CurrentHighestBidderAppUserId && link.LinkshellId == auction.LinkshellId);
            if (winner is null)
            {
                continue;
            }

            winner.LinkshellDkp = (winner.LinkshellDkp ?? 0) - item.CurrentHighestBid.GetValueOrDefault();
            _context.DkpLedgerEntries.Add(new DkpLedgerEntry
            {
                AppUserId = winner.AppUserId,
                LinkshellId = auction.LinkshellId,
                EntryType = "AuctionSpent",
                Amount = -item.CurrentHighestBid.GetValueOrDefault(),
                Sequence = 1,
                OccurredAt = DateTime.UtcNow,
                CharacterName = winner.CharacterName,
                ItemName = item.ItemName,
                Details = $"Auction spend from {auction.AuctionTitle ?? "auction"}."
            });
        }

        _context.Bids.RemoveRange(auction.AuctionItems.SelectMany(item => item.Bids));
        _context.AuctionItems.RemoveRange(auction.AuctionItems);
        _context.Auctions.Remove(auction);
        await _context.SaveChangesAsync();

        return RedirectToAction(nameof(Index), "AuctionHistory");
    }

    [HttpGet]
    public async Task<IActionResult> GetAuctionDetails(int auctionId)
    {
        var user = await RequireCurrentUserAsync();
        if (user is null)
        {
            return Challenge();
        }

        var auction = await _context.Auctions
            .Include(item => item.AuctionItems)
            .FirstOrDefaultAsync(item => item.Id == auctionId);
        if (auction is null)
        {
            return NotFound();
        }

        if (!await HasLinkshellAccessAsync(user.Id, auction.LinkshellId))
        {
            return Forbid();
        }

        return Json(auction.AuctionItems
            .OrderBy(item => item.Id)
            .Select(item => new
            {
                itemName = item.ItemName,
                highestBid = item.CurrentHighestBid ?? item.StartingBidDkp ?? 0,
                highestBidder = string.IsNullOrWhiteSpace(item.CurrentHighestBidder) ? "No bids" : item.CurrentHighestBidder
            }));
    }

    private async Task<AuctionViewModel> BuildAuctionViewModelAsync(AppUser user, AuctionViewModel? source = null)
    {
        var linkshells = await _context.AppUserLinkshells
            .Where(link => link.AppUserId == user.Id)
            .Select(link => link.Linkshell!)
            .OrderBy(link => link.LinkshellName)
            .ToListAsync();

        var selectedLinkshellId = source?.LinkshellId
            ?? user.PrimaryLinkshellId
            ?? linkshells.FirstOrDefault()?.Id
            ?? 0;
        if (selectedLinkshellId > 0 && linkshells.All(link => link.Id != selectedLinkshellId))
        {
            selectedLinkshellId = linkshells.FirstOrDefault()?.Id ?? 0;
        }

        return new AuctionViewModel
        {
            LinkshellId = selectedLinkshellId,
            Linkshells = linkshells,
            Auction = source?.Auction ?? new Auction(),
            AuctionItems = source?.AuctionItems?.Count > 0 ? source.AuctionItems : new List<AuctionItem> { new() }
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
        }
    }

    private DateTime? ConvertUtcToUserTimeZone(DateTime? utcDateTime, string? timeZoneId)
    {
        if (!utcDateTime.HasValue)
        {
            return null;
        }

        var zone = ResolveTimeZone(timeZoneId);
        var instant = Instant.FromDateTimeUtc(DateTime.SpecifyKind(utcDateTime.Value, DateTimeKind.Utc));
        return instant.InZone(zone).ToDateTimeUnspecified();
    }

    private DateTime? ConvertUserTimeZoneToUtc(DateTime? localDateTime, string? timeZoneId)
    {
        if (!localDateTime.HasValue)
        {
            return null;
        }

        var zone = ResolveTimeZone(timeZoneId);
        return zone.AtLeniently(LocalDateTime.FromDateTime(localDateTime.Value)).ToDateTimeUtc();
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
