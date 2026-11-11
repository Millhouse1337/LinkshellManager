using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LinkshellManagerDiscordApp.Data;
using LinkshellManagerDiscordApp.Models;
using LinkshellManagerDiscordApp.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LinkshellManager.PostgresTests;

// P2.4: Post claims the card with one UPDATE that only matches while it is unposted, and pays it in
// the same transaction. Two officers pressing Post together pay the card once; a payout that fails
// leaves the card unposted so Post can be pressed again.
[Collection("postgres")]
public class PostClaimTests : IAsyncLifetime
{
    private static readonly DateTime Now = new(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);

    private readonly PostgresFixture _pg;

    public PostClaimTests(PostgresFixture pg) => _pg = pg;

    public Task InitializeAsync() => _pg.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    private static WindowEventDkpLedgerService NewLedgerService(ApplicationDbContext db)
    {
        var pools = new DkpPoolResolver(db, new DkpPoolProvisioner(db));
        return new WindowEventDkpLedgerService(
            db,
            new DkpLedgerWriter(db, pools, NullLogger<DkpLedgerWriter>.Instance),
            pools,
            NullLogger<WindowEventDkpLedgerService>.Instance);
    }

    // `cards` unposted Kings cards priced at 10, each with Alice and Bob on them.
    private async Task<int[]> SeedCardsAsync(int cards)
    {
        await using var db = _pg.CreateContext();
        var linkshell = new Linkshell { LinkshellName = "LS" };
        db.Linkshells.Add(linkshell);
        foreach (var (id, name) in new[] { ("alice", "Alice"), ("bob", "Bob") })
        {
            db.Users.Add(new AppUser { Id = id, UserName = id, CharacterName = name });
            db.AppUserLinkshells.Add(new AppUserLinkshell
            {
                Linkshell = linkshell, AppUserId = id, CharacterName = name, LinkshellDkp = 0,
            });
        }

        var made = Enumerable.Range(0, cards).Select(_ => new WindowEvent
        {
            Linkshell = linkshell,
            Name = "Kings",
            Status = WindowEventStatuses.Closed,
            CreatedAtUtc = Now,
            FirstCapturedAtUtc = Now,
            LastCapturedAtUtc = Now,
            EntryType = WindowEventEntryTypes.KingsCamp,
            DkpAmount = 10d,
        }).ToList();
        foreach (var card in made)
        {
            db.WindowEvents.Add(card);
            db.AttendanceSnapshots.Add(new AttendanceSnapshot
            {
                Linkshell = linkshell,
                WindowEvent = card,
                CapturedAtUtc = Now,
                CreatedAtUtc = Now,
                SnapshotStatus = AttendanceSnapshotStatuses.Active,
                EntryCount = 2,
                Entries =
                {
                    new AttendanceSnapshotEntry { CharacterName = "Alice" },
                    new AttendanceSnapshotEntry { CharacterName = "Bob" },
                },
            });
        }
        await db.SaveChangesAsync();
        return made.Select(c => c.Id).ToArray();
    }

    // One officer's Post: load the card (as the controller does), stage a draft, claim and pay.
    private async Task<int?> PostAsync(int cardId, double draftDkp, params IInterceptor[] interceptors)
    {
        await using var db = _pg.CreateContext(interceptors);
        var card = await db.WindowEvents.SingleAsync(w => w.Id == cardId);
        card.DkpAmount = draftDkp;
        return await NewLedgerService(db).PostAndPayAsync(card, DateTime.UtcNow, CancellationToken.None);
    }

    // Two officers press Post on the same card at the same moment, on several cards to give the
    // race a fair chance to overlap. Every card is paid once, by exactly one of the two.
    [Fact]
    public async Task TwoPostsAtOnce_PayTheCardOnce()
    {
        var cardIds = await SeedCardsAsync(5);

        foreach (var cardId in cardIds)
        {
            var results = await Task.WhenAll(PostAsync(cardId, 10d), PostAsync(cardId, 10d));

            Assert.Single(results, r => r is null);
            Assert.Single(results, r => r == 2);
        }

        await using var check = _pg.CreateContext();
        var earned = await check.DkpLedgerEntries.AsNoTracking()
            .Where(e => e.EntryType == "SnapshotEarned")
            .ToListAsync();
        Assert.Equal(cardIds.Length * 2, earned.Count);
        foreach (var cardId in cardIds)
        {
            Assert.Equal(
                new[] { "alice", "bob" },
                earned.Where(e => e.SourceWindowEventId == cardId).Select(e => e.AppUserId).OrderBy(u => u));
        }
        var balances = await check.AppUserLinkshells.AsNoTracking().Select(m => m.LinkshellDkp).ToListAsync();
        Assert.All(balances, dkp => Assert.Equal(cardIds.Length * 10d, dkp));
    }

    // Holds officer A's Post just after its claim, starts officer B's Post on the same card, and
    // checks B is still waiting while A has not committed. B then finds the card posted, pays
    // nobody, and its draft (50) never reaches the card.
    [Fact]
    public async Task SecondPost_WaitsForTheFirst_ThenIsTurnedAway()
    {
        var cardId = (await SeedCardsAsync(1)).Single();

        Task<int?>? officerB = null;
        var bWasWaiting = false;
        var pauseAfterClaim = new OnFirstSave(async () =>
        {
            officerB = PostAsync(cardId, 50d);
            await Task.Delay(TimeSpan.FromMilliseconds(500));
            bWasWaiting = !officerB.IsCompleted;
        });

        var paidByA = await PostAsync(cardId, 10d, pauseAfterClaim);
        var paidByB = await officerB!;

        Assert.True(bWasWaiting, "Officer B's Post should wait on the card until officer A's commits.");
        Assert.Equal(2, paidByA);
        Assert.Null(paidByB);

        await using var check = _pg.CreateContext();
        Assert.Equal(10d, (await check.WindowEvents.AsNoTracking().SingleAsync()).DkpAmount);
        Assert.Equal(2, await check.DkpLedgerEntries.CountAsync());
        Assert.All(await check.AppUserLinkshells.AsNoTracking().ToListAsync(), m => Assert.Equal(10d, m.LinkshellDkp));
    }

    // The payout's save fails (anything that is not a lost payout race). The whole Post rolls back:
    // the card is unposted again, the draft is not kept, nobody is paid -- and the next Post works.
    [Fact]
    public async Task FailedPayout_LeavesTheCardUnposted()
    {
        var cardId = (await SeedCardsAsync(1)).Single();

        var failLedgerSave = new FailLedgerSave();
        await Assert.ThrowsAsync<InvalidOperationException>(() => PostAsync(cardId, 12d, failLedgerSave));

        await using (var check = _pg.CreateContext())
        {
            var card = await check.WindowEvents.AsNoTracking().SingleAsync();
            Assert.Null(card.PostedToSheetAt);
            Assert.Equal(10d, card.DkpAmount);
            Assert.Equal(0, await check.DkpLedgerEntries.CountAsync());
            Assert.All(await check.AppUserLinkshells.AsNoTracking().ToListAsync(), m => Assert.Equal(0d, m.LinkshellDkp));
        }

        Assert.Equal(2, await PostAsync(cardId, 12d));
        await using (var check = _pg.CreateContext())
        {
            Assert.NotNull((await check.WindowEvents.AsNoTracking().SingleAsync()).PostedToSheetAt);
            Assert.All(await check.AppUserLinkshells.AsNoTracking().ToListAsync(), m => Assert.Equal(12d, m.LinkshellDkp));
        }
    }

    // The payout keeps its lost-race retry inside Post's transaction. A failed save in Postgres
    // poisons the whole transaction unless it is rolled back to a savepoint; EF takes one before
    // each save. Here Alice's payout row lands from elsewhere just before Post's ledger save: the
    // index turns that save away, the re-run (same transaction) pays only Bob, and Post commits.
    [Fact]
    public async Task LostPayoutRace_InsidePost_RetriesInTheSameTransaction()
    {
        var cardId = (await SeedCardsAsync(1)).Single();

        var aliceAlreadyPaid = new BeforeFirstLedgerSave(async () =>
        {
            await using var other = _pg.CreateContext();
            var alice = await other.AppUserLinkshells.SingleAsync(m => m.AppUserId == "alice");
            alice.LinkshellDkp += 10d;
            other.DkpLedgerEntries.Add(new DkpLedgerEntry
            {
                LinkshellId = alice.LinkshellId,
                AppUserId = "alice",
                SourceWindowEventId = cardId,
                EntryType = "SnapshotEarned",
                Amount = 10d,
                OccurredAt = Now,
            });
            await other.SaveChangesAsync();
        });

        Assert.Equal(1, await PostAsync(cardId, 10d, aliceAlreadyPaid));

        await using var check = _pg.CreateContext();
        Assert.NotNull((await check.WindowEvents.AsNoTracking().SingleAsync()).PostedToSheetAt);
        var earned = await check.DkpLedgerEntries.AsNoTracking().ToListAsync();
        Assert.Single(earned, e => e.AppUserId == "alice");
        Assert.Single(earned, e => e.AppUserId == "bob");
        Assert.All(await check.AppUserLinkshells.AsNoTracking().ToListAsync(), m => Assert.Equal(10d, m.LinkshellDkp));
    }

    // Runs `then` once, just before this context's first save that adds ledger rows.
    private sealed class BeforeFirstLedgerSave : SaveChangesInterceptor
    {
        private readonly Func<Task> _then;
        private bool _fired;

        public BeforeFirstLedgerSave(Func<Task> then) => _then = then;

        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            var addsLedgerRows = eventData.Context!.ChangeTracker.Entries<DkpLedgerEntry>()
                .Any(e => e.State == EntityState.Added);
            if (!_fired && addsLedgerRows)
            {
                _fired = true;
                await _then();
            }
            return result;
        }
    }

    // Runs `then` the first time this context saves (in PostAndPayAsync that is just after the claim).
    private sealed class OnFirstSave : SaveChangesInterceptor
    {
        private readonly Func<Task> _then;
        private bool _fired;

        public OnFirstSave(Func<Task> then) => _then = then;

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

    // Throws when a save adds ledger rows: the payout fails after the claim and the draft are saved.
    private sealed class FailLedgerSave : SaveChangesInterceptor
    {
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            var addsLedgerRows = eventData.Context!.ChangeTracker.Entries<DkpLedgerEntry>()
                .Any(e => e.State == EntityState.Added);
            if (addsLedgerRows)
            {
                throw new InvalidOperationException("payout failed");
            }
            return ValueTask.FromResult(result);
        }
    }
}
