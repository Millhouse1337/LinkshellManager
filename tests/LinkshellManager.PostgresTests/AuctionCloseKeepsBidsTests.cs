using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LinkshellManagerDiscordApp.Controllers;
using LinkshellManagerDiscordApp.Data;
using LinkshellManagerDiscordApp.Models;
using LinkshellManagerDiscordApp.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace LinkshellManager.PostgresTests;

// Closing an auction keeps its items and their bids: the same AuctionItem rows move from the
// auction to the new AuctionHistory, and every Bid stays on its item. Before, the close copied the
// items onto new rows and deleted the originals with all their bids.
//
// The DKP and treasury side of the close must not change, so the first test pins exactly what a
// close charges (the amounts the old copy-and-delete close charged for the same auction).
[Collection("postgres")]
public class AuctionCloseKeepsBidsTests : IAsyncLifetime
{
    private readonly PostgresFixture _pg;

    public AuctionCloseKeepsBidsTests(PostgresFixture pg) => _pg = pg;

    public Task InitializeAsync() => _pg.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    private sealed record Seeded(int LinkshellId, int AuctionId, int OsodeId, int RidillId, int GilId, int UnsoldId, int InventoryId, int[] BidIds);

    // One ended auction with four items:
    //   Kirin's Osode -- Carol 40, then Bob 50 (Bob wins)
    //   Ridill        -- from the treasury (last one), Carol 30 (Carol wins)
    //   Gil stack     -- 1,000,000 gil sold for DKP, Bob 20 (Bob wins)
    //   Unsold item   -- no bids
    // Bob and Carol start on 100 DKP.
    private async Task<Seeded> SeedAsync()
    {
        await using var db = _pg.CreateContext();
        var linkshell = new Linkshell { LinkshellName = "LS", LootStructure = "Dkp" };
        db.Linkshells.Add(linkshell);
        await db.SaveChangesAsync();
        db.DkpPools.Add(DkpPoolDefaults.BuildDefaultPool(linkshell.Id));
        db.Users.Add(new AppUser { Id = "officer", UserName = "officer", CharacterName = "Boss" });
        db.AppUserLinkshells.Add(new AppUserLinkshell
        {
            LinkshellId = linkshell.Id, AppUserId = "officer", CharacterName = "Boss", Rank = LinkshellRanks.Leader, LinkshellDkp = 0,
        });
        foreach (var (id, name) in new[] { ("bob", "Bob"), ("carol", "Carol") })
        {
            db.Users.Add(new AppUser { Id = id, UserName = id, CharacterName = name });
            db.AppUserLinkshells.Add(new AppUserLinkshell
            {
                LinkshellId = linkshell.Id, AppUserId = id, CharacterName = name, LinkshellDkp = 100,
            });
        }
        // Someone outside the linkshell, for the "who may see the bids" test.
        db.Users.Add(new AppUser { Id = "stranger", UserName = "stranger", CharacterName = "Stranger" });

        var inventory = new Item { LinkshellId = linkshell.Id, ItemName = "Ridill", Quantity = 1 };
        db.Items.Add(inventory);

        var auction = new Auction
        {
            LinkshellId = linkshell.Id,
            AuctionTitle = "Sky drops",
            StartedAt = DateTime.UtcNow.AddHours(-2),
            EndTime = DateTime.UtcNow.AddMinutes(-1),
        };
        db.Auctions.Add(auction);
        await db.SaveChangesAsync();

        var bidAt = DateTime.UtcNow.AddHours(-1);
        AuctionItem Won(string name, int high, string winnerId, string winner) => new()
        {
            Auction = auction,
            ItemName = name,
            StartingBidDkp = 10,
            CurrentHighestBid = high,
            CurrentHighestBidder = winner,
            CurrentHighestBidderAppUserId = winnerId,
            Status = "BidPlaced",
        };
        var osode = Won("Kirin's Osode", 50, "bob", "Bob");
        osode.Bids.Add(new Bid { AppUserId = "carol", CharacterName = "Carol", BidAmount = 40, CreatedAt = bidAt });
        osode.Bids.Add(new Bid { AppUserId = "bob", CharacterName = "Bob", BidAmount = 50, CreatedAt = bidAt.AddMinutes(1) });
        var ridill = Won("Ridill", 30, "carol", "Carol");
        ridill.SourceItemId = inventory.Id;
        ridill.Bids.Add(new Bid { AppUserId = "carol", CharacterName = "Carol", BidAmount = 30, CreatedAt = bidAt });
        var gil = Won("Gil stack", 20, "bob", "Bob");
        gil.GilAmount = 1_000_000;
        gil.Bids.Add(new Bid { AppUserId = "bob", CharacterName = "Bob", BidAmount = 20, CreatedAt = bidAt });
        var unsold = new AuctionItem { Auction = auction, ItemName = "Unsold item", StartingBidDkp = 5, Notes = "Nobody wanted it" };
        db.AuctionItems.AddRange(osode, ridill, gil, unsold);
        await db.SaveChangesAsync();

        var bidIds = await db.Bids.OrderBy(b => b.Id).Select(b => b.Id).ToArrayAsync();
        return new Seeded(linkshell.Id, auction.Id, osode.Id, ridill.Id, gil.Id, unsold.Id, inventory.Id, bidIds);
    }

    private async Task<IActionResult> CloseAsync(int auctionId)
    {
        await using var db = _pg.CreateContext();
        return await AuctionCloseRaceTests.NewActivityController(db).CloseAuctionAsync(auctionId, null, CancellationToken.None);
    }

    // What the close charges -- the same as before items were kept: each winner's high bid as an
    // AuctionSpent row, the gil stack paid out of the treasury, the treasury item drawn down.
    [Fact]
    public async Task Close_ChargesWinnersExactlyAsBefore()
    {
        var seeded = await SeedAsync();

        Assert.IsType<OkObjectResult>(await CloseAsync(seeded.AuctionId));

        await using var check = _pg.CreateContext();
        var spent = await check.DkpLedgerEntries.AsNoTracking()
            .Where(e => e.EntryType == "AuctionSpent")
            .OrderBy(e => e.AppUserId).ThenBy(e => e.Amount)
            .Select(e => new { e.AppUserId, e.Amount, e.ItemName, e.EventName, e.DkpPoolPinned })
            .ToListAsync();
        Assert.Equal(
            new[]
            {
                new { AppUserId = (string?)"bob", Amount = -50d, ItemName = (string?)"Kirin's Osode", EventName = (string?)"Sky drops", DkpPoolPinned = true },
                new { AppUserId = (string?)"bob", Amount = -20d, ItemName = (string?)"Gil stack", EventName = (string?)"Sky drops", DkpPoolPinned = true },
                new { AppUserId = (string?)"carol", Amount = -30d, ItemName = (string?)"Ridill", EventName = (string?)"Sky drops", DkpPoolPinned = true },
            },
            spent.ToArray());
        Assert.Equal(30d, await BalanceAsync(check, "bob"));
        Assert.Equal(70d, await BalanceAsync(check, "carol"));

        // The gil stack is paid out of the treasury, once, to Bob.
        var payout = await check.JournalEntries.AsNoTracking().Include(e => e.Lines).SingleAsync();
        Assert.Equal(JournalEntrySources.GilAuction, payout.Source);
        Assert.Equal(TreasuryTransactionKinds.PaidGilToMember, payout.TransactionKind);
        Assert.Contains(payout.Lines, line => line.CounterpartyAppUserId == "bob" && Math.Abs(line.Amount) == 1_000_000);

        // The last Ridill left the treasury.
        Assert.False(await check.Items.AnyAsync(i => i.Id == seeded.InventoryId));
    }

    [Fact]
    public async Task Close_KeepsTheSameItemsAndBids_UnderTheHistory()
    {
        var seeded = await SeedAsync();

        Assert.IsType<OkObjectResult>(await CloseAsync(seeded.AuctionId));

        await using var check = _pg.CreateContext();
        Assert.Empty(await check.Auctions.AsNoTracking().ToListAsync());
        var history = await check.AuctionHistories.AsNoTracking().SingleAsync();
        Assert.Equal("Sky drops", history.AuctionTitle);

        var items = await check.AuctionItems.AsNoTracking().Include(i => i.Bids).OrderBy(i => i.Id).ToListAsync();
        // The same rows (same ids), now on the history and off the auction.
        Assert.Equal(new[] { seeded.OsodeId, seeded.RidillId, seeded.GilId, seeded.UnsoldId }, items.Select(i => i.Id).ToArray());
        Assert.All(items, i =>
        {
            Assert.Null(i.AuctionId);
            Assert.Equal(history.Id, i.AuctionHistoryId);
        });
        Assert.Equal(new[] { "Closed", "Received", "Closed", "NoBids" }, items.Select(i => i.Status).ToArray());
        Assert.Equal("Nobody wanted it", items[3].Notes);
        Assert.Equal(1_000_000, items[2].GilAmount);
        // The treasury Item ran out and was deleted, so the kept row lets go of it (SET NULL).
        Assert.Null(items[1].SourceItemId);

        // Every bid is still there, on its own item.
        Assert.Equal(seeded.BidIds, await check.Bids.AsNoTracking().OrderBy(b => b.Id).Select(b => b.Id).ToArrayAsync());
        Assert.Equal(new[] { 40, 50 }, items[0].Bids.OrderBy(b => b.Id).Select(b => b.BidAmount).ToArray());
        Assert.Equal("Carol", Assert.Single(items[1].Bids).CharacterName);
        Assert.Single(items[2].Bids);
        Assert.Empty(items[3].Bids);
    }

    // A second press of Close finds no auction and charges nobody again.
    [Fact]
    public async Task SecondClose_IsRefusedAndChargesNothingMore()
    {
        var seeded = await SeedAsync();

        Assert.IsType<OkObjectResult>(await CloseAsync(seeded.AuctionId));
        Assert.IsType<NotFoundObjectResult>(await CloseAsync(seeded.AuctionId));

        await using var check = _pg.CreateContext();
        Assert.Equal(3, await check.DkpLedgerEntries.CountAsync(e => e.EntryType == "AuctionSpent"));
        Assert.Single(await check.JournalEntries.AsNoTracking().ToListAsync());
        Assert.Single(await check.AuctionHistories.AsNoTracking().ToListAsync());
        Assert.Equal(4, await check.AuctionItems.CountAsync(i => i.AuctionHistoryId != null));
        Assert.Equal(30d, await BalanceAsync(check, "bob"));
        Assert.Equal(70d, await BalanceAsync(check, "carol"));
    }

    // Kept items belong to no open auction: they lock no DKP, and nobody can bid on them.
    [Fact]
    public async Task KeptItems_LockNoDkp_AndTakeNoBids()
    {
        var seeded = await SeedAsync();
        await using (var before = _pg.CreateContext())
        {
            // While open, Bob's two winning bids (50 + 20) are locked.
            Assert.Equal(30d, await AuctionDkpService.ComputeAvailableDkpAsync(before, "bob", seeded.LinkshellId, CancellationToken.None));
        }

        Assert.IsType<OkObjectResult>(await CloseAsync(seeded.AuctionId));

        await using var db = _pg.CreateContext();
        // Now the 70 is charged and nothing is locked any more: 30 left, not 30 - 70.
        Assert.Equal(30d, await AuctionDkpService.ComputeAvailableDkpAsync(db, "bob", seeded.LinkshellId, CancellationToken.None));
        Assert.Empty(await AuctionDkpService.ComputeCommittedDkpByUserAsync(db, seeded.LinkshellId, CancellationToken.None));

        var pools = new DkpPoolResolver(db, new DkpPoolProvisioner(db));
        var bid = await AuctionBidService.PlaceBidAsync(
            db, pools, new DkpPoolBalanceService(db, pools), "carol", "Carol", seeded.OsodeId, 60, CancellationToken.None);
        Assert.False(bid.Success);
        Assert.Equal(4, await db.Bids.CountAsync());
    }

    // The history's bid list: a member of the linkshell sees who bid what; anyone else is refused.
    [Fact]
    public async Task HistoryItemBids_ShowToMembersOnly()
    {
        var seeded = await SeedAsync();
        Assert.IsType<OkObjectResult>(await CloseAsync(seeded.AuctionId));

        await using var db = _pg.CreateContext();
        var ok = Assert.IsType<OkObjectResult>(
            await AuctionCloseRaceTests.NewActivityController(db, signedIn: "carol").GetAuctionItemBidsAsync(seeded.OsodeId, CancellationToken.None));
        var bids = Assert.IsAssignableFrom<IEnumerable<ActivityAuctionBidDto>>(ok.Value).ToList();
        Assert.Equal(new[] { ("Bob", 50), ("Carol", 40) }, bids.Select(b => (b.CharacterName, b.BidAmount)).ToArray());

        await using var other = _pg.CreateContext();
        Assert.IsType<ForbidResult>(
            await AuctionCloseRaceTests.NewActivityController(other, signedIn: "stranger").GetAuctionItemBidsAsync(seeded.OsodeId, CancellationToken.None));

        // The history list counts each item's kept bids.
        await using var list = _pg.CreateContext();
        var historyOk = Assert.IsType<OkObjectResult>(
            await AuctionCloseRaceTests.NewActivityController(list, signedIn: "carol").GetAuctionHistoryAsync(seeded.LinkshellId, CancellationToken.None));
        var history = Assert.Single(Assert.IsAssignableFrom<IEnumerable<ActivityAuctionHistoryDto>>(historyOk.Value));
        Assert.Equal(new[] { 2, 1, 1, 0 }, history.Items.Select(i => i.BidCount).ToArray());
    }

    private static async Task<double> BalanceAsync(ApplicationDbContext db, string appUserId)
        => (await db.AppUserLinkshells.AsNoTracking().SingleAsync(l => l.AppUserId == appUserId)).LinkshellDkp ?? 0d;
}
