using LinkshellManagerDiscordApp.Data;
using LinkshellManagerDiscordApp.Models;
using LinkshellManagerDiscordApp.ViewModels;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NodaTime;

namespace LinkshellManagerDiscordApp.Controllers;

public partial class AuctionController
{
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(AuctionViewModel model)
    {
        var user = await RequireCurrentUserAsync();
        if (user is null)
        {
            return Challenge();
        }

        model.LinkshellId = await ResolveActiveLinkshellIdAsync(user);
        ModelState.Remove(nameof(AuctionViewModel.LinkshellId));

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
                Notes = item.Notes?.Trim(),
                SourceItemId = item.SourceItemId
            }).ToList()
        };

        _context.Auctions.Add(auction);
        await _context.SaveChangesAsync();

        return RedirectToAction(nameof(Index));
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
                existingItem.SourceItemId = itemModel.SourceItemId;
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
                    Notes = itemModel.Notes?.Trim(),
                    SourceItemId = itemModel.SourceItemId
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

    public sealed class AuctionAddItemInput
    {
        public string? ItemName { get; set; }
        public string? ItemType { get; set; }
        public int? StartingBidDkp { get; set; }
        public string? Notes { get; set; }
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> AddItem(AuctionAddItemInput newItem, int auctionId)
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

        if (bidAmount <= 0)
        {
            TempData["AuctionError"] = "Bid amount must be a positive number.";
            return RedirectToAction(nameof(Index));
        }

        const int MaxBidAmount = 1_000_000;
        if (bidAmount > MaxBidAmount)
        {
            TempData["AuctionError"] = $"Bid amount cannot exceed {MaxBidAmount:N0}.";
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

    // Stops bidding now without archiving the run. Mirrors the activity's
    // /api/activity/auctions/{id}/end endpoint — the auction transitions to
    // status Ended and lingers on the active board until the creator closes
    // it (which is where delivery confirmation + inventory drawdown happen).
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
            .FirstOrDefaultAsync(item => item.Id == auctionId);
        if (auction is null)
        {
            return NotFound();
        }

        if (!IsAuctionCreator(user.Id, auction))
        {
            return Forbid();
        }

        var nowUtc = DateTime.UtcNow;
        if (!IsAuctionLive(auction, nowUtc))
        {
            TempData["AuctionError"] = "Only a live auction can be ended early.";
            return RedirectToAction(nameof(Index));
        }

        auction.EndTime = nowUtc;
        foreach (var item in auction.AuctionItems)
        {
            item.EndTime = nowUtc;
        }

        await _context.SaveChangesAsync();
        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> CloseAuction(int auctionId, List<int>? deliveredItemIds)
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

        if (!HasAuctionEnded(auction, DateTime.UtcNow))
        {
            TempData["AuctionError"] = "End the auction before closing it.";
            return RedirectToAction(nameof(Index));
        }

        var deliveredIds = (deliveredItemIds ?? new List<int>()).ToHashSet();

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
                .Select(item =>
                {
                    var hasWinner = !string.IsNullOrWhiteSpace(item.CurrentHighestBidderAppUserId);
                    var delivered = hasWinner && deliveredIds.Contains(item.Id);
                    return new AuctionItem
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
                        Status = !hasWinner ? "NoBids" : delivered ? "Received" : "Closed",
                        Notes = item.Notes,
                        SourceItemId = item.SourceItemId
                    };
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

        // Drawdown the linkshell stockpile for any auction items that were
        // sourced from inventory and that the creator confirmed delivered.
        var sourceItemIds = auction.AuctionItems
            .Where(item => item.SourceItemId.HasValue && deliveredIds.Contains(item.Id) && !string.IsNullOrWhiteSpace(item.CurrentHighestBidderAppUserId))
            .Select(item => item.SourceItemId!.Value)
            .Distinct()
            .ToList();
        var inventoryItems = sourceItemIds.Count == 0
            ? new List<Item>()
            : await _context.Items
                .Where(inv => sourceItemIds.Contains(inv.Id) && inv.LinkshellId == auction.LinkshellId)
                .ToListAsync();
        foreach (var auctionItem in auction.AuctionItems.Where(item =>
                     item.SourceItemId.HasValue &&
                     deliveredIds.Contains(item.Id) &&
                     !string.IsNullOrWhiteSpace(item.CurrentHighestBidderAppUserId)))
        {
            var inv = inventoryItems.FirstOrDefault(candidate => candidate.Id == auctionItem.SourceItemId!.Value);
            if (inv is null) continue;
            inv.Quantity = Math.Max(0, inv.Quantity - 1);
            inv.UpdatedAt = DateTime.UtcNow;
            if (inv.Quantity == 0)
            {
                _context.Items.Remove(inv);
            }
        }

        _context.Bids.RemoveRange(auction.AuctionItems.SelectMany(item => item.Bids));
        _context.AuctionItems.RemoveRange(auction.AuctionItems);
        _context.Auctions.Remove(auction);
        await _context.SaveChangesAsync();

        return RedirectToAction(nameof(Index), "AuctionHistory");
    }
}
