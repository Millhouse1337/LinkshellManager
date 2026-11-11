using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LinkshellManagerDiscordApp.Data;
using LinkshellManagerDiscordApp.Migrations;
using LinkshellManagerDiscordApp.Models;
using LinkshellManagerDiscordApp.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Xunit;

namespace LinkshellManager.PostgresTests;

// P2.3: one payout row per account per review card. The OneCampCreditPerAccount migration folds the
// duplicates already in the ledger (money kept), then a unique index stops new ones, and Post re-runs
// its payout when that index turns away a racing second payment.
[Collection("postgres")]
public class CampCreditTests : IAsyncLifetime
{
    private const string IndexName = "IX_DkpLedgerEntries_SourceWindowEventId_AppUserId";

    // What the migration's CreateIndex produces, so a test that drops the index can put it back.
    private const string CreateIndexSql =
        "CREATE UNIQUE INDEX IF NOT EXISTS \"" + IndexName + "\" ON \"DkpLedgerEntries\" (\"SourceWindowEventId\", \"AppUserId\") " +
        "WHERE \"EntryType\" = 'SnapshotEarned' AND \"AppUserId\" IS NOT NULL AND \"SourceWindowEventId\" IS NOT NULL;";

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

    // The duplicates production may hold can no longer be inserted once the index exists, so this
    // drops it, seeds them, runs the migration's own fold SQL, and checks the index then builds.
    [Fact]
    public async Task FoldSql_KeepsTheMoneyAndLetsTheIndexBuild()
    {
        try
        {
            await using (var db = _pg.CreateContext())
            {
                await db.Database.ExecuteSqlRawAsync($"DROP INDEX IF EXISTS \"{IndexName}\";");
            }

            int linkshellId;
            int aliceFirst, aliceSecond, aliceAudit, bobOnly, carolFirst;
            await using (var db = _pg.CreateContext())
            {
                var linkshell = new Linkshell { LinkshellName = "LS" };
                db.Linkshells.Add(linkshell);
                foreach (var id in new[] { "alice", "bob", "carol" })
                {
                    db.Users.Add(new AppUser { Id = id, UserName = id, CharacterName = id });
                }
                await db.SaveChangesAsync();
                linkshellId = linkshell.Id;

                // Alice paid twice on card 1 (main 10 + alt 4), and an officer audited the SECOND row.
                var a1 = Row(linkshellId, "alice", 1, "SnapshotEarned", 10, poolId: 7);
                var a2 = Row(linkshellId, "alice", 1, "SnapshotEarned", 4, poolId: 7);
                db.DkpLedgerEntries.AddRange(a1, a2);
                await db.SaveChangesAsync();
                var audit = Row(linkshellId, "alice", 1, "AuditAdjustment", -2, poolId: 7);
                audit.AuditRelatedLedgerEntryId = a2.Id;
                // Bob paid once on card 1: nothing to fold.
                var b1 = Row(linkshellId, "bob", 1, "SnapshotEarned", 10);
                // Carol paid three times on card 2 (two Posts that raced, plus the old page sweep).
                var c1 = Row(linkshellId, "carol", 2, "SnapshotEarned", 5);
                var c2 = Row(linkshellId, "carol", 2, "SnapshotEarned", 5);
                var c3 = Row(linkshellId, "carol", 2, "SnapshotEarned", 5);
                // Not payout rows, or not account-keyed: outside the rule, left alone.
                var bobAudit1 = Row(linkshellId, "bob", 1, "AuditAdjustment", 1);
                var bobAudit2 = Row(linkshellId, "bob", 1, "AuditAdjustment", 1);
                var outside1 = Row(linkshellId, null, 1, "SnapshotEarned", 3);
                var outside2 = Row(linkshellId, null, 1, "SnapshotEarned", 3);
                db.DkpLedgerEntries.AddRange(audit, b1, c1, c2, c3, bobAudit1, bobAudit2, outside1, outside2);
                await db.SaveChangesAsync();

                aliceFirst = a1.Id;
                aliceSecond = a2.Id;
                aliceAudit = audit.Id;
                bobOnly = b1.Id;
                carolFirst = c1.Id;
            }

            await using (var db = _pg.CreateContext())
            {
                await db.Database.ExecuteSqlRawAsync(OneCampCreditPerAccount.FoldDuplicateCreditsSql);
            }

            await using (var db = _pg.CreateContext())
            {
                var rows = await db.DkpLedgerEntries.AsNoTracking().OrderBy(e => e.Id).ToListAsync();

                // Every account's total (what LinkshellDkp caches) is unchanged.
                double Total(string? user) => rows.Where(r => r.AppUserId == user).Sum(r => r.Amount);
                Assert.Equal(12d, Total("alice"));
                Assert.Equal(12d, Total("bob"));
                Assert.Equal(15d, Total("carol"));
                Assert.Equal(6d, Total(null));

                // Alice: one payout row (the oldest) holding 14, in the same pool; the audit now
                // points at it.
                var alicePay = Assert.Single(rows, r => r.AppUserId == "alice" && r.EntryType == "SnapshotEarned");
                Assert.Equal(aliceFirst, alicePay.Id);
                Assert.Equal(14d, alicePay.Amount);
                Assert.Equal(7, alicePay.DkpPoolId);
                Assert.DoesNotContain(rows, r => r.Id == aliceSecond);
                Assert.Equal(aliceFirst, rows.Single(r => r.Id == aliceAudit).AuditRelatedLedgerEntryId);

                // Bob's single row is untouched, and his two audits stay two rows.
                Assert.Equal(10d, rows.Single(r => r.Id == bobOnly).Amount);
                Assert.Equal(2, rows.Count(r => r.AppUserId == "bob" && r.EntryType == "AuditAdjustment"));

                // Carol: one row of 15.
                var carolPay = Assert.Single(rows, r => r.AppUserId == "carol");
                Assert.Equal(carolFirst, carolPay.Id);
                Assert.Equal(15d, carolPay.Amount);

                // Account-less rows are not covered by the index, so they are not folded either.
                Assert.Equal(2, rows.Count(r => r.AppUserId == null));

                // Running it again changes nothing (a second deploy, or a database with no duplicates).
                await db.Database.ExecuteSqlRawAsync(OneCampCreditPerAccount.FoldDuplicateCreditsSql);
                var again = await db.DkpLedgerEntries.AsNoTracking().OrderBy(e => e.Id).ToListAsync();
                Assert.Equal(
                    rows.Select(r => (r.Id, r.Amount, r.AuditRelatedLedgerEntryId)),
                    again.Select(r => (r.Id, r.Amount, r.AuditRelatedLedgerEntryId)));

                // And the index the migration creates next now builds.
                await db.Database.ExecuteSqlRawAsync(CreateIndexSql);
            }
        }
        finally
        {
            // Leave the shared database with its index, whatever happened above.
            await _pg.ResetAsync();
            await using var db = _pg.CreateContext();
            await db.Database.ExecuteSqlRawAsync(CreateIndexSql);
        }
    }

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
                CreatedAtUtc = now,
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
