using LinkshellManagerDiscordApp.Data;
using LinkshellManagerDiscordApp.Models;
using LinkshellManagerDiscordApp.Services;
using LinkshellManagerDiscordApp.ViewModels;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NodaTime;

namespace LinkshellManagerDiscordApp.Controllers;

public partial class AuctionController
{
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
        ViewBag.SelectedLinkshellId = selectedLinkshellId;
        ViewBag.AuctionsLocked = false;
        ViewBag.CanLockAuctions = false;

        if (selectedLinkshellId != 0)
        {
            ViewBag.TotalDkp = await _context.AppUserLinkshells
                .Where(m => m.AppUserId == user.Id && m.LinkshellId == selectedLinkshellId)
                .Select(m => m.LinkshellDkp ?? 0d)
                .FirstOrDefaultAsync();
            ViewBag.AvailableDkp = await AuctionDkpService.ComputeAvailableDkpAsync(
                _context, user.Id, selectedLinkshellId, HttpContext.RequestAborted);

            // Each auction can draw from a different pool, so the viewer's available DKP is
            // per-pool. Keyed by pool id; the view falls back to AvailableDkp above when the
            // linkshell has a single pool (in which case the two are identical anyway).
            var poolMap = await _dkpPools.GetMapAsync(selectedLinkshellId, HttpContext.RequestAborted);
            var availableByPool = new Dictionary<int, double>();
            var poolNames = new Dictionary<int, string>();
            foreach (var pool in poolMap.Pools)
            {
                poolNames[pool.Id] = pool.Name;
                availableByPool[pool.Id] = await AuctionDkpService.ComputePoolAvailableDkpAsync(
                    _context, _dkpPoolBalances, user.Id, selectedLinkshellId, pool.Id, HttpContext.RequestAborted);
            }
            ViewBag.AvailableDkpByPool = availableByPool;
            ViewBag.DkpPoolNames = poolNames;
            ViewBag.DefaultDkpPoolId = poolMap.DefaultPoolId;
            ViewBag.HasMultipleDkpPools = poolMap.HasMultiplePools;
            ViewBag.AuctionsLocked = await _context.Linkshells
                .Where(l => l.Id == selectedLinkshellId)
                .Select(l => l.AuctionsLocked)
                .FirstOrDefaultAsync();
            var role = await GetEffectiveRoleAsync(user.Id, selectedLinkshellId);
            ViewBag.CanLockAuctions = role?.CanLockAuctions == true;
        }

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

        // The pool is locked once anyone has bid: those bidders were validated against THIS pool's
        // balance, so switching it under them would debit a wallet they never agreed to spend from.
        var poolLocked = await _context.Bids
            .AnyAsync(bid => bid.AuctionItem!.AuctionId == auction.Id, HttpContext.RequestAborted);

        var model = await BuildAuctionViewModelAsync(user, new AuctionViewModel
        {
            LinkshellId = auction.LinkshellId,
            DkpPoolId = auction.DkpPoolId,
            DkpPoolLocked = poolLocked,
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
                    Notes = item.Notes,
                    SourceItemId = item.SourceItemId
                })
                .ToList()
        });

        return View(model);
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
}
