using System;
using System.Data.Common;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LinkshellManagerDiscordApp.Data;
using LinkshellManagerDiscordApp.Models;
using LinkshellManagerDiscordApp.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Xunit;

namespace LinkshellManager.PostgresTests;

// P2.9: a bid locks the bidder's membership and then the item before it reads the high bid, and
// holds both until it commits. Two bids on one item take turns, so the lower one can never land
// last and win; two bids by one member take turns, so together they never spend more than the
// member has.
[Collection("postgres")]
public class AuctionBidRaceTests : IAsyncLifetime
{
    private readonly PostgresFixture _pg;

    public AuctionBidRaceTests(PostgresFixture pg) => _pg = pg;

    public Task InitializeAsync() => _pg.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    // A live auction with `items` items, each already bid to 50 by Carol. Alice and Bob have
    // `balance` DKP each (Carol has plenty).
    private async Task<int[]> SeedAsync(int items, double balance)
    {
        await using var db = _pg.CreateContext();
        var linkshell = new Linkshell { LinkshellName = "LS", LootStructure = "Dkp" };
        db.Linkshells.Add(linkshell);
        await db.SaveChangesAsync();
        // Provisioned up front, so no bid has to create the pool inside its transaction.
        db.DkpPools.Add(DkpPoolDefaults.BuildDefaultPool(linkshell.Id));
        foreach (var (id, name, dkp) in new[] { ("alice", "Alice", balance), ("bob", "Bob", balance), ("carol", "Carol", 10_000d) })
        {
            db.Users.Add(new AppUser { Id = id, UserName = id, CharacterName = name });
            db.AppUserLinkshells.Add(new AppUserLinkshell
            {
                LinkshellId = linkshell.Id, AppUserId = id, CharacterName = name, LinkshellDkp = dkp,
            });
        }

        var auction = new Auction
        {
            LinkshellId = linkshell.Id,
            StartedAt = DateTime.UtcNow.AddMinutes(-5),
            EndTime = DateTime.UtcNow.AddHours(1),
        };
        db.Auctions.Add(auction);
        var made = Enumerable.Range(1, items).Select(n => new AuctionItem
        {
            Auction = auction,
            ItemName = $"Item {n}",
            StartingBidDkp = 10,
            CurrentHighestBid = 50,
            CurrentHighestBidder = "Carol",
            CurrentHighestBidderAppUserId = "carol",
            Status = "BidPlaced",
        }).ToList();
        db.AuctionItems.AddRange(made);
        await db.SaveChangesAsync();
        return made.Select(i => i.Id).ToArray();
    }

    // One member's bid, start to finish, on its own context (its own "request").
    private async Task<AuctionBidResult> BidAsync(string appUserId, int itemId, int amount, params IInterceptor[] interceptors)
    {
        await using var db = _pg.CreateContext(interceptors);
        var pools = new DkpPoolResolver(db, new DkpPoolProvisioner(db));
        return await AuctionBidService.PlaceBidAsync(
            db, pools, new DkpPoolBalanceService(db, pools), appUserId, appUserId, itemId, amount, CancellationToken.None);
    }

    // The finding's example, on several items to give the race a fair chance to overlap: Alice
    // bids 60 and Bob 55 at the same moment against a high of 50. Alice always ends up winning.
    [Fact]
    public async Task SixtyAndFiftyFiveAtOnce_SixtyWins()
    {
        // Enough DKP that winning all five never runs anyone short.
        var itemIds = await SeedAsync(items: 5, balance: 1_000);

        foreach (var itemId in itemIds)
        {
            await Task.WhenAll(BidAsync("alice", itemId, 60), BidAsync("bob", itemId, 55));
        }

        await using var check = _pg.CreateContext();
        var items = await check.AuctionItems.AsNoTracking().ToListAsync();
        Assert.All(items, item =>
        {
            Assert.Equal(60, item.CurrentHighestBid);
            Assert.Equal("alice", item.CurrentHighestBidderAppUserId);
        });
    }

    // The worst order, made to happen: Bob's 55 has passed its checks and is about to save when
    // Alice's 60 arrives. Alice waits for Bob's bid to commit, then reads 55, beats it, and wins.
    // Before the lock, Alice read the old 50, saved first, and Bob's later save took the item at 55.
    [Fact]
    public async Task HigherBid_ArrivingWhileTheLowerSaves_WaitsAndWins()
    {
        var itemId = (await SeedAsync(items: 1, balance: 100)).Single();

        Task<AuctionBidResult>? alice = null;
        var aliceWasWaiting = false;
        var pauseBobsSave = new BeforeFirstSave(async () =>
        {
            alice = BidAsync("alice", itemId, 60);
            await Task.Delay(TimeSpan.FromMilliseconds(500));
            aliceWasWaiting = !alice.IsCompleted;
        });

        var bob = await BidAsync("bob", itemId, 55, pauseBobsSave);
        var aliceResult = await alice!;

        Assert.True(aliceWasWaiting, "Alice's bid should wait on the item until Bob's commits.");
        Assert.True(bob.Success);
        Assert.True(aliceResult.Success);
        await using var check = _pg.CreateContext();
        var item = await check.AuctionItems.AsNoTracking().SingleAsync();
        Assert.Equal(60, item.CurrentHighestBid);
        Assert.Equal("alice", item.CurrentHighestBidderAppUserId);
    }

    // The other order: Alice's 60 is about to save when Bob's 55 arrives. Bob waits, then reads 60
    // and is told to bid higher. His bid is not recorded.
    [Fact]
    public async Task LowerBid_ArrivingWhileTheHigherSaves_IsTurnedAway()
    {
        var itemId = (await SeedAsync(items: 1, balance: 100)).Single();

        Task<AuctionBidResult>? bob = null;
        var pauseAlicesSave = new BeforeFirstSave(async () =>
        {
            bob = BidAsync("bob", itemId, 55);
            await Task.Delay(TimeSpan.FromMilliseconds(500));
        });

        var alice = await BidAsync("alice", itemId, 60, pauseAlicesSave);
        var bobResult = await bob!;

        Assert.True(alice.Success);
        Assert.False(bobResult.Success);
        Assert.Equal("Bid must be greater than the current high bid of 60.", bobResult.Error);
        await using var check = _pg.CreateContext();
        Assert.Equal(60, (await check.AuctionItems.AsNoTracking().SingleAsync()).CurrentHighestBid);
        Assert.Equal(new[] { "alice" }, await check.Bids.AsNoTracking().Select(b => b.AppUserId).ToListAsync());
    }

    // Alice has 100 DKP and bids 70 on two items at the same moment. Each bid alone fits; both
    // together would commit 140. The second waits for the first, then sees only 30 free.
    [Fact]
    public async Task TwoBidsByOneMemberAtOnce_NeverCommitMoreThanTheyHave()
    {
        var itemIds = await SeedAsync(items: 2, balance: 100);

        Task<AuctionBidResult>? second = null;
        var secondWasWaiting = false;
        var pauseFirstSave = new BeforeFirstSave(async () =>
        {
            second = BidAsync("alice", itemIds[1], 70);
            await Task.Delay(TimeSpan.FromMilliseconds(500));
            secondWasWaiting = !second.IsCompleted;
        });

        var first = await BidAsync("alice", itemIds[0], 70, pauseFirstSave);
        var secondResult = await second!;

        Assert.True(secondWasWaiting, "Alice's second bid should wait on her membership until the first commits.");
        Assert.True(first.Success);
        Assert.False(secondResult.Success);
        Assert.Contains("you have 30 available", secondResult.Error);

        await using var check = _pg.CreateContext();
        var committed = await check.AuctionItems.AsNoTracking()
            .Where(i => i.CurrentHighestBidderAppUserId == "alice")
            .SumAsync(i => i.CurrentHighestBid ?? 0);
        Assert.Equal(70, committed);
    }

    // An officer ends the auction early while a bid waits behind the End's save (which writes the
    // items, so it holds them). The bid took its clock before it waited; when it wakes, the
    // auction's new EndTime is later than that clock. It must still be turned away.
    [Fact]
    public async Task BidQueuedBehindAnEarlyEnd_IsTurnedAway()
    {
        var itemId = (await SeedAsync(items: 1, balance: 100)).Single();

        await using var officer = _pg.CreateContext();
        await using var end = await officer.Database.BeginTransactionAsync();
        await officer.Database.ExecuteSqlInterpolatedAsync(
            $"""SELECT 1 FROM "AuctionItems" WHERE "Id" = {itemId} FOR UPDATE""");

        var bid = BidAsync("alice", itemId, 60);
        await Task.Delay(TimeSpan.FromMilliseconds(500));
        Assert.False(bid.IsCompleted, "Alice's bid should wait on the item until the End commits.");

        var endedAt = DateTime.UtcNow;
        // What End does: only the auction's own EndTime moves.
        var auction = await officer.Auctions.SingleAsync();
        auction.EndTime = endedAt;
        await officer.SaveChangesAsync();
        await end.CommitAsync();

        var result = await bid;
        Assert.False(result.Success);
        Assert.Equal("This auction has already ended.", result.Error);
        await using var check = _pg.CreateContext();
        Assert.Equal(50, (await check.AuctionItems.AsNoTracking().SingleAsync()).CurrentHighestBid);
        Assert.Empty(await check.Bids.AsNoTracking().ToListAsync());
    }

    // The Discord card edit for a bid is queued only after the bid commits. Queued before (inside
    // the bid's transaction), the in-process Discord worker could read the item first and post the
    // old high bid.
    [Fact]
    public async Task DiscordCardEdit_IsQueuedOnlyAfterTheBidCommits()
    {
        var itemId = (await SeedAsync(items: 1, balance: 100)).Single();
        int auctionId;
        await using (var read = _pg.CreateContext())
        {
            auctionId = await read.Auctions.Select(a => a.Id).SingleAsync();
        }

        var queue = new DiscordAuctionChannelQueue();
        bool? queuedBeforeCommit = null;
        var watchCommit = new BeforeCommit(() => queuedBeforeCommit ??= queue.Reader.TryPeek(out _));
        await using var db = _pg.CreateContextWithAuctionQueue(queue, watchCommit);
        var pools = new DkpPoolResolver(db, new DkpPoolProvisioner(db));
        var result = await AuctionBidService.PlaceBidAsync(
            db, pools, new DkpPoolBalanceService(db, pools), "alice", "alice", itemId, 60, CancellationToken.None);

        Assert.True(result.Success);
        Assert.False(queuedBeforeCommit, "The card edit must not be queued before the bid commits.");
        Assert.True(queue.Reader.TryRead(out var job));
        Assert.Equal(new AuctionChannelJob(AuctionChannelJobKind.Update, auctionId), job);
        Assert.False(queue.Reader.TryRead(out _));
    }

    // Runs `then` just before this context commits a transaction.
    private sealed class BeforeCommit : DbTransactionInterceptor
    {
        private readonly Action _then;

        public BeforeCommit(Action then) => _then = then;

        public override ValueTask<InterceptionResult> TransactionCommittingAsync(
            DbTransaction transaction, TransactionEventData eventData, InterceptionResult result,
            CancellationToken cancellationToken = default)
        {
            _then();
            return ValueTask.FromResult(result);
        }
    }

    // Runs `then` once, just before this context's first save -- after the bid has taken its locks
    // and passed its checks.
    private sealed class BeforeFirstSave : SaveChangesInterceptor
    {
        private readonly Func<Task> _then;
        private bool _fired;

        public BeforeFirstSave(Func<Task> then) => _then = then;

        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (!_fired)
            {
                _fired = true;
                await _then();
            }
            return result;
        }
    }
}
