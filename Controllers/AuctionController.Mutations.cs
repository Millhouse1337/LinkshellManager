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
                ItemName = ResolveAuctionItemName(item),
                ItemType = item.GilAmount.HasValue ? "Gil" : item.ItemType?.Trim(),
                StartingBidDkp = item.StartingBidDkp,
                CurrentHighestBid = null,
                CurrentHighestBidder = null,
                CurrentHighestBidderAppUserId = null,
                EndingBidDkp = null,
                StartTime = ConvertUserTimeZoneToUtc(model.Auction.StartTime, user.TimeZone),
                EndTime = ConvertUserTimeZoneToUtc(model.Auction.EndTime, user.TimeZone),
                Status = "Pending",
                Notes = item.Notes?.Trim(),
                // Gil items sell from the treasury, never from inventory.
                SourceItemId = item.GilAmount.HasValue ? null : item.SourceItemId,
                GilAmount = item.GilAmount
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
                existingItem.ItemName = ResolveAuctionItemName(itemModel);
                existingItem.ItemType = itemModel.GilAmount.HasValue ? "Gil" : itemModel.ItemType?.Trim();
                existingItem.StartingBidDkp = itemModel.StartingBidDkp;
                existingItem.StartTime = auction.StartTime;
                existingItem.EndTime = auction.EndTime;
                existingItem.Notes = itemModel.Notes?.Trim();
                existingItem.SourceItemId = itemModel.GilAmount.HasValue ? null : itemModel.SourceItemId;
                existingItem.GilAmount = itemModel.GilAmount;
                remainingItemsById.Remove(itemModel.Id);
            }
            else
            {
                auction.AuctionItems.Add(new AuctionItem
                {
                    ItemName = ResolveAuctionItemName(itemModel),
                    ItemType = itemModel.GilAmount.HasValue ? "Gil" : itemModel.ItemType?.Trim(),
                    StartingBidDkp = itemModel.StartingBidDkp,
                    StartTime = auction.StartTime,
                    EndTime = auction.EndTime,
                    Status = "Pending",
                    Notes = itemModel.Notes?.Trim(),
                    SourceItemId = itemModel.GilAmount.HasValue ? null : itemModel.SourceItemId,
                    GilAmount = itemModel.GilAmount
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

        var auctionsLocked = await _context.Linkshells
            .Where(l => l.Id == auctionItem.Auction.LinkshellId)
            .Select(l => l.AuctionsLocked)
            .FirstOrDefaultAsync();
        if (auctionsLocked)
        {
            TempData["AuctionError"] = "Bidding is locked by leadership right now.";
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

        // Starting bid is a suggested opening, not a floor: the first bid is
        // accepted at any positive amount. After that, each bid must beat the
        // current high by at least 1.
        var currentHigh = auctionItem.CurrentHighestBid ?? 0;
        if (currentHigh > 0 && bidAmount <= currentHigh)
        {
            TempData["AuctionError"] = $"Bid amount must be greater than the current high bid of {currentHigh}.";
            return RedirectToAction(nameof(Index));
        }

        // Available = total minus DKP locked by bids the user is currently
        // winning on OTHER live items (this item excluded so a raise compares
        // against the replacement, not old + new).
        var availableDkp = await AuctionDkpService.ComputeAvailableDkpAsync(
            _context, user.Id, auctionItem.Auction.LinkshellId,
            HttpContext.RequestAborted, excludeAuctionItemId: auctionItemId);
        if (bidAmount > availableDkp)
        {
            TempData["AuctionError"] = $"Insufficient available DKP. You have {availableDkp:0.##} available (the rest is locked by bids you're currently winning).";
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

    // Leadership freezes/unfreezes bidding across ALL of the linkshell's auctions
    // (anti-collusion: stops a friend overbidding you to release your committed
    // DKP). Gated by the CanLockAuctions permission.
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SetAuctionsLock(int linkshellId, bool locked)
    {
        var user = await RequireCurrentUserAsync();
        if (user is null)
        {
            return Challenge();
        }

        var role = await GetEffectiveRoleAsync(user.Id, linkshellId);
        if (role?.CanLockAuctions != true)
        {
            return Forbid();
        }

        var linkshell = await _context.Linkshells.FirstOrDefaultAsync(l => l.Id == linkshellId);
        if (linkshell is null)
        {
            return NotFound();
        }

        linkshell.AuctionsLocked = locked;
        await _context.SaveChangesAsync();
        TempData["AuctionStatus"] = locked
            ? "Bidding is now locked — no new bids will be accepted until you unlock."
            : "Bidding is unlocked.";
        return RedirectToAction(nameof(Index));
    }

    private async Task<LinkshellRole?> GetEffectiveRoleAsync(string appUserId, int linkshellId)
    {
        var rank = await _context.AppUserLinkshells
            .Where(m => m.AppUserId == appUserId && m.LinkshellId == linkshellId)
            .Select(m => m.Rank)
            .FirstOrDefaultAsync();
        if (rank is null) return null;
        var rankName = string.IsNullOrWhiteSpace(rank) ? "Member" : rank.Trim();
        return await _context.LinkshellRoles
            .AsNoTracking()
            .FirstOrDefaultAsync(r => r.LinkshellId == linkshellId && r.Name == rankName);
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
            .Include(item => item.Linkshell)
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

        // Inventory now auto-draws down on close for any item that was sourced
        // from the stockpile and has a winner — no separate "delivered" tick.
        // `deliveredItemIds` is accepted for backward compatibility but ignored.
        var closedAt = DateTime.UtcNow;

        var auctionHistory = new AuctionHistory
        {
            LinkshellId = auction.LinkshellId,
            AuctionTitle = auction.AuctionTitle,
            CreatedBy = auction.CreatedBy,
            CreatedByUserId = auction.CreatedByUserId,
            StartTime = auction.StartTime,
            EndTime = auction.EndTime,
            StartedAt = auction.StartedAt,
            ClosedAt = closedAt,
            // Carry the per-auction Discord channel id forward so the channel
            // can have its results posted and then be deleted on close.
            DiscordChannelId = auction.DiscordChannelId,
            AuctionItems = auction.AuctionItems
                .OrderBy(item => item.Id)
                .Select(item =>
                {
                    var hasWinner = !string.IsNullOrWhiteSpace(item.CurrentHighestBidderAppUserId);
                    // Inventory-sourced winners are auto-delivered, so mark them
                    // Received up front; the inventory decrement below transitions
                    // through AuctionInventoryService to keep the math consistent
                    // with the post-close Received/Undo toggle (prevents a second
                    // decrement if an officer re-toggles the row later).
                    var autoReceived = hasWinner && item.SourceItemId.HasValue;
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
                        Status = !hasWinner ? "NoBids" : autoReceived ? "Received" : "Closed",
                        Notes = item.Notes,
                        SourceItemId = item.SourceItemId,
                        GilAmount = item.GilAmount
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
                OccurredAt = closedAt,
                CharacterName = winner.CharacterName,
                ItemName = item.ItemName,
                Details = $"Auction spend from {auction.AuctionTitle ?? "auction"}.",
                // Navigation property -> EF resolves the FK to the new
                // AuctionHistory row during SaveChanges; ManualPoints picks
                // these up by SourceAuctionHistoryId so a single close lands
                // in one column on the sheet.
                SourceAuctionHistory = auctionHistory
            });

            // Gil auctions pay out of the treasury: the winner spent DKP, and
            // the linkshell hands over the listed gil. Record that as a negative
            // RevenueEntry so it shows in Manage Revenue and lowers the total.
            if (item.GilAmount.HasValue && item.GilAmount.Value > 0)
            {
                _context.RevenueEntries.Add(new RevenueEntry
                {
                    LinkshellId = auction.LinkshellId,
                    LinkshellName = auction.Linkshell?.LinkshellName,
                    EntryType = "Auction Payout",
                    Category = "Gil",
                    Value = -item.GilAmount.Value,
                    Details = $"Gil auction: {item.ItemName} won by {winner.CharacterName} for {item.CurrentHighestBid.GetValueOrDefault()} DKP.",
                    OccurredAt = closedAt,
                    CreatedByAppUserId = user.Id,
                    CreatedByCharacterName = winner.CharacterName,
                    CreatedAt = closedAt
                });
            }
        }

        // Auto-drawdown the linkshell stockpile for every inventory-sourced item
        // that has a winner. Routed through AuctionInventoryService (transition
        // into "Received") so the close-time decrement and the post-close
        // Received/Undo toggle share one idempotent code path. The history items
        // were created with Status="Received" above to match.
        foreach (var item in auction.AuctionItems.Where(item =>
                     item.SourceItemId.HasValue &&
                     !string.IsNullOrWhiteSpace(item.CurrentHighestBidderAppUserId)))
        {
            await AuctionInventoryService.AdjustForStatusChangeAsync(
                _context, item, previousStatus: "Closed", newStatus: AuctionInventoryService.ReceivedStatus,
                auction.LinkshellId, closedAt);
        }

        _context.Bids.RemoveRange(auction.AuctionItems.SelectMany(item => item.Bids));
        _context.AuctionItems.RemoveRange(auction.AuctionItems);
        _context.Auctions.Remove(auction);
        await _context.SaveChangesAsync();

        // SaveChanges populated AuctionHistory.Id and each ledger entry's
        // SourceAuctionHistoryId. Hand the AuctionHistory id to the queue so
        // ManualPointsAppendService can pull every AuctionSpent entry tied to
        // this close and write them into a single sheet column.
        if (auctionHistory.Id > 0)
        {
            await _sheetSync.EnqueueAuctionDeductionsAsync(auctionHistory.Id);
        }

        return RedirectToAction(nameof(Index), "AuctionHistory");
    }
}
