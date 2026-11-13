using System;
using System.Data.Common;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using LinkshellManagerDiscordApp.Controllers;
using LinkshellManagerDiscordApp.Data;
using LinkshellManagerDiscordApp.Models;
using LinkshellManagerDiscordApp.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;
using LinkshellManager.TestSupport;

namespace LinkshellManager.PostgresTests;

// Closing an auction locks the winners' memberships and then the auction's items -- the order a
// bid takes them -- before it reads the high bids. A bid that took the item just before the auction
// ended and is still saving when the close arrives is waited for, and the close charges the bid it
// saved. Before, the close read the old high bid, charged that bidder, and deleted the winning bid.
[Collection("postgres")]
public class AuctionCloseRaceTests : IAsyncLifetime
{
    private const string Officer = "officer";

    private readonly PostgresFixture _pg;

    public AuctionCloseRaceTests(PostgresFixture pg) => _pg = pg;

    public Task InitializeAsync() => _pg.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    // The app's controller with only what Close touches, and a signed-in Leader.
    private static ActivityLootController NewActivityController(ApplicationDbContext db)
    {
        var userManager = new UserManager<AppUser>(
            new UserStore<AppUser>(db),
            Options.Create(new IdentityOptions()),
            new PasswordHasher<AppUser>(),
            Array.Empty<IUserValidator<AppUser>>(),
            Array.Empty<IPasswordValidator<AppUser>>(),
            new UpperInvariantLookupNormalizer(),
            new IdentityErrorDescriber(),
            null!,
            NullLogger<UserManager<AppUser>>.Instance);
        var pools = new DkpPoolResolver(db, new DkpPoolProvisioner(db));
        var settings = new GlobalSettingsService(db, new MemoryCache(new MemoryCacheOptions()));
        var permissions = new LinkshellPermissions(
            db, new AdminOverrideService(db, settings, NullLogger<AdminOverrideService>.Instance));
        var http = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
                new[] { new Claim(ClaimTypes.NameIdentifier, Officer) }, authenticationType: "Test")),
            // Empty: the close asks for the live-refresh notifier, which these tests do without.
            RequestServices = new ServiceCollection().BuildServiceProvider(),
        };
        return ActivityControllerFactory.Create<ActivityLootController>(
            http, db, userManager, new MemberActivityService(db),
            new DkpLedgerWriter(db, pools, NullLogger<DkpLedgerWriter>.Instance), pools,
            permissions);
    }

    // One auction with one item, Carol on it at 50, ending at `endsAt`. Bob and Carol have 100 DKP.
    private async Task<(int AuctionId, int ItemId)> SeedAsync(DateTime endsAt)
    {
        await using var db = _pg.CreateContext();
        var linkshell = new Linkshell { LinkshellName = "LS", LootStructure = "Dkp" };
        db.Linkshells.Add(linkshell);
        await db.SaveChangesAsync();
        db.DkpPools.Add(DkpPoolDefaults.BuildDefaultPool(linkshell.Id));
        db.Users.Add(new AppUser { Id = Officer, UserName = Officer, CharacterName = "Boss" });
        db.AppUserLinkshells.Add(new AppUserLinkshell
        {
            LinkshellId = linkshell.Id, AppUserId = Officer, CharacterName = "Boss", Rank = LinkshellRanks.Leader, LinkshellDkp = 0,
        });
        foreach (var (id, name) in new[] { ("bob", "Bob"), ("carol", "Carol") })
        {
            db.Users.Add(new AppUser { Id = id, UserName = id, CharacterName = name });
            db.AppUserLinkshells.Add(new AppUserLinkshell
            {
                LinkshellId = linkshell.Id, AppUserId = id, CharacterName = name, LinkshellDkp = 100,
            });
        }
        var auction = new Auction
        {
            LinkshellId = linkshell.Id,
            AuctionTitle = "Sky drops",
            StartedAt = DateTime.UtcNow.AddMinutes(-5),
            EndTime = endsAt,
        };
        var item = new AuctionItem
        {
            Auction = auction,
            ItemName = "Kirin's Osode",
            StartingBidDkp = 10,
            CurrentHighestBid = 50,
            CurrentHighestBidder = "Carol",
            CurrentHighestBidderAppUserId = "carol",
            Status = "BidPlaced",
        };
        db.Auctions.Add(auction);
        db.AuctionItems.Add(item);
        await db.SaveChangesAsync();
        return (auction.Id, item.Id);
    }

    private async Task<IActionResult> CloseAsync(int auctionId, params IInterceptor[] interceptors)
    {
        await using var db = _pg.CreateContext(interceptors);
        return await NewActivityController(db).CloseAuctionAsync(auctionId, null, CancellationToken.None);
    }

    private async Task<double> BalanceAsync(string appUserId)
    {
        await using var db = _pg.CreateContext();
        return (await db.AppUserLinkshells.AsNoTracking().SingleAsync(l => l.AppUserId == appUserId)).LinkshellDkp ?? 0d;
    }

    // Bob's 55 has its locks and passed its "still live?" check a moment before the end, and is
    // about to save. The officer presses Close just after the end. The close waits for Bob's bid to
    // commit, then charges Bob 55 -- not Carol 50.
    [Fact]
    public async Task CloseArrivingWhileTheLastBidSaves_WaitsAndChargesThatBid()
    {
        // Far enough ahead that Bob's bid (a cold first query included) passes its end check.
        var endsAt = DateTime.UtcNow.AddSeconds(3);
        var (auctionId, itemId) = await SeedAsync(endsAt);

        Task<IActionResult>? close = null;
        var closeWasWaiting = false;
        var pauseBobsSave = new BeforeFirstSave(async () =>
        {
            var untilEnded = endsAt - DateTime.UtcNow + TimeSpan.FromMilliseconds(200);
            if (untilEnded > TimeSpan.Zero)
            {
                await Task.Delay(untilEnded);
            }
            close = CloseAsync(auctionId);
            await Task.Delay(TimeSpan.FromMilliseconds(500));
            closeWasWaiting = !close.IsCompleted;
        });

        AuctionBidResult bob;
        await using (var db = _pg.CreateContext(pauseBobsSave))
        {
            var pools = new DkpPoolResolver(db, new DkpPoolProvisioner(db));
            bob = await AuctionBidService.PlaceBidAsync(
                db, pools, new DkpPoolBalanceService(db, pools), "bob", "bob", itemId, 55, CancellationToken.None);
        }
        var closeResult = await close!;

        Assert.True(bob.Success, bob.Error);
        Assert.True(closeWasWaiting, "The close should wait on the item until Bob's bid commits.");
        Assert.IsType<OkObjectResult>(closeResult);

        Assert.Equal(45d, await BalanceAsync("bob"));
        Assert.Equal(100d, await BalanceAsync("carol"));
        await using var check = _pg.CreateContext();
        var spent = await check.DkpLedgerEntries.AsNoTracking().SingleAsync(e => e.EntryType == "AuctionSpent");
        Assert.Equal("bob", spent.AppUserId);
        Assert.Equal(-55d, spent.Amount);
        var archived = await check.AuctionHistories.AsNoTracking().Include(h => h.AuctionItems).SingleAsync();
        var sold = Assert.Single(archived.AuctionItems);
        Assert.Equal("bob", sold.CurrentHighestBidderAppUserId);
        Assert.Equal(55, sold.CurrentHighestBid);
        Assert.Empty(await check.Auctions.AsNoTracking().ToListAsync());
    }

    // Two officers press Close at once: one closes it, the other is told it is gone. The winner is
    // charged once.
    [Fact]
    public async Task TwoClosesAtOnce_ChargeTheWinnerOnce()
    {
        var (auctionId, _) = await SeedAsync(DateTime.UtcNow.AddMinutes(-1));

        var results = await Task.WhenAll(CloseAsync(auctionId), CloseAsync(auctionId));

        Assert.Single(results, r => r is OkObjectResult);
        Assert.Single(results, r => r is NotFoundObjectResult);
        Assert.Equal(50d, await BalanceAsync("carol"));
        await using var check = _pg.CreateContext();
        Assert.Single(await check.DkpLedgerEntries.AsNoTracking().Where(e => e.EntryType == "AuctionSpent").ToListAsync());
        Assert.Single(await check.AuctionHistories.AsNoTracking().ToListAsync());
    }

    // The "auction closed" Discord post is queued only after the close commits. Queued before, the
    // in-process worker could look for the new history row first, find nothing, and never post.
    [Fact]
    public async Task ClosedDiscordPost_IsQueuedOnlyAfterTheCloseCommits()
    {
        var (auctionId, _) = await SeedAsync(DateTime.UtcNow.AddMinutes(-1));

        var queue = new DiscordAuctionChannelQueue();
        bool? queuedBeforeCommit = null;
        var watchCommit = new BeforeCommit(() => queuedBeforeCommit ??= queue.Reader.TryPeek(out _));
        IActionResult result;
        await using (var db = _pg.CreateContextWithAuctionQueue(queue, watchCommit))
        {
            result = await NewActivityController(db).CloseAuctionAsync(auctionId, null, CancellationToken.None);
        }

        Assert.IsType<OkObjectResult>(result);
        Assert.False(queuedBeforeCommit, "The closed post must not be queued before the close commits.");
        int historyId;
        await using (var check = _pg.CreateContext())
        {
            historyId = await check.AuctionHistories.Select(h => h.Id).SingleAsync();
        }
        Assert.True(queue.Reader.TryRead(out var job));
        Assert.Equal(new AuctionChannelJob(AuctionChannelJobKind.Close, historyId), job);
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
