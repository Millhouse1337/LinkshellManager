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

        var auctionsLocked = await _dbContext.Linkshells
            .Where(l => l.Id == selectedLinkshellId)
            .Select(l => l.AuctionsLocked)
            .FirstOrDefaultAsync(cancellationToken);

        // Available DKP is now PER AUCTION, because each auction can draw from a different pool.
        // Computed once per distinct pool in play (normally one), not once per auction.
        var poolMap = await _dkpPools.GetMapAsync(selectedLinkshellId, cancellationToken);
        var availableByPool = new Dictionary<int, double>();
        foreach (var poolId in auctions.Select(a => a.DkpPoolId ?? poolMap.DefaultPoolId).Distinct())
        {
            availableByPool[poolId] = await AuctionDkpService.ComputePoolAvailableDkpAsync(
                _dbContext, _dkpPoolBalances, appUser.Id, selectedLinkshellId, poolId, cancellationToken);
        }

        return Ok(auctions
            .Select(auction =>
            {
                var poolId = auction.DkpPoolId ?? poolMap.DefaultPoolId;
                return MapAuctionDto(
                    auction, appUser.Id, nowUtc, availableByPool.GetValueOrDefault(poolId), auctionsLocked,
                    poolId,
                    // Only name the pool when there's more than one — that's the client's cue to
                    // hide the chip and look exactly like it did before pools existed.
                    poolMap.HasMultiplePools ? poolMap.NameFor(poolId) : null);
            })
            .ToList());
    }

    // Leadership freezes/unfreezes bidding for the whole linkshell (anti-collusion).
    // Gated by CanLockAuctions.
    [HttpPost("linkshells/{linkshellId:int}/auctions-lock")]
    public async Task<IActionResult> SetAuctionsLockAsync(
        int linkshellId,
        [FromBody] ActivityAuctionsLockRequest request,
        CancellationToken cancellationToken)
    {
        var appUser = await ResolveAppUserAsync(cancellationToken);
        if (appUser is null)
        {
            return Unauthorized(new { error = "Sign in to lock auctions." });
        }

        var membership = await GetMembershipAsync(appUser.Id, linkshellId, cancellationToken);
        if (!await CanAsync(membership, r => r.CanLockAuctions, cancellationToken))
        {
            return Forbid();
        }

        var linkshell = await _dbContext.Linkshells.FirstOrDefaultAsync(l => l.Id == linkshellId, cancellationToken);
        if (linkshell is null)
        {
            return NotFound(new { error = "Linkshell not found." });
        }

        linkshell.AuctionsLocked = request.Locked;
        await _dbContext.SaveChangesAsync(cancellationToken);
        return Ok(new { success = true, locked = linkshell.AuctionsLocked });
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

        // Gil items sell treasury gil for DKP — block if the linkshell has no gil,
        // or if the total gil being auctioned exceeds the treasury balance.
        var requestedGil = normalizedItems
            .Where(item => item.GilAmount is > 0)
            .Sum(item => item.GilAmount!.Value);
        if (requestedGil > 0)
        {
            var availableGil = await _dbContext.RevenueEntries
                .Where(entry => entry.LinkshellId == request.LinkshellId)
                .SumAsync(entry => entry.EntryType == "Expense" ? -entry.Value : entry.Value, cancellationToken);
            if (availableGil <= 0)
            {
                return BadRequest(new { error = "This linkshell has no gil in its treasury to auction." });
            }
            if (requestedGil > availableGil)
            {
                return BadRequest(new { error = $"Not enough gil in the treasury: {availableGil:N0} available, but {requestedGil:N0} is being auctioned." });
            }
        }

        var createPoolMap = await _dkpPools.GetMapAsync(request.LinkshellId, cancellationToken);
        var createPoolId = request.DkpPoolId is int picked && createPoolMap.Pools.Any(pool => pool.Id == picked)
            ? picked
            : createPoolMap.DefaultPoolId;

        var auction = new Auction
        {
            LinkshellId = request.LinkshellId,
            AuctionTitle = request.Title.Trim(),
            CreatedBy = appUser.CharacterName ?? appUser.UserName ?? "User",
            CreatedByUserId = appUser.Id,
            StartTime = startTimeUtc,
            EndTime = endTimeUtc,
            StartedAt = null,
            DkpPoolId = createPoolId,
            AuctionItems = normalizedItems.Select(item => new AuctionItem
            {
                ItemName = ResolveAuctionItemName(item),
                ItemType = item.GilAmount.HasValue ? "Gil" : item.ItemType?.Trim(),
                StartingBidDkp = item.StartingBidDkp,
                CurrentHighestBid = null,
                CurrentHighestBidder = null,
                CurrentHighestBidderAppUserId = null,
                EndingBidDkp = null,
                StartTime = startTimeUtc,
                EndTime = endTimeUtc,
                Status = "Pending",
                Notes = item.Notes?.Trim(),
                SourceItemId = item.GilAmount.HasValue ? null : item.SourceItemId,
                GilAmount = item.GilAmount
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

        var editPoolMap = await _dkpPools.GetMapAsync(request.LinkshellId, cancellationToken);
        var requestedPoolId = request.DkpPoolId is int picked && editPoolMap.Pools.Any(pool => pool.Id == picked)
            ? picked
            : editPoolMap.DefaultPoolId;
        var currentPoolId = auction.DkpPoolId ?? editPoolMap.DefaultPoolId;
        if (requestedPoolId != currentPoolId && auction.AuctionItems.Any(item => item.Bids.Count > 0))
        {
            // Bidders were validated against the balance in the CURRENT pool. Switching pools under
            // them would debit a wallet they never agreed to spend from — and possibly one they
            // can't afford.
            return BadRequest(new { error = "The DKP pool can't be changed once bids have been placed. Close this auction and start a new one." });
        }
        auction.DkpPoolId = requestedPoolId;

        auction.LinkshellId = request.LinkshellId;
        auction.AuctionTitle = request.Title.Trim();
        auction.StartTime = startTimeUtc;
        auction.EndTime = endTimeUtc;

        var remainingItems = auction.AuctionItems.ToDictionary(item => item.Id);
        foreach (var itemRequest in normalizedItems)
        {
            if (itemRequest.Id > 0 && remainingItems.TryGetValue(itemRequest.Id, out var existingItem))
            {
                existingItem.ItemName = ResolveAuctionItemName(itemRequest);
                existingItem.ItemType = itemRequest.GilAmount.HasValue ? "Gil" : itemRequest.ItemType?.Trim();
                existingItem.StartingBidDkp = itemRequest.StartingBidDkp;
                existingItem.StartTime = startTimeUtc;
                existingItem.EndTime = endTimeUtc;
                existingItem.Notes = itemRequest.Notes?.Trim();
                existingItem.SourceItemId = itemRequest.GilAmount.HasValue ? null : itemRequest.SourceItemId;
                existingItem.GilAmount = itemRequest.GilAmount;
                remainingItems.Remove(itemRequest.Id);
                continue;
            }

            auction.AuctionItems.Add(new AuctionItem
            {
                ItemName = ResolveAuctionItemName(itemRequest),
                ItemType = itemRequest.GilAmount.HasValue ? "Gil" : itemRequest.ItemType?.Trim(),
                StartingBidDkp = itemRequest.StartingBidDkp,
                CurrentHighestBid = null,
                CurrentHighestBidder = null,
                CurrentHighestBidderAppUserId = null,
                EndingBidDkp = null,
                StartTime = startTimeUtc,
                EndTime = endTimeUtc,
                Status = "Pending",
                Notes = itemRequest.Notes?.Trim(),
                SourceItemId = itemRequest.GilAmount.HasValue ? null : itemRequest.SourceItemId,
                GilAmount = itemRequest.GilAmount
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

        // Trial members (and any role with bidding off) can't place bids.
        if (!await CanAsync(membership, r => r.CanBid, cancellationToken))
        {
            return BadRequest(new { error = "Your role isn't allowed to place bids on auctions." });
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

        var auctionsLocked = await _dbContext.Linkshells
            .Where(l => l.Id == auctionItem.Auction.LinkshellId)
            .Select(l => l.AuctionsLocked)
            .FirstOrDefaultAsync(cancellationToken);
        if (auctionsLocked)
        {
            return BadRequest(new { error = "Bidding is locked by leadership right now." });
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

        // Starting bid is a suggested opening, not a floor: the first bid is
        // accepted at any positive amount. After that, each bid must beat the
        // current high by at least 1.
        var currentHigh = auctionItem.CurrentHighestBid ?? 0;
        if (currentHigh > 0 && bidAmount <= currentHigh)
        {
            return BadRequest(new { error = $"Bid amount must be greater than the current high bid of {currentHigh}." });
        }

        // Available = the balance in the auction's DKP pool, minus DKP locked by bids the user is
        // currently winning on OTHER live items drawing from it. Exclude this item so raising a bid
        // you already hold compares against the replacement, not old + new.
        var bidPoolMap = await _dkpPools.GetMapAsync(auctionItem.Auction.LinkshellId, cancellationToken);
        var bidPoolId = auctionItem.Auction.DkpPoolId ?? bidPoolMap.DefaultPoolId;
        var availableDkp = await AuctionDkpService.ComputePoolAvailableDkpAsync(
            _dbContext, _dkpPoolBalances, appUser.Id, auctionItem.Auction.LinkshellId, bidPoolId,
            cancellationToken, excludeAuctionItemId: itemId);
        if (bidAmount > availableDkp)
        {
            var poolLabel = bidPoolMap.HasMultiplePools ? $" {bidPoolMap.NameFor(bidPoolId)}" : string.Empty;
            return BadRequest(new { error = $"Insufficient available{poolLabel} DKP. You have {availableDkp:0.##} available (the rest is locked by bids you're currently winning)." });
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
            .Include(item => item.Linkshell)
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

        // Inventory now auto-draws down on close for any inventory-sourced item
        // with a winner — no separate "delivered" tick. DeliveredItemIds is
        // accepted for backward compatibility but ignored.
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
                    // Inventory-sourced winners are auto-delivered (marked
                    // Received); the decrement below transitions through
                    // AuctionInventoryService so the post-close Received/Undo
                    // toggle stays consistent and never double-decrements.
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

        _dbContext.AuctionHistories.Add(history);

        // The pool the bidders were validated against. PINNED — a later remap must never relocate
        // a historical auction purchase. Copied onto the history row because `auction` is deleted
        // below, taking its DkpPoolId with it.
        var auctionPoolId = auction.DkpPoolId
            ?? (await _dkpPools.GetMapAsync(auction.LinkshellId, cancellationToken)).DefaultPoolId;
        history.DkpPoolId = auctionPoolId;

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
            await _dkpLedger.AppendAsync(
                winnerMembership,
                "AuctionSpent",
                -winningBid,
                closedAt,
                DkpPoolRef.Pinned(auctionPoolId),
                new DkpEntryContext(
                    CharacterName: winnerMembership.CharacterName,
                    EventName: auction.AuctionTitle,
                    EventStartTime: auction.StartedAt ?? auction.StartTime,
                    EventEndTime: auction.EndTime ?? closedAt,
                    ItemName: item.ItemName,
                    Details: $"Auction spend from {auction.AuctionTitle ?? "auction"}.",
                    // Link to the new history row so ManualPoints batches this close into one sheet
                    // column (mirrors the web close path).
                    SourceAuctionHistory: history),
                cancellationToken);

            // Gil auctions pay out of the treasury: record the listed gil as a
            // negative RevenueEntry so it appears in revenue and lowers the total.
            if (item.GilAmount.HasValue && item.GilAmount.Value > 0)
            {
                _dbContext.RevenueEntries.Add(new RevenueEntry
                {
                    LinkshellId = auction.LinkshellId,
                    LinkshellName = auction.Linkshell?.LinkshellName,
                    EntryType = "Auction Payout",
                    Category = "Gil",
                    Value = -item.GilAmount.Value,
                    Details = $"Gil auction: {item.ItemName} won by {winnerMembership.CharacterName} for {winningBid} DKP.",
                    OccurredAt = closedAt,
                    CreatedByAppUserId = appUser.Id,
                    CreatedByCharacterName = winnerMembership.CharacterName,
                    CreatedAt = closedAt
                });
            }
        }

        // Auto-drawdown inventory for every inventory-sourced item with a winner,
        // routed through the shared service (transition into "Received").
        foreach (var item in auction.AuctionItems.Where(item =>
                     item.SourceItemId.HasValue &&
                     !string.IsNullOrWhiteSpace(item.CurrentHighestBidderAppUserId)))
        {
            await AuctionInventoryService.AdjustForStatusChangeAsync(
                _dbContext, item, previousStatus: "Closed", newStatus: AuctionInventoryService.ReceivedStatus,
                auction.LinkshellId, closedAt, cancellationToken);
        }

        _dbContext.Bids.RemoveRange(auction.AuctionItems.SelectMany(item => item.Bids));
        _dbContext.AuctionItems.RemoveRange(auction.AuctionItems);
        _dbContext.Auctions.Remove(auction);
        await _dbContext.SaveChangesAsync(cancellationToken);

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
        // "Mark received" is the winner confirming they got their own loot, so
        // only the item's winner may flip it — not officers, not other members.
        if (!IsAuctionItemWinner(item, appUser.Id))
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
        // Winner-only, mirroring "Mark received" — only the winner can un-confirm
        // receipt of their own loot.
        if (!IsAuctionItemWinner(item, appUser.Id))
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

    // "Mark received" / "Undo" are winner-only: the person who won the item is
    // the one who confirms (or unconfirms) receipt of their own loot — not
    // officers, not other members.
    private static bool IsAuctionItemWinner(AuctionItem item, string appUserId)
    {
        return !string.IsNullOrWhiteSpace(item.CurrentHighestBidderAppUserId)
            && string.Equals(item.CurrentHighestBidderAppUserId, appUserId, StringComparison.Ordinal);
    }
}
