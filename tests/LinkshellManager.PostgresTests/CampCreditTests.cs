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
using Npgsql;
using Xunit;

namespace LinkshellManager.PostgresTests;

// P2.3: one payout row per account per review card. The OneCampCreditPerAccount migration folded the
// duplicates already in the ledger (money kept; before the squash, so its test is in git history),
// then a unique index stops new ones, and Post re-runs its payout when that index turns away a racing
// second payment.
[Collection("postgres")]
public class CampCreditTests : IAsyncLifetime
{
    private readonly PostgresFixture _pg;

    public CampCreditTests(PostgresFixture pg) => _pg = pg;

    public Task InitializeAsync() => _pg.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    private static DkpLedgerEntry Row(
        int linkshellId, string? appUserId, int cardId, string entryType, double amount, int? poolId = null) => new()
    {
        LinkshellId = linkshellId,
        AppUserId = appUserId,
        SourceWindowEventId = cardId,
        EntryType = entryType,
        Amount = amount,
        DkpPoolId = poolId,
        OccurredAt = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc),
    };

    [Fact]
    public async Task SecondPayoutRowForTheSameAccountAndCardIsRejected()
    {
        int linkshellId;
        await using (var db = _pg.CreateContext())
        {
            var linkshell = new Linkshell { LinkshellName = "LS" };
            db.Linkshells.Add(linkshell);
            db.Users.Add(new AppUser { Id = "alice", UserName = "alice", CharacterName = "Alice" });
            await db.SaveChangesAsync();
            linkshellId = linkshell.Id;
            db.DkpLedgerEntries.Add(Row(linkshellId, "alice", 1, "SnapshotEarned", 10));
            await db.SaveChangesAsync();
        }

        // A second request that never saw the first row.
        await using (var db = _pg.CreateContext())
        {
            db.DkpLedgerEntries.Add(Row(linkshellId, "alice", 1, "SnapshotEarned", 10));
            var ex = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
            Assert.True(UniqueRaceRetry.IsUniqueRejectionOn(ex, new[] { "DkpLedgerEntries" }));
        }

        // Still allowed: audits on the same card, the same account on another card, and
        // account-less rows.
        await using (var db = _pg.CreateContext())
        {
            db.DkpLedgerEntries.AddRange(
                Row(linkshellId, "alice", 1, "AuditAdjustment", -2),
                Row(linkshellId, "alice", 1, "AuditAdjustment", 1),
                Row(linkshellId, "alice", 2, "SnapshotEarned", 10),
                Row(linkshellId, null, 1, "SnapshotEarned", 3),
                Row(linkshellId, null, 1, "SnapshotEarned", 3));
            await db.SaveChangesAsync();
            Assert.Equal(6, await db.DkpLedgerEntries.CountAsync());
        }
    }

    // Lets one save wait while another request runs to completion first.
    private sealed class BeforeLedgerSave : SaveChangesInterceptor
    {
        private readonly Func<Task> _then;
        private bool _fired;

        public BeforeLedgerSave(Func<Task> then) => _then = then;

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

    private static WindowEventDkpLedgerService NewLedgerService(ApplicationDbContext db)
    {
        var pools = new DkpPoolResolver(db, new DkpPoolProvisioner(db));
        return new WindowEventDkpLedgerService(
            db,
            new DkpLedgerWriter(db, pools, NullLogger<DkpLedgerWriter>.Instance),
            pools,
            NullLogger<WindowEventDkpLedgerService>.Instance);
    }

    // Two officers press Post on the same card. Officer B has already read "nobody paid yet" when
    // officer A's payout commits; B's save is turned away by the index, B re-runs, finds A's rows,
    // and pays nobody. Alice is paid 10, once.
    [Fact]
    public async Task RacingPayouts_PayEachAccountOnce()
    {
        var now = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);
        int cardId;
        await using (var db = _pg.CreateContext())
        {
            var linkshell = new Linkshell { LinkshellName = "LS" };
            db.Linkshells.Add(linkshell);
            db.Users.Add(new AppUser { Id = "alice", UserName = "alice", CharacterName = "Alice" });
            db.AppUserLinkshells.Add(new AppUserLinkshell
            {
                Linkshell = linkshell, AppUserId = "alice", CharacterName = "Alice", LinkshellDkp = 0,
            });
            var card = new WindowEvent
            {
                Linkshell = linkshell,
                Name = "Kings",
                Status = WindowEventStatuses.Open,
                CreatedAtUtc = now,
                FirstCapturedAtUtc = now,
                LastCapturedAtUtc = now,
                EntryType = WindowEventEntryTypes.KingsCamp,
                DkpAmount = 10d,
                PostedToSheetAt = now,
            };
            db.WindowEvents.Add(card);
            db.AttendanceSnapshots.Add(new AttendanceSnapshot
            {
                Linkshell = linkshell,
                WindowEvent = card,
                CapturedAtUtc = now,
                SnapshotStatus = AttendanceSnapshotStatuses.Active,
                EntryCount = 1,
                Entries = { new AttendanceSnapshotEntry { CharacterName = "Alice" } },
            });
            await db.SaveChangesAsync();
            cardId = card.Id;
        }

        var paidByA = -1;
        var attemptsByB = 0;
        var officerAPays = new BeforeLedgerSave(async () =>
        {
            await using var dbA = _pg.CreateContext();
            paidByA = await WindowEventDkpLedgerService.RetryIfCampCreditRaceAsync(
                dbA, () => NewLedgerService(dbA).EnsurePostedWindowEventLedgerEntriesAsync(cardId, CancellationToken.None));
        });

        int paidByB;
        await using (var dbB = _pg.CreateContext(officerAPays))
        {
            var ledger = NewLedgerService(dbB);
            paidByB = await WindowEventDkpLedgerService.RetryIfCampCreditRaceAsync(dbB, () =>
            {
                attemptsByB++;
                return ledger.EnsurePostedWindowEventLedgerEntriesAsync(cardId, CancellationToken.None);
            });
        }

        Assert.Equal(1, paidByA);
        Assert.Equal(0, paidByB);
        Assert.Equal(2, attemptsByB);

        await using (var db = _pg.CreateContext())
        {
            var paid = await db.DkpLedgerEntries.AsNoTracking().SingleAsync(e => e.EntryType == "SnapshotEarned");
            Assert.Equal(10d, paid.Amount);
            Assert.Equal(10d, (await db.AppUserLinkshells.AsNoTracking().SingleAsync()).LinkshellDkp);
        }
    }
}
