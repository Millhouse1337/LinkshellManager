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
    [HttpGet("auctions")]
    public async Task<IActionResult> GetAuctionsAsync(int? linkshellId, CancellationToken cancellationToken)
    {
        var appUser = await ResolveAppUserAsync(cancellationToken);
        if (appUser is null)
        {
            return Unauthorized(new { error = "Sign in to load auctions." });
        }

        var accessibleLinkshellIds = await _dbContext.AppUserLinkshells
            .Where(link => link.AppUserId == appUser.Id)
            .Select(link => link.LinkshellId)
            .Distinct()
            .ToListAsync(cancellationToken);

        if (accessibleLinkshellIds.Count == 0)
        {
            return Ok(Array.Empty<ActivityAuctionDto>());
        }

        var selectedLinkshellId = linkshellId
            ?? appUser.PrimaryLinkshellId
            ?? accessibleLinkshellIds.First();

        if (!accessibleLinkshellIds.Contains(selectedLinkshellId))
        {
            return Forbid();
        }

        var nowUtc = DateTime.UtcNow;
        var auctions = await _dbContext.Auctions
            .Include(auction => auction.AuctionItems.OrderBy(item => item.Id))
                .ThenInclude(item => item.Bids.OrderByDescending(bid => bid.BidAmount).ThenBy(bid => bid.CreatedAt))
            .Where(auction => auction.LinkshellId == selectedLinkshellId)
            .OrderBy(auction => auction.StartTime)
            .AsNoTracking()
            .ToListAsync(cancellationToken);

        var availableDkp = await AuctionDkpService.ComputeAvailableDkpAsync(
            _dbContext, appUser.Id, selectedLinkshellId, cancellationToken);

        return Ok(auctions
            .Select(auction => MapAuctionDto(auction, appUser.Id, nowUtc, availableDkp))
            .ToList());
    }

    [HttpGet("auction-history")]
    public async Task<IActionResult> GetAuctionHistoryAsync(int? linkshellId, CancellationToken cancellationToken)
    {
        var appUser = await ResolveAppUserAsync(cancellationToken);
        if (appUser is null)
        {
            return Unauthorized(new { error = "Sign in to load auction history." });
        }

        var accessibleLinkshellIds = await _dbContext.AppUserLinkshells
            .Where(link => link.AppUserId == appUser.Id)
            .Select(link => link.LinkshellId)
            .Distinct()
            .ToListAsync(cancellationToken);

        if (accessibleLinkshellIds.Count == 0)
        {
            return Ok(Array.Empty<ActivityAuctionHistoryDto>());
        }

        var selectedLinkshellId = linkshellId
            ?? appUser.PrimaryLinkshellId
            ?? accessibleLinkshellIds.First();

        if (!accessibleLinkshellIds.Contains(selectedLinkshellId))
        {
            return Forbid();
        }

        var history = await _dbContext.AuctionHistories
            .Include(item => item.AuctionItems.OrderBy(auctionItem => auctionItem.Id))
            .Where(item => item.LinkshellId == selectedLinkshellId)
            .OrderByDescending(item => item.ClosedAt)
            .Take(25)
            .AsNoTracking()
            .ToListAsync(cancellationToken);

        return Ok(history.Select(MapAuctionHistoryDto).ToList());
    }

    [HttpGet("auction-items/{itemId:int}/bids")]
    public async Task<IActionResult> GetAuctionItemBidsAsync(int itemId, CancellationToken cancellationToken)
    {
        var appUser = await ResolveAppUserAsync(cancellationToken);
        if (appUser is null)
        {
            return Unauthorized(new { error = "Sign in to load bid history." });
        }

        var auctionItem = await _dbContext.AuctionItems
            .Include(item => item.Auction)
            .Include(item => item.Bids.OrderByDescending(bid => bid.BidAmount).ThenBy(bid => bid.CreatedAt))
            .AsNoTracking()
            .FirstOrDefaultAsync(item => item.Id == itemId, cancellationToken);

        if (auctionItem is null || auctionItem.Auction is null)
        {
            return NotFound(new { error = "Auction item not found." });
        }

        var membership = await GetMembershipAsync(appUser.Id, auctionItem.Auction.LinkshellId, cancellationToken);
        if (membership is null)
        {
            return Forbid();
        }

        return Ok(auctionItem.Bids.Select(bid => new ActivityAuctionBidDto(
            bid.Id,
            bid.CharacterName,
            bid.BidAmount,
            bid.CreatedAt)).ToList());
    }

    [HttpPost("auctions")]
    public async Task<IActionResult> CreateAuctionAsync(
        [FromBody] ActivityCreateAuctionRequest request,
        CancellationToken cancellationToken)
    {
        var appUser = await ResolveAppUserAsync(cancellationToken);
        if (appUser is null)
        {
            return Unauthorized(new { error = "Sign in to create auctions." });
        }

        var membership = await GetMembershipAsync(appUser.Id, request.LinkshellId, cancellationToken);
        if (membership is null)
        {
            return Forbid();
        }

        if (!TryConvertUserTimeZoneToUtc(request.StartTimeLocal, appUser.TimeZone, out var startTimeUtc) ||
            !TryConvertUserTimeZoneToUtc(request.EndTimeLocal, appUser.TimeZone, out var endTimeUtc))
        {
            return BadRequest(new { error = "Auction start and end times must be valid local date/time values." });
        }

        var normalizedItems = NormalizeAuctionItems(request.Items);
        var validationError = ValidateAuctionRequest(request.Title, startTimeUtc, endTimeUtc, normalizedItems);
        if (validationError is not null)
        {
            return BadRequest(new { error = validationError });
        }

        var auction = new Auction
        {
            LinkshellId = request.LinkshellId,
            AuctionTitle = request.Title.Trim(),
            CreatedBy = appUser.CharacterName ?? appUser.UserName ?? "User",
            CreatedByUserId = appUser.Id,
            StartTime = startTimeUtc,
            EndTime = endTimeUtc,
            StartedAt = null,
            AuctionItems = normalizedItems.Select(item => new AuctionItem
            {
                ItemName = item.ItemName?.Trim(),
                ItemType = item.ItemType?.Trim(),
                StartingBidDkp = item.StartingBidDkp,
                CurrentHighestBid = null,
                CurrentHighestBidder = null,
                CurrentHighestBidderAppUserId = null,
                EndingBidDkp = null,
                StartTime = startTimeUtc,
                EndTime = endTimeUtc,
                Status = "Pending",
                Notes = item.Notes?.Trim(),
                SourceItemId = item.SourceItemId
            }).ToList()
        };

        _dbContext.Auctions.Add(auction);
        await _dbContext.SaveChangesAsync(cancellationToken);

        return Ok(MapAuctionDto(auction, appUser.Id, DateTime.UtcNow));
    }

    [HttpPost("auctions/{auctionId:int}/update")]
    public async Task<IActionResult> UpdateAuctionAsync(
        int auctionId,
        [FromBody] ActivityCreateAuctionRequest request,
        CancellationToken cancellationToken)
    {
        var appUser = await ResolveAppUserAsync(cancellationToken);
        if (appUser is null)
        {
            return Unauthorized(new { error = "Sign in to update auctions." });
        }

        var auction = await _dbContext.Auctions
            .Include(item => item.AuctionItems.OrderBy(auctionItem => auctionItem.Id))
                .ThenInclude(item => item.Bids)
            .FirstOrDefaultAsync(item => item.Id == auctionId, cancellationToken);

        if (auction is null)
        {
            return NotFound(new { error = "Auction not found." });
        }

        if (!CanEditAuction(appUser.Id, auction, DateTime.UtcNow))
        {
            return Forbid();
        }

        var membership = await GetMembershipAsync(appUser.Id, request.LinkshellId, cancellationToken);
        if (membership is null)
        {
            return Forbid();
        }

        if (!TryConvertUserTimeZoneToUtc(request.StartTimeLocal, appUser.TimeZone, out var startTimeUtc) ||
            !TryConvertUserTimeZoneToUtc(request.EndTimeLocal, appUser.TimeZone, out var endTimeUtc))
        {
            return BadRequest(new { error = "Auction start and end times must be valid local date/time values." });
        }

        var normalizedItems = NormalizeAuctionItems(request.Items);
        var validationError = ValidateAuctionRequest(request.Title, startTimeUtc, endTimeUtc, normalizedItems);
        if (validationError is not null)
        {
            return BadRequest(new { error = validationError });
        }

        auction.LinkshellId = request.LinkshellId;
        auction.AuctionTitle = request.Title.Trim();
        auction.StartTime = startTimeUtc;
        auction.EndTime = endTimeUtc;

        var remainingItems = auction.AuctionItems.ToDictionary(item => item.Id);
        foreach (var itemRequest in normalizedItems)
        {
            if (itemRequest.Id > 0 && remainingItems.TryGetValue(itemRequest.Id, out var existingItem))
            {
                existingItem.ItemName = itemRequest.ItemName?.Trim();
                existingItem.ItemType = itemRequest.ItemType?.Trim();
                existingItem.StartingBidDkp = itemRequest.StartingBidDkp;
                existingItem.StartTime = startTimeUtc;
                existingItem.EndTime = endTimeUtc;
                existingItem.Notes = itemRequest.Notes?.Trim();
                existingItem.SourceItemId = itemRequest.SourceItemId;
                remainingItems.Remove(itemRequest.Id);
                continue;
            }

            auction.AuctionItems.Add(new AuctionItem
            {
                ItemName = itemRequest.ItemName?.Trim(),
                ItemType = itemRequest.ItemType?.Trim(),
                StartingBidDkp = itemRequest.StartingBidDkp,
                CurrentHighestBid = null,
                CurrentHighestBidder = null,
                CurrentHighestBidderAppUserId = null,
                EndingBidDkp = null,
                StartTime = startTimeUtc,
                EndTime = endTimeUtc,
                Status = "Pending",
                Notes = itemRequest.Notes?.Trim(),
                SourceItemId = itemRequest.SourceItemId
            });
        }

        if (remainingItems.Count > 0)
        {
            if (remainingItems.Values.Any(item => item.Bids.Count > 0))
            {
                return BadRequest(new { error = "Items that already have bids can't be removed from a live auction." });
            }
            _dbContext.AuctionItems.RemoveRange(remainingItems.Values);
        }

        await _dbContext.SaveChangesAsync(cancellationToken);
        return Ok(MapAuctionDto(auction, appUser.Id, DateTime.UtcNow));
    }

    [HttpPost("auctions/{auctionId:int}/start")]
    public async Task<IActionResult> StartAuctionAsync(int auctionId, CancellationToken cancellationToken)
    {
        var appUser = await ResolveAppUserAsync(cancellationToken);
        if (appUser is null)
        {
            return Unauthorized(new { error = "Sign in to start auctions." });
        }

        var auction = await _dbContext.Auctions
            .Include(item => item.AuctionItems)
            .FirstOrDefaultAsync(item => item.Id == auctionId, cancellationToken);

        if (auction is null)
        {
            return NotFound(new { error = "Auction not found." });
        }

        if (!CanStartAuction(appUser.Id, auction, DateTime.UtcNow))
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

        await _dbContext.SaveChangesAsync(cancellationToken);
        return Ok(MapAuctionDto(auction, appUser.Id, DateTime.UtcNow));
    }

    [HttpPost("auction-items/{itemId:int}/bid")]
    public async Task<IActionResult> MakeAuctionBidAsync(
        int itemId,
        [FromBody] ActivityAuctionBidRequest request,
        CancellationToken cancellationToken)
    {
        var appUser = await ResolveAppUserAsync(cancellationToken);
        if (appUser is null)
        {
            return Unauthorized(new { error = "Sign in to bid on auctions." });
        }

        var auctionItem = await _dbContext.AuctionItems
            .Include(item => item.Auction)
            .Include(item => item.Bids)
            .FirstOrDefaultAsync(item => item.Id == itemId, cancellationToken);

        if (auctionItem is null || auctionItem.Auction is null)
        {
            return NotFound(new { error = "Auction item not found." });
        }

        var membership = await GetMembershipAsync(appUser.Id, auctionItem.Auction.LinkshellId, cancellationToken);
        if (membership is null)
        {
            return Forbid();
        }

        var nowUtc = DateTime.UtcNow;
        if (!IsAuctionLive(auctionItem.Auction, nowUtc))
        {
            return BadRequest(new { error = "This auction has not started yet." });
        }

        if (HasAuctionEnded(auctionItem.Auction, nowUtc))
        {
            return BadRequest(new { error = "This auction has already ended." });
        }

        var bidAmount = request.BidAmount;
        if (bidAmount <= 0)
        {
            return BadRequest(new { error = "Bid amount must be a positive number." });
        }

        const int MaxBidAmount = 1_000_000;
        if (bidAmount > MaxBidAmount)
        {
            return BadRequest(new { error = $"Bid amount cannot exceed {MaxBidAmount:N0}." });
        }

        var minimumBid = Math.Max(auctionItem.StartingBidDkp ?? 0, auctionItem.CurrentHighestBid ?? 0);
        if (bidAmount <= minimumBid)
        {
            return BadRequest(new { error = $"Bid amount must be greater than {minimumBid}." });
        }

        // Available = total minus DKP locked by bids the user is currently
        // winning on OTHER live items. Exclude this item so raising a bid you
        // already hold compares against the replacement, not old + new.
        var availableDkp = await AuctionDkpService.ComputeAvailableDkpAsync(
            _dbContext, appUser.Id, auctionItem.Auction.LinkshellId,
            cancellationToken, excludeAuctionItemId: itemId);
        if (bidAmount > availableDkp)
        {
            return BadRequest(new { error = $"Insufficient available DKP. You have {availableDkp:0.##} available (the rest is locked by bids you're currently winning)." });
        }

        var bid = new Bid
        {
            AuctionItemId = itemId,
            AppUserId = appUser.Id,
            CharacterName = appUser.CharacterName ?? appUser.UserName ?? "User",
            BidAmount = bidAmount,
            CreatedAt = nowUtc
        };

        auctionItem.Bids.Add(bid);
        auctionItem.CurrentHighestBid = bidAmount;
        auctionItem.CurrentHighestBidder = bid.CharacterName;
        auctionItem.CurrentHighestBidderAppUserId = appUser.Id;
        auctionItem.Status = "BidPlaced";

        await _dbContext.SaveChangesAsync(cancellationToken);
        return Ok(new ActivityAuctionBidDto(bid.Id, bid.CharacterName, bid.BidAmount, bid.CreatedAt));
    }

    // Undo the caller's OWN currently-winning bid while the auction is live.
    // The next highest remaining bid becomes the winner ("2nd place"), and the
    // caller is blocked from in-game loot wins for the linkshell's cooldown
    // window (anti bid-then-pull abuse). Re-bidding the same item is allowed.
    [HttpPost("auction-items/{itemId:int}/undo-bid")]
    public async Task<IActionResult> UndoAuctionBidAsync(
        int itemId, CancellationToken cancellationToken)
    {
        var appUser = await ResolveAppUserAsync(cancellationToken);
        if (appUser is null)
        {
            return Unauthorized(new { error = "Sign in to undo a bid." });
        }

        var auctionItem = await _dbContext.AuctionItems
            .Include(item => item.Auction)
            .Include(item => item.Bids)
            .FirstOrDefaultAsync(item => item.Id == itemId, cancellationToken);

        if (auctionItem is null || auctionItem.Auction is null)
        {
            return NotFound(new { error = "Auction item not found." });
        }

        var membership = await GetMembershipAsync(appUser.Id, auctionItem.Auction.LinkshellId, cancellationToken);
        if (membership is null)
        {
            return Forbid();
        }

        var nowUtc = DateTime.UtcNow;
        if (!IsAuctionLive(auctionItem.Auction, nowUtc) || HasAuctionEnded(auctionItem.Auction, nowUtc))
        {
            return BadRequest(new { error = "You can only undo a bid while the auction is live." });
        }

        if (auctionItem.CurrentHighestBidderAppUserId != appUser.Id)
        {
            return BadRequest(new { error = "You can only undo a bid you are currently winning." });
        }

        var cooldownHours = await _dbContext.Linkshells
            .Where(l => l.Id == auctionItem.Auction.LinkshellId)
            .Select(l => l.LootBlockCooldownHours)
            .FirstOrDefaultAsync(cancellationToken);

        var outcome = AuctionDkpService.UndoWinningBid(
            _dbContext, auctionItem, appUser.Id, membership, cooldownHours, nowUtc);
        if (!outcome.Ok)
        {
            return BadRequest(new { error = outcome.Error });
        }

        await _dbContext.SaveChangesAsync(cancellationToken);

        var availableDkp = await AuctionDkpService.ComputeAvailableDkpAsync(
            _dbContext, appUser.Id, auctionItem.Auction.LinkshellId, cancellationToken);

        return Ok(new
        {
            success = true,
            newWinner = outcome.NewWinnerCharacterName,
            newWinningBid = outcome.NewWinningBid,
            availableDkp,
            lootBlockedUntil = membership.LootBiddingBlockedUntil
        });
    }

    // Stops bidding without archiving the run. Pulls the EndTime forward to
    // "now" so the auction transitions from Live → Ended. The creator then
    // archives via the separate /close endpoint, which is where the delivery
    // confirmation + inventory drawdown live.
    [HttpPost("auctions/{auctionId:int}/end")]
    public async Task<IActionResult> EndAuctionAsync(int auctionId, CancellationToken cancellationToken)
    {
        var appUser = await ResolveAppUserAsync(cancellationToken);
        if (appUser is null)
        {
            return Unauthorized(new { error = "Sign in to end auctions." });
        }

        var auction = await _dbContext.Auctions
            .Include(item => item.AuctionItems)
            .FirstOrDefaultAsync(item => item.Id == auctionId, cancellationToken);

        if (auction is null)
        {
            return NotFound(new { error = "Auction not found." });
        }

        if (!IsAuctionCreator(appUser.Id, auction))
        {
            return Forbid();
        }

        var nowUtc = DateTime.UtcNow;
        if (!IsAuctionLive(auction, nowUtc))
        {
            return BadRequest(new { error = "Only a live auction can be ended early." });
        }

        auction.EndTime = nowUtc;
        foreach (var item in auction.AuctionItems)
        {
            item.EndTime = nowUtc;
        }

        await _dbContext.SaveChangesAsync(cancellationToken);
        return Ok(new { success = true });
    }

    [HttpPost("auctions/{auctionId:int}/close")]
    public async Task<IActionResult> CloseAuctionAsync(
        int auctionId,
        [FromBody] ActivityCloseAuctionRequest? request,
        CancellationToken cancellationToken)
    {
        var appUser = await ResolveAppUserAsync(cancellationToken);
        if (appUser is null)
        {
            return Unauthorized(new { error = "Sign in to close auctions." });
        }

        var auction = await _dbContext.Auctions
            .Include(item => item.AuctionItems)
                .ThenInclude(item => item.Bids)
            .FirstOrDefaultAsync(item => item.Id == auctionId, cancellationToken);

        if (auction is null)
        {
            return NotFound(new { error = "Auction not found." });
        }

        if (!IsAuctionCreator(appUser.Id, auction))
        {
            return Forbid();
        }

        if (!HasAuctionEnded(auction, DateTime.UtcNow))
        {
            return BadRequest(new { error = "End the auction before closing it." });
        }

        var deliveredIds = (request?.DeliveredItemIds ?? Array.Empty<int>()).ToHashSet();

        var closedAt = DateTime.UtcNow;
        var history = new AuctionHistory
        {
            LinkshellId = auction.LinkshellId,
            AuctionTitle = auction.AuctionTitle,
            CreatedBy = auction.CreatedBy,
            CreatedByUserId = auction.CreatedByUserId,
            StartTime = auction.StartTime,
            EndTime = auction.EndTime,
            StartedAt = auction.StartedAt,
            ClosedAt = closedAt,
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

        _dbContext.AuctionHistories.Add(history);

        var sourceItemIds = auction.AuctionItems
            .Where(item => item.SourceItemId.HasValue && deliveredIds.Contains(item.Id) && !string.IsNullOrWhiteSpace(item.CurrentHighestBidderAppUserId))
            .Select(item => item.SourceItemId!.Value)
            .Distinct()
            .ToList();
        var inventoryItems = sourceItemIds.Count == 0
            ? new List<Item>()
            : await _dbContext.Items
                .Where(inv => sourceItemIds.Contains(inv.Id) && inv.LinkshellId == auction.LinkshellId)
                .ToListAsync(cancellationToken);
        foreach (var auctionItem in auction.AuctionItems.Where(item =>
                     item.SourceItemId.HasValue &&
                     deliveredIds.Contains(item.Id) &&
                     !string.IsNullOrWhiteSpace(item.CurrentHighestBidderAppUserId)))
        {
            var inv = inventoryItems.FirstOrDefault(candidate => candidate.Id == auctionItem.SourceItemId!.Value);
            if (inv is null) continue;
            inv.Quantity = Math.Max(0, inv.Quantity - 1);
            inv.UpdatedAt = closedAt;
            if (inv.Quantity == 0)
            {
                _dbContext.Items.Remove(inv);
            }
        }

        foreach (var item in auction.AuctionItems.Where(item =>
                     !string.IsNullOrWhiteSpace(item.CurrentHighestBidderAppUserId) &&
                     item.CurrentHighestBid.HasValue &&
                     item.CurrentHighestBid.Value > 0))
        {
            var winnerMembership = await _dbContext.AppUserLinkshells
                .FirstOrDefaultAsync(link =>
                    link.AppUserId == item.CurrentHighestBidderAppUserId &&
                    link.LinkshellId == auction.LinkshellId,
                    cancellationToken);

            if (winnerMembership is null)
            {
                continue;
            }

            var winningBid = item.CurrentHighestBid.GetValueOrDefault();
            winnerMembership.LinkshellDkp = (winnerMembership.LinkshellDkp ?? 0) - winningBid;
            _dbContext.DkpLedgerEntries.Add(new DkpLedgerEntry
            {
                AppUserId = winnerMembership.AppUserId,
                LinkshellId = auction.LinkshellId,
                EntryType = "AuctionSpent",
                Amount = -winningBid,
                Sequence = 1,
                OccurredAt = closedAt,
                CharacterName = winnerMembership.CharacterName,
                EventName = auction.AuctionTitle,
                EventStartTime = auction.StartedAt ?? auction.StartTime,
                EventEndTime = auction.EndTime ?? closedAt,
                ItemName = item.ItemName,
                Details = $"Auction spend from {auction.AuctionTitle ?? "auction"}."
            });
        }

        _dbContext.Bids.RemoveRange(auction.AuctionItems.SelectMany(item => item.Bids));
        _dbContext.AuctionItems.RemoveRange(auction.AuctionItems);
        _dbContext.Auctions.Remove(auction);
        await _dbContext.SaveChangesAsync(cancellationToken);
        await _sheetSync.EnqueueAsync(auction.LinkshellId, cancellationToken);

        return Ok(new { success = true });
    }

    [HttpPost("auction-history/items/{itemId:int}/received")]
    public async Task<IActionResult> MarkAuctionHistoryItemReceivedAsync(int itemId, CancellationToken cancellationToken)
    {
        var appUser = await ResolveAppUserAsync(cancellationToken);
        if (appUser is null)
        {
            return Unauthorized(new { error = "Sign in to update auction history." });
        }

        var item = await _dbContext.AuctionItems
            .Include(auctionItem => auctionItem.AuctionHistory)
            .FirstOrDefaultAsync(auctionItem => auctionItem.Id == itemId, cancellationToken);

        if (item is null || item.AuctionHistory is null)
        {
            return NotFound(new { error = "Auction history item not found." });
        }

        var membership = await GetMembershipAsync(appUser.Id, item.AuctionHistory.LinkshellId, cancellationToken);
        if (membership is null)
        {
            return Forbid();
        }
        // Auction-history status changes are an officer-level action — only
        // members with CanManageAuctions can flip "Closed" ↔ "Received".
        if (!await CanAsync(membership, role => role.CanManageAuctions, cancellationToken))
        {
            return Forbid();
        }

        var previousStatus = item.Status;
        item.Status = AuctionInventoryService.ReceivedStatus;
        await AuctionInventoryService.AdjustForStatusChangeAsync(
            _dbContext,
            item,
            previousStatus,
            item.Status,
            item.AuctionHistory.LinkshellId,
            DateTime.UtcNow,
            cancellationToken);
        await _dbContext.SaveChangesAsync(cancellationToken);
        return Ok(new { success = true });
    }

    [HttpPost("auction-history/items/{itemId:int}/undo")]
    public async Task<IActionResult> UndoAuctionHistoryItemStatusAsync(int itemId, CancellationToken cancellationToken)
    {
        var appUser = await ResolveAppUserAsync(cancellationToken);
        if (appUser is null)
        {
            return Unauthorized(new { error = "Sign in to update auction history." });
        }

        var item = await _dbContext.AuctionItems
            .Include(auctionItem => auctionItem.AuctionHistory)
            .FirstOrDefaultAsync(auctionItem => auctionItem.Id == itemId, cancellationToken);

        if (item is null || item.AuctionHistory is null)
        {
            return NotFound(new { error = "Auction history item not found." });
        }

        var membership = await GetMembershipAsync(appUser.Id, item.AuctionHistory.LinkshellId, cancellationToken);
        if (membership is null)
        {
            return Forbid();
        }
        if (!await CanAsync(membership, role => role.CanManageAuctions, cancellationToken))
        {
            return Forbid();
        }

        var previousStatus = item.Status;
        item.Status = string.Equals(previousStatus, AuctionInventoryService.ReceivedStatus, StringComparison.OrdinalIgnoreCase) ? "Closed" : "Pending";
        if (string.IsNullOrWhiteSpace(item.CurrentHighestBidderAppUserId))
        {
            item.Status = "NoBids";
        }

        await AuctionInventoryService.AdjustForStatusChangeAsync(
            _dbContext,
            item,
            previousStatus,
            item.Status,
            item.AuctionHistory.LinkshellId,
            DateTime.UtcNow,
            cancellationToken);
        await _dbContext.SaveChangesAsync(cancellationToken);
        return Ok(new { success = true });
    }
}
