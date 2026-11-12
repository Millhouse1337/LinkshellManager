using LinkshellManagerDiscordApp.Data;
using LinkshellManagerDiscordApp.Models;
using LinkshellManagerDiscordApp.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace LinkshellManager.Tests;

// P2.9: the Discord bid modal and the Activity bid endpoint share AuctionBidService.CommitBidAsync,
// which re-reads the item and checks the amount under row locks (the races themselves are covered
// against a real Postgres in LinkshellManager.PostgresTests/AuctionBidRaceTests). These pin the
// rules it checks once it has the item: beat the current high, and never commit more DKP than the
// member has across the items they are winning.
public class AuctionBidServiceTests
{
    private const int Ls = 1;

    private static ApplicationDbContext NewDb() =>
        new(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    // A live auction with two items (ids 1 and 2), Alice (100 DKP) and Bob (100 DKP).
    private static void Seed(ApplicationDbContext db, DateTime? endTime = null)
    {
        db.Linkshells.Add(new Linkshell { Id = Ls, LinkshellName = "LS", LootStructure = "Dkp" });
        db.DkpPools.Add(DkpPoolDefaults.BuildDefaultPool(Ls));
        foreach (var (id, name) in new[] { ("alice", "Alice"), ("bob", "Bob") })
        {
            db.Users.Add(new AppUser { Id = id, UserName = id, CharacterName = name });
            db.AppUserLinkshells.Add(new AppUserLinkshell
            {
                LinkshellId = Ls, AppUserId = id, CharacterName = name, LinkshellDkp = 100,
            });
        }

        var auction = new Auction
        {
            Id = 1,
            LinkshellId = Ls,
            StartedAt = DateTime.UtcNow.AddMinutes(-5),
            EndTime = endTime ?? DateTime.UtcNow.AddMinutes(30),
        };
        db.Auctions.Add(auction);
        db.AuctionItems.Add(new AuctionItem { Id = 1, Auction = auction, ItemName = "Ridill", StartingBidDkp = 10 });
        db.AuctionItems.Add(new AuctionItem { Id = 2, Auction = auction, ItemName = "Haubergeon", StartingBidDkp = 10 });
        db.SaveChanges();
    }

    private static Task<AuctionBidResult> BidAsync(ApplicationDbContext db, string appUserId, int itemId, int amount)
    {
        var pools = new DkpPoolResolver(db, new DkpPoolProvisioner(db));
        return AuctionBidService.PlaceBidAsync(
            db, pools, new DkpPoolBalanceService(db, pools), appUserId, appUserId, itemId, amount, CancellationToken.None);
    }

    [Fact]
    public async Task HigherBid_TakesTheItem()
    {
        using var db = NewDb();
        Seed(db);

        Assert.True((await BidAsync(db, "bob", 1, 55)).Success);
        var result = await BidAsync(db, "alice", 1, 60);

        Assert.True(result.Success);
        Assert.NotNull(result.Bid);
        var item = await db.AuctionItems.AsNoTracking().SingleAsync(i => i.Id == 1);
        Assert.Equal(60, item.CurrentHighestBid);
        Assert.Equal("alice", item.CurrentHighestBidderAppUserId);
        Assert.Equal("Alice", item.CurrentHighestBidder);
        Assert.Equal(2, await db.Bids.CountAsync());
    }

    [Fact]
    public async Task LowerOrEqualBid_IsRejected_AndChangesNothing()
    {
        using var db = NewDb();
        Seed(db);
        Assert.True((await BidAsync(db, "alice", 1, 60)).Success);

        var lower = await BidAsync(db, "bob", 1, 55);
        var equal = await BidAsync(db, "bob", 1, 60);

        Assert.False(lower.Success);
        Assert.Equal("Bid must be greater than the current high bid of 60.", lower.Error);
        Assert.False(equal.Success);
        var item = await db.AuctionItems.AsNoTracking().SingleAsync(i => i.Id == 1);
        Assert.Equal(60, item.CurrentHighestBid);
        Assert.Equal("alice", item.CurrentHighestBidderAppUserId);
        Assert.Equal(1, await db.Bids.CountAsync());
    }

    // Alice is winning item 1 at 70, so only 30 of her 100 is free for item 2.
    [Fact]
    public async Task BidOnASecondItem_CannotSpendDkpLockedByTheFirst()
    {
        using var db = NewDb();
        Seed(db);
        Assert.True((await BidAsync(db, "alice", 1, 70)).Success);

        var tooMuch = await BidAsync(db, "alice", 2, 31);
        var fits = await BidAsync(db, "alice", 2, 30);

        Assert.False(tooMuch.Success);
        Assert.Contains("you have 30 available", tooMuch.Error);
        Assert.True(fits.Success);
    }

    // Raising your own winning bid replaces it; it is not old + new.
    [Fact]
    public async Task RaisingYourOwnBid_ComparesAgainstTheReplacementOnly()
    {
        using var db = NewDb();
        Seed(db);
        Assert.True((await BidAsync(db, "alice", 1, 70)).Success);

        Assert.True((await BidAsync(db, "alice", 1, 100)).Success);
        Assert.False((await BidAsync(db, "alice", 1, 101)).Success);
    }

    // Being outbid releases the DKP that bid had locked.
    [Fact]
    public async Task BeingOutbid_FreesTheLockedDkp()
    {
        using var db = NewDb();
        Seed(db);
        Assert.True((await BidAsync(db, "alice", 1, 90)).Success);
        Assert.False((await BidAsync(db, "alice", 2, 20)).Success);

        Assert.True((await BidAsync(db, "bob", 1, 95)).Success);

        Assert.True((await BidAsync(db, "alice", 2, 20)).Success);
    }

    [Fact]
    public async Task EndedAuction_IsRejected_ByTheSharedCore()
    {
        using var db = NewDb();
        Seed(db, endTime: DateTime.UtcNow.AddMinutes(-1));
        var pools = new DkpPoolResolver(db, new DkpPoolProvisioner(db));

        // Called directly, the way the Activity endpoint does after its own gate: the core still
        // refuses an auction that ended (an officer may end it while a bid waits on the lock).
        var result = await AuctionBidService.CommitBidAsync(
            db, pools, new DkpPoolBalanceService(db, pools), "alice", "Alice", Ls, 1, 50,
            DateTime.UtcNow, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("This auction has already ended.", result.Error);
        Assert.Equal(0, await db.Bids.CountAsync());
    }

    [Fact]
    public async Task ItemGoneBeforeTheBidSaves_IsRejected()
    {
        using var db = NewDb();
        Seed(db);
        var pools = new DkpPoolResolver(db, new DkpPoolProvisioner(db));

        var result = await AuctionBidService.CommitBidAsync(
            db, pools, new DkpPoolBalanceService(db, pools), "alice", "Alice", Ls, 99, 50,
            DateTime.UtcNow, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("That auction item was not found.", result.Error);
    }
}
