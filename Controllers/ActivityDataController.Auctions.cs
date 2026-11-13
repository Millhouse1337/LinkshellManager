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

        // The action buttons on each card follow the same rule the endpoints enforce.
        var (isMember, canManageAuctions) = await GetAuctionRightsAsync(appUser.Id, selectedLinkshellId, cancellationToken);

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
                    auction, CanControlAuction(isMember, canManageAuctions), nowUtc, availableByPool.GetValueOrDefault(poolId), auctionsLocked,
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

        // Creating needs Manage auctions, not just membership: Close pays treasury gil to the
        // winner and draws inventory down, so a member could otherwise auction the whole
        // treasury to themselves for 1 DKP.
        var membership = await GetMembershipAsync(appUser.Id, request.LinkshellId, cancellationToken);
        if (!await CanAsync(membership, role => role.CanManageAuctions, cancellationToken))
        {
            return Forbid();
        }

        if (!_timeZones.TryParseUserLocalOrUtc(request.StartTimeLocal, appUser.TimeZone, out var startTimeUtc) ||
            !_timeZones.TryParseUserLocalOrUtc(request.EndTimeLocal, appUser.TimeZone, out var endTimeUtc))
        {
            return BadRequest(new { error = "Auction start and end times must be valid local date/time values." });
        }

        var normalizedItems = NormalizeAuctionItems(request.Items);
        var validationError = ValidateAuctionRequest(request.Title, startTimeUtc, endTimeUtc, normalizedItems);
        if (validationError is not null)
        {
            return BadRequest(new { error = validationError });
        }

        var stockError = await ValidateAuctionGilAsync(request.LinkshellId, normalizedItems, cancellationToken)
            ?? await ValidateAuctionSourceItemsAsync(_dbContext, request.LinkshellId, normalizedItems, cancellationToken);
        if (stockError is not null)
        {
            return BadRequest(new { error = stockError });
        }

        var createPoolMap = await _dkpPools.GetMapAsync(request.LinkshellId, cancellationToken);
        if (!createPoolMap.TryPick(request.DkpPoolId, createPoolMap.DefaultPoolId, out var createPoolId))
        {
            return BadRequest(new { error = DkpPoolMap.UnknownPoolError });
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
            DkpPoolId = createPoolId,
            AuctionItems = normalizedItems.Select(item => new AuctionItem
            {
                ItemName = ResolveAuctionItemName(item),
                ItemType = item.GilAmount.HasValue ? "Gil" : item.ItemType?.Trim(),
                StartingBidDkp = item.StartingBidDkp,
                CurrentHighestBid = null,
                CurrentHighestBidder = null,
                CurrentHighestBidderAppUserId = null,
                Status = "Pending",
                Notes = item.Notes?.Trim(),
                SourceItemId = item.GilAmount.HasValue ? null : item.SourceItemId,
                GilAmount = item.GilAmount
            }).ToList()
        };

        _dbContext.Auctions.Add(auction);
        await _dbContext.SaveChangesAsync(cancellationToken);

        // The creator passed the Manage auctions check above, so they control it.
        return Ok(MapAuctionDto(auction, canControl: true, DateTime.UtcNow));
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

        // Authorize against the auction's OWN linkshell. The request's LinkshellId can't move the
        // auction somewhere else: that would let a check in one linkshell write into another.
        if (request.LinkshellId != auction.LinkshellId)
        {
            return BadRequest(new { error = "An auction can't be moved to a different linkshell." });
        }

        var (isMember, canManageAuctions) = await GetAuctionRightsAsync(appUser.Id, auction.LinkshellId, cancellationToken);
        var canControl = CanControlAuction(isMember, canManageAuctions);
        if (!CanEditAuction(canControl, auction, DateTime.UtcNow))
        {
            return Forbid();
        }

        if (!_timeZones.TryParseUserLocalOrUtc(request.StartTimeLocal, appUser.TimeZone, out var startTimeUtc) ||
            !_timeZones.TryParseUserLocalOrUtc(request.EndTimeLocal, appUser.TimeZone, out var endTimeUtc))
        {
            return BadRequest(new { error = "Auction start and end times must be valid local date/time values." });
        }

        var normalizedItems = NormalizeAuctionItems(request.Items);
        var validationError = ValidateAuctionRequest(request.Title, startTimeUtc, endTimeUtc, normalizedItems);
        if (validationError is not null)
        {
            return BadRequest(new { error = validationError });
        }

        // Same gil and inventory checks as Create, against the auction's own linkshell.
        var stockError = await ValidateAuctionGilAsync(auction.LinkshellId, normalizedItems, cancellationToken)
            ?? await ValidateAuctionSourceItemsAsync(_dbContext, auction.LinkshellId, normalizedItems, cancellationToken);
        if (stockError is not null)
        {
            return BadRequest(new { error = stockError });
        }

        var editPoolMap = await _dkpPools.GetMapAsync(request.LinkshellId, cancellationToken);
        var currentPoolId = auction.DkpPoolId ?? editPoolMap.DefaultPoolId;
        // No pool in the request keeps the auction where it is. It used to mean "the default pool",
        // so an edit that only fixed a title quietly moved an auction into the default pool (or
        // failed with the bids error below even though nobody touched the pool).
        if (!editPoolMap.TryPick(request.DkpPoolId, currentPoolId, out var requestedPoolId))
        {
            return BadRequest(new { error = DkpPoolMap.UnknownPoolError });
        }
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
        return Ok(MapAuctionDto(auction, canControl, DateTime.UtcNow));
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

        var (isMember, canManageAuctions) = await GetAuctionRightsAsync(appUser.Id, auction.LinkshellId, cancellationToken);
        var canControl = CanControlAuction(isMember, canManageAuctions);
        if (!CanStartAuction(canControl, auction, DateTime.UtcNow))
        {
            return Forbid();
        }

        var startedAt = DateTime.UtcNow;
        var plannedDuration = ResolveAuctionDuration(auction, startedAt);
        auction.StartedAt = startedAt;
        auction.EndTime = startedAt.Add(plannedDuration);
        foreach (var item in auction.AuctionItems)
        {
            item.Status = "Live";
        }

        await _dbContext.SaveChangesAsync(cancellationToken);
        return Ok(MapAuctionDto(auction, canControl, DateTime.UtcNow));
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

        // Read-only: CommitBidAsync reads the item again under its lock.
        var auctionItem = await _dbContext.AuctionItems
            .AsNoTracking()
            .Include(item => item.Auction)
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

        // The amount rules (positive, above the high bid, within available DKP) and the locks that
        // stop two bids at once from racing live in one place, shared with the Discord bid modal.
        var characterName = appUser.CharacterName ?? appUser.UserName ?? "User";
        var result = await AuctionBidService.CommitBidAsync(
            _dbContext, _dkpPools, _dkpPoolBalances, appUser.Id, characterName,
            auctionItem.Auction.LinkshellId, itemId, request.BidAmount, nowUtc, cancellationToken);
        if (!result.Success || result.Bid is null)
        {
            return BadRequest(new { error = result.Error ?? "Placing your bid failed." });
        }

        var bid = result.Bid;
        return Ok(new ActivityAuctionBidDto(bid.Id, bid.CharacterName, bid.BidAmount, bid.CreatedAt));
    }

    // Stops bidding without archiving the run. Pulls the EndTime forward to
    // "now" so the auction transitions from Live → Ended. Manage auctions then
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

        var (isMember, canManageAuctions) = await GetAuctionRightsAsync(appUser.Id, auction.LinkshellId, cancellationToken);
        if (!CanControlAuction(isMember, canManageAuctions))
        {
            return Forbid();
        }

        var nowUtc = DateTime.UtcNow;
        if (!IsAuctionLive(auction, nowUtc))
        {
            return BadRequest(new { error = "Only a live auction can be ended early." });
        }

        // The auction's own EndTime is the one every check reads; its items keep no copy.
        auction.EndTime = nowUtc;

        await _dbContext.SaveChangesAsync(cancellationToken);
        return Ok(new { success = true });
    }

    // Throws an auction away -- a mistake, a duplicate, a test. Unlike Close it writes no history,
    // charges no winner and draws no inventory down (inventory only moves on Close, so there is
    // nothing to give back). Any state; Manage auctions, same as End and Close. Deleting the row also
    // takes its Discord cards down (ApplicationDbContext.CollectDeletedAuctionCards).
    [HttpPost("auctions/{auctionId:int}/delete")]
    public async Task<IActionResult> DeleteAuctionAsync(int auctionId, CancellationToken cancellationToken)
    {
        var appUser = await ResolveAppUserAsync(cancellationToken);
        if (appUser is null)
        {
            return Unauthorized(new { error = "Sign in to delete auctions." });
        }

        var auction = await _dbContext.Auctions
            .Include(item => item.AuctionItems)
                .ThenInclude(item => item.Bids)
            .FirstOrDefaultAsync(item => item.Id == auctionId, cancellationToken);

        if (auction is null)
        {
            return NotFound(new { error = "Auction not found." });
        }

        var (isMember, canManageAuctions) = await GetAuctionRightsAsync(appUser.Id, auction.LinkshellId, cancellationToken);
        if (!CanControlAuction(isMember, canManageAuctions))
        {
            return Forbid();
        }

        _dbContext.Bids.RemoveRange(auction.AuctionItems.SelectMany(item => item.Bids));
        _dbContext.AuctionItems.RemoveRange(auction.AuctionItems);
        _dbContext.Auctions.Remove(auction);
        await _dbContext.SaveChangesAsync(cancellationToken);

        return Ok(new { success = true });
    }

    // Charges every winner in one save. Re-run from the top when a winner's balance changed under
    // it (Data/ConcurrencyRetry.cs); a second officer closing the same auction at the same moment
    // also lands here (the auction delete finds no row) and the re-run answers "not found".
    [HttpPost("auctions/{auctionId:int}/close")]
    public Task<IActionResult> CloseAuctionAsync(
        int auctionId,
        [FromBody] ActivityCloseAuctionRequest? request,
        CancellationToken cancellationToken) =>
        ConcurrencyRetry.RunAsync(_dbContext, () => CloseAuctionOnceAsync(auctionId, cancellationToken));

    // How many times the close starts over when a late bid changed an item's winner while it was
    // taking its locks (see LockAuctionForCloseAsync). One more go normally settles it: the auction
    // has ended, so no further bid can land.
    private const int CloseAuctionLockAttempts = 3;

    private async Task<IActionResult> CloseAuctionOnceAsync(int auctionId, CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            var result = await CloseAuctionAttemptAsync(
                auctionId, lastAttempt: attempt >= CloseAuctionLockAttempts, cancellationToken);
            if (result is not null)
            {
                return result;
            }
            // The attempt's transaction rolled back (locks released, nothing saved). Start over
            // from what is in the database now.
            _dbContext.ChangeTracker.Clear();
        }
    }

    // Takes the row locks the close needs, in the SAME ORDER a bid takes them (AuctionBidService):
    // first the memberships of everyone currently winning an item, then the auction's items, both
    // by Id. Returns false when the winners changed while the locks were being taken -- a bid that
    // had its locks before the auction ended committed in between -- and the caller starts over.
    //
    // Why the items: the close reads each item's high bid and charges that bidder. A bid that took
    // the item's lock just before the end could still commit after the close read it; the close
    // then charged the old high bidder and deleted the new winning bid. Holding the items, the
    // close waits for that bid to commit and reads the high bid it saved. A bid that arrives after
    // this waits for the close and then finds the item gone.
    //
    // Why the memberships first: the close's save writes the winners' balances. A bid holds its
    // bidder's membership while it waits for an item, so if the close took the items first, a
    // winner bidding again at that moment would hold the membership the close needs while waiting
    // on an item the close holds -- each waiting on the other until Postgres killed one of them.
    private async Task<bool> LockAuctionForCloseAsync(
        int auctionId, int linkshellId, CancellationToken cancellationToken)
    {
        var winners = await AuctionWinnerIdsAsync(auctionId, cancellationToken);
        if (winners.Count > 0)
        {
            await _dbContext.Database.ExecuteSqlInterpolatedAsync(
                $"""
                 SELECT 1 FROM "AppUserLinkshells"
                 WHERE "LinkshellId" = {linkshellId} AND "AppUserId" = ANY({winners.ToArray()})
                 ORDER BY "Id" FOR UPDATE
                 """,
                cancellationToken);
        }
        await _dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"""SELECT 1 FROM "AuctionItems" WHERE "AuctionId" = {auctionId} ORDER BY "Id" FOR UPDATE""",
            cancellationToken);

        // Read again now that no bid can change them: every winner must be one we locked.
        var lockedWinners = await AuctionWinnerIdsAsync(auctionId, cancellationToken);
        return lockedWinners.All(winners.Contains);
    }

    private async Task<HashSet<string>> AuctionWinnerIdsAsync(int auctionId, CancellationToken cancellationToken)
        => (await _dbContext.AuctionItems
                .AsNoTracking()
                .Where(item => item.AuctionId == auctionId && item.CurrentHighestBidderAppUserId != null)
                .Select(item => item.CurrentHighestBidderAppUserId!)
                .Distinct()
                .ToListAsync(cancellationToken))
            .ToHashSet(StringComparer.Ordinal);

    // One go at the close. Null = the winners moved while locking; nothing was saved and the
    // caller starts over (or, on the last attempt, a 409 asks the officer to press Close again).
    private async Task<IActionResult?> CloseAuctionAttemptAsync(
        int auctionId, bool lastAttempt, CancellationToken cancellationToken)
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

        var (isMember, canManageAuctions) = await GetAuctionRightsAsync(appUser.Id, auction.LinkshellId, cancellationToken);
        if (!CanControlAuction(isMember, canManageAuctions))
        {
            return Forbid();
        }

        if (!HasAuctionEnded(auction, DateTime.UtcNow))
        {
            return BadRequest(new { error = "End the auction before closing it." });
        }

        // The locks are held until the close commits (LockAuctionForCloseAsync). In-memory tests
        // have neither transactions nor row locks, and close one auction at a time.
        var relational = _dbContext.Database.IsRelational() && _dbContext.Database.CurrentTransaction is null;
        await using var transaction = relational
            ? await _dbContext.Database.BeginTransactionAsync(cancellationToken)
            : null;
        if (transaction is not null)
        {
            if (!await LockAuctionForCloseAsync(auction.Id, auction.LinkshellId, cancellationToken))
            {
                // Disposing the transaction without a commit rolls it back and frees the locks.
                return lastAttempt ? Conflict(new { error = ConcurrencyRetry.ConflictMessage }) : null;
            }

            // Read the auction again under the locks: the copy above may hold a high bid that a
            // bid committed over while the close waited. Gone now = another officer's close (or a
            // delete) got there first.
            _dbContext.ChangeTracker.Clear();
            auction = await _dbContext.Auctions
                .Include(item => item.Linkshell)
                .Include(item => item.AuctionItems)
                    .ThenInclude(item => item.Bids)
                .FirstOrDefaultAsync(item => item.Id == auctionId, cancellationToken);
            if (auction is null)
            {
                return NotFound(new { error = "Auction not found." });
            }
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
                        Status = !hasWinner ? "NoBids" : autoReceived ? "Received" : "Closed",
                        Notes = item.Notes,
                        SourceItemId = item.SourceItemId,
                        GilAmount = item.GilAmount
                    };
                })
                .ToList()
        };

        _dbContext.AuctionHistories.Add(history);

        // The pool the bidders were validated against. PINNED on each winner's ledger row — a later
        // remap must never relocate a historical auction purchase. (The history row no longer keeps
        // its own copy: nothing read it, and the ledger rows already say which pool paid.)
        var auctionPoolId = auction.DkpPoolId
            ?? (await _dkpPools.GetMapAsync(auction.LinkshellId, cancellationToken)).DefaultPoolId;

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
                    ItemName: item.ItemName,
                    Details: $"Auction spend from {auction.AuctionTitle ?? "auction"}."),
                cancellationToken,
                // Affordability was enforced at bid time (AuctionBidService). Re-blocking at close
                // would strand a whole auction over one winner whose balance moved since.
                DkpOverdraft.Allow);

            // Gil auctions pay out of the treasury. The amount is POSITIVE and the direction lives in
            // which categories the two halves name — the old row stored a negative value and an
            // "Auction Payout" type, a third vocabulary in a column that already held two.
            //
            // The winner goes on the counterparty of the gil-paid-to-members half, which is where
            // "who did we pay" belongs. The old row put the winner's name in CreatedByCharacterName,
            // conflating the person paid with the person who recorded it.
            if (item.GilAmount.HasValue && item.GilAmount.Value > 0)
            {
                await _treasuryJournal.RecordAsync(
                    auction.LinkshellId,
                    new TreasuryEntryRequest(
                        TreasuryTransactionKinds.PaidGilToMember,
                        item.GilAmount.Value,
                        closedAt,
                        Memo: $"Gil auction: {item.ItemName} won by {winnerMembership.CharacterName} for {winningBid} DKP.",
                        CounterpartyAppUserId: winnerMembership.AppUserId,
                        CounterpartyCharacterName: winnerMembership.CharacterName,
                        Source: JournalEntrySources.GilAuction),
                    new TreasuryActor(appUser.Id, appUser.CharacterName),
                    cancellationToken);
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

        if (transaction is null)
        {
            await _dbContext.SaveChangesAsync(cancellationToken);
            return Ok(new { success = true });
        }

        // The "auction closed" Discord post waits for the COMMIT, so the worker can read the new
        // history row (ApplicationDbContext.HoldAuctionCloseAnnouncements).
        _dbContext.HoldAuctionCloseAnnouncements();
        try
        {
            await _dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            _dbContext.ReleaseHeldAuctionCloseAnnouncements();
        }
        finally
        {
            _dbContext.DropHeldAuctionCloseAnnouncements();
        }

        // The save already told open clients to refresh, but before the commit, so a quick one
        // could re-read the auction while it still looked open. Tell them again now that it is gone.
        var changeNotifier = HttpContext?.RequestServices.GetService<LinkshellChangeNotifier>();
        changeNotifier?.Notify(auction.LinkshellId, LinkshellChangeNotifier.Areas.Auctions);
        changeNotifier?.Notify(auction.LinkshellId, LinkshellChangeNotifier.Areas.Dkp);
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
