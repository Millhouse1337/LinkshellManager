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
using Xunit;

namespace LinkshellManager.PostgresTests;

// P2.8: one membership per account per linkshell, one participation per account per event. The two
// migrations merge the duplicates production may already hold (money kept), then unique indexes stop
// new ones, and the join paths turn the index's "no" into "already a member" or a re-run.
[Collection("postgres")]
public class OneMembershipOneParticipationTests : IAsyncLifetime
{
    private const string MembershipIndex = "IX_AppUserLinkshells_LinkshellId_AppUserId";
    private const string ParticipationIndex = "IX_AppUserEvents_EventId_AppUserId";

    // What the migrations' CreateIndex produces, so a test that drops an index can put it back.
    private const string CreateMembershipIndexSql =
        "CREATE UNIQUE INDEX IF NOT EXISTS \"" + MembershipIndex + "\" ON \"AppUserLinkshells\" (\"LinkshellId\", \"AppUserId\") " +
        "WHERE \"AppUserId\" IS NOT NULL;";
    private const string CreateParticipationIndexSql =
        "CREATE UNIQUE INDEX IF NOT EXISTS \"" + ParticipationIndex + "\" ON \"AppUserEvents\" (\"EventId\", \"AppUserId\") " +
        "WHERE \"AppUserId\" IS NOT NULL;";

    private static readonly DateTime Now = new(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);

    private readonly PostgresFixture _pg;

    public OneMembershipOneParticipationTests(PostgresFixture pg) => _pg = pg;

    public Task InitializeAsync() => _pg.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    private async Task<int> SeedLinkshellAsync(params string[] userIds)
    {
        await using var db = _pg.CreateContext();
        var linkshell = new Linkshell { LinkshellName = "LS" };
        db.Linkshells.Add(linkshell);
        foreach (var id in userIds)
        {
            db.Users.Add(new AppUser { Id = id, UserName = id, CharacterName = id });
        }
        await db.SaveChangesAsync();
        return linkshell.Id;
    }

    private static AppUserLinkshell Member(int linkshellId, string? appUserId, double dkp, string? rank = "Member") => new()
    {
        LinkshellId = linkshellId,
        AppUserId = appUserId,
        LinkshellDkp = dkp,
        Rank = rank,
        Status = "Active",
    };

    // ------------------------------------------------------------------ memberships ---

    [Fact]
    public async Task MembershipMergeSql_KeepsTheMoney_MovesChartRows_AndLetsTheIndexBuild()
    {
        try
        {
            await using (var db = _pg.CreateContext())
            {
                await db.Database.ExecuteSqlRawAsync($"DROP INDEX IF EXISTS \"{MembershipIndex}\";");
            }

            var linkshellId = await SeedLinkshellAsync("alice", "bob");
            int aliceKeep, aliceDup1, aliceDup2, bobOnly, popItemId, wishId;
            await using (var db = _pg.CreateContext())
            {
                // Alice joined three times (a triple-clicked sign-up). Her DKP was split across the
                // rows; the newest row is the one an officer promoted to Officer, and the second holds
                // the DKP import's seed.
                var keep = Member(linkshellId, "alice", 10, "Member");
                keep.CharacterName = null;
                var dup1 = Member(linkshellId, "alice", 25, "Member");
                dup1.CharacterName = "Alice";
                dup1.SeededDkpEarned = 400;
                dup1.SeededDkpSpent = 150;
                dup1.DateJoined = Now.AddDays(-30);
                var dup2 = Member(linkshellId, "alice", 5, "Officer");
                dup2.DiscordUserId = "111";
                var bob = Member(linkshellId, "bob", 7);
                // Two rows without an account are not a person to dedupe: left alone.
                var ghost1 = Member(linkshellId, null, 1);
                var ghost2 = Member(linkshellId, null, 1);
                db.AppUserLinkshells.AddRange(keep, dup1, dup2, bob, ghost1, ghost2);
                await db.SaveChangesAsync();
                aliceKeep = keep.Id;
                aliceDup1 = dup1.Id;
                aliceDup2 = dup2.Id;
                bobOnly = bob.Id;

                // Chart rows that point at the membership by Id (no foreign key).
                db.ChartMemberKeyItems.AddRange(
                    // Ticked on the kept row AND a duplicate: one tick survives, on the kept row.
                    new ChartMemberKeyItem { LinkshellId = linkshellId, Board = "Sky", KeyItemName = "Ra", MembershipId = aliceKeep, CharacterName = "Alice" },
                    new ChartMemberKeyItem { LinkshellId = linkshellId, Board = "Sky", KeyItemName = "Ra", MembershipId = aliceDup1, CharacterName = "Alice" },
                    // Ticked on both duplicates only: one survives, moved to the kept row.
                    new ChartMemberKeyItem { LinkshellId = linkshellId, Board = "Sky", KeyItemName = "Al", MembershipId = aliceDup1, CharacterName = "Alice" },
                    new ChartMemberKeyItem { LinkshellId = linkshellId, Board = "Sky", KeyItemName = "Al", MembershipId = aliceDup2, CharacterName = "Alice" },
                    // Bob's own tick is untouched.
                    new ChartMemberKeyItem { LinkshellId = linkshellId, Board = "Sky", KeyItemName = "Ra", MembershipId = bobOnly, CharacterName = "Bob" });
                var pop = new ChartPopItem { LinkshellId = linkshellId, Board = "Sky", Boss = "Kirin", ItemName = "Orb", HeldByMembershipId = aliceDup2 };
                db.ChartPopItems.Add(pop);
                var wish = new ChartWishlistRequest
                {
                    LinkshellId = linkshellId, Board = "Sky", ItemName = "Helm",
                    RequestedByMembershipId = aliceDup1, RequestedByCharacterName = "Alice",
                };
                db.ChartWishlistRequests.Add(wish);
                await db.SaveChangesAsync();
                popItemId = pop.Id;
                wishId = wish.Id;
                db.ChartPopItemCredits.Add(new ChartPopItemCredit
                {
                    ChartPopItemId = popItemId, LinkshellId = linkshellId, MembershipId = aliceDup1, CharacterName = "Alice",
                });
                await db.SaveChangesAsync();

                // Ledger watermarks: the seeded row's import watermark is the newest. The pool epochs
                // are stamped when each row was created, so the oldest row has the earliest.
                await db.Database.ExecuteSqlRawAsync(
                    "UPDATE \"AppUserLinkshells\" SET \"DkpSeedLedgerId\" = CASE \"Id\" WHEN {0} THEN 90 ELSE 0 END, " +
                    "\"DkpPoolLedgerFromId\" = CASE \"Id\" WHEN {0} THEN 4 WHEN {1} THEN 6 ELSE 2 END " +
                    "WHERE \"AppUserId\" = 'alice';",
                    aliceDup1, aliceDup2);
            }

            await using (var db = _pg.CreateContext())
            {
                await db.Database.ExecuteSqlRawAsync(OneMembershipPerLinkshell.MergeDuplicateMembershipsSql);
            }

            await using (var db = _pg.CreateContext())
            {
                var members = await db.AppUserLinkshells.AsNoTracking().OrderBy(m => m.Id).ToListAsync();

                // One Alice, the oldest row, holding the sum of the balances and seeds.
                var alice = Assert.Single(members, m => m.AppUserId == "alice");
                Assert.Equal(aliceKeep, alice.Id);
                Assert.Equal(40d, alice.LinkshellDkp);
                Assert.Equal(400d, alice.SeededDkpEarned);
                Assert.Equal(150d, alice.SeededDkpSpent);
                // Seed watermark: the latest (the import folded everything up to it into the seed).
                // Pool epoch: the earliest, when Alice first joined.
                Assert.Equal(90, alice.DkpSeedLedgerId);
                Assert.Equal(2, alice.DkpPoolLedgerFromId);

                // So the 15 HNM-pool DKP she earned (ledger row 3) after her first join but before
                // the duplicate rows were made stays in the HNM pool, and the default pool holds
                // the rest of her 40. The latest epoch (6) would have moved it to the default pool.
                const int defaultPool = 1, hnmPool = 2;
                var pools = DkpPoolBalanceService.Project(
                    alice.LinkshellDkp ?? 0d,
                    new[] { new LedgerPoolRow(3, hnmPool, 15d), new LedgerPoolRow(5, null, 25d) },
                    alice.DkpPoolLedgerFromId,
                    defaultPool,
                    new[] { defaultPool, hnmPool });
                Assert.Equal(15d, pools[hnmPool]);
                Assert.Equal(25d, pools[defaultPool]);
                // Highest rank wins; missing name / Discord id filled from the duplicates.
                Assert.Equal("Officer", alice.Rank);
                Assert.Equal("Alice", alice.CharacterName);
                Assert.Equal("111", alice.DiscordUserId);
                Assert.Equal(Now.AddDays(-30), alice.DateJoined);

                // Bob and the account-less rows are untouched.
                Assert.Equal(7d, members.Single(m => m.Id == bobOnly).LinkshellDkp);
                Assert.Equal(2, members.Count(m => m.AppUserId == null));

                // Chart rows all point at the kept row now; one tick per key item.
                var ticks = await db.ChartMemberKeyItems.AsNoTracking().Where(k => k.MembershipId != bobOnly).ToListAsync();
                Assert.Equal(2, ticks.Count);
                Assert.All(ticks, t => Assert.Equal(aliceKeep, t.MembershipId));
                Assert.Equal(new[] { "Al", "Ra" }, ticks.Select(t => t.KeyItemName).OrderBy(n => n));
                Assert.Single(await db.ChartMemberKeyItems.AsNoTracking().Where(k => k.MembershipId == bobOnly).ToListAsync());
                Assert.Equal(aliceKeep, (await db.ChartPopItems.AsNoTracking().SingleAsync(p => p.Id == popItemId)).HeldByMembershipId);
                Assert.Equal(aliceKeep, (await db.ChartPopItemCredits.AsNoTracking().SingleAsync()).MembershipId);
                Assert.Equal(aliceKeep, (await db.ChartWishlistRequests.AsNoTracking().SingleAsync(r => r.Id == wishId)).RequestedByMembershipId);

                // Running it again changes nothing (a database with no duplicates).
                await db.Database.ExecuteSqlRawAsync(OneMembershipPerLinkshell.MergeDuplicateMembershipsSql);
                var again = await db.AppUserLinkshells.AsNoTracking().OrderBy(m => m.Id).ToListAsync();
                Assert.Equal(
                    members.Select(m => (m.Id, m.LinkshellDkp, m.Rank)),
                    again.Select(m => (m.Id, m.LinkshellDkp, m.Rank)));

                // And the index the migration creates next now builds.
                await db.Database.ExecuteSqlRawAsync(CreateMembershipIndexSql);
            }
        }
        finally
        {
            await _pg.ResetAsync();
            await using var db = _pg.CreateContext();
            await db.Database.ExecuteSqlRawAsync(CreateMembershipIndexSql);
        }
    }

    [Fact]
    public async Task SecondMembershipForTheSameAccountIsRejected_AsADuplicateMembership()
    {
        var linkshellId = await SeedLinkshellAsync("alice");
        await using (var db = _pg.CreateContext())
        {
            db.AppUserLinkshells.Add(Member(linkshellId, "alice", 0));
            await db.SaveChangesAsync();
        }

        // A second join that never saw the first row.
        await using (var db = _pg.CreateContext())
        {
            db.AppUserLinkshells.Add(Member(linkshellId, "alice", 0));
            var ex = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
            Assert.True(UniqueRaceRetry.IsDuplicateMembership(ex));
            Assert.False(UniqueRaceRetry.IsDuplicateParticipation(ex));
        }

        // Still allowed: rows without an account.
        await using (var db = _pg.CreateContext())
        {
            db.AppUserLinkshells.AddRange(Member(linkshellId, null, 0), Member(linkshellId, null, 0));
            await db.SaveChangesAsync();
            Assert.Equal(3, await db.AppUserLinkshells.CountAsync());
        }
    }

    // ---------------------------------------------------------------- participations ---

    private async Task<(int LinkshellId, int EventId)> SeedEventAsync(params string[] userIds)
    {
        var linkshellId = await SeedLinkshellAsync(userIds);
        await using var db = _pg.CreateContext();
        var ev = new Event { LinkshellId = linkshellId, EventName = "Claim camp", StartTime = Now, CommencementStartTime = Now };
        db.Events.Add(ev);
        await db.SaveChangesAsync();
        return (linkshellId, ev.Id);
    }

    private static AppUserEvent Participation(int eventId, string? appUserId) => new()
    {
        EventId = eventId,
        AppUserId = appUserId,
        StartTime = Now,
        EventDkp = 0,
    };

    [Fact]
    public async Task ParticipationMergeSql_MovesWindowsAndHistory_AndLetsTheIndexBuild()
    {
        try
        {
            await using (var db = _pg.CreateContext())
            {
                await db.Database.ExecuteSqlRawAsync($"DROP INDEX IF EXISTS \"{ParticipationIndex}\";");
            }

            var (_, eventId) = await SeedEventAsync("bob", "carol");
            int keepId, dupId, carolId, w1, w2, w3;
            await using (var db = _pg.CreateContext())
            {
                // Bob has two rows. The kept (older) row has no job and no check-in; the newer one
                // has both.
                var keep = Participation(eventId, "bob");
                keep.IsVerified = true;
                var dup = Participation(eventId, "bob");
                dup.CharacterName = "Bob";
                dup.JobName = "WAR";
                dup.WdArrivalWindow = 2;
                dup.WdDepartureWindow = 4;
                dup.IsVerified = false; // not copied: the kept row's mark is what close paid on
                var carol = Participation(eventId, "carol");
                // Outside sign-ups (no account) are not merged.
                var guest1 = Participation(eventId, null);
                guest1.DiscordUserId = "555";
                var guest2 = Participation(eventId, null);
                guest2.DiscordUserId = "555";
                db.AppUserEvents.AddRange(keep, dup, carol, guest1, guest2);

                var win1 = new EventAttendanceWindow { EventId = eventId, SequenceNumber = 1, PostedAt = Now };
                var win2 = new EventAttendanceWindow { EventId = eventId, SequenceNumber = 2, PostedAt = Now };
                var win3 = new EventAttendanceWindow { EventId = eventId, SequenceNumber = 3, PostedAt = Now };
                db.EventAttendanceWindows.AddRange(win1, win2, win3);
                await db.SaveChangesAsync();
                keepId = keep.Id;
                dupId = dup.Id;
                carolId = carol.Id;
                w1 = win1.Id;
                w2 = win2.Id;
                w3 = win3.Id;

                db.AppUserEventWindows.AddRange(
                    new AppUserEventWindow { EventAttendanceWindowId = w1, AppUserEventId = keepId, AppUserId = "bob", VerifiedAt = Now },
                    new AppUserEventWindow { EventAttendanceWindowId = w2, AppUserEventId = dupId, AppUserId = "bob", VerifiedAt = Now },
                    // Window 3 credited on both rows (one is a legacy credit without an AppUserId).
                    new AppUserEventWindow { EventAttendanceWindowId = w3, AppUserEventId = keepId, AppUserId = "bob", VerifiedAt = Now },
                    new AppUserEventWindow { EventAttendanceWindowId = w3, AppUserEventId = dupId, AppUserId = null, VerifiedAt = Now },
                    new AppUserEventWindow { EventAttendanceWindowId = w1, AppUserEventId = carolId, AppUserId = "carol", VerifiedAt = Now });
                db.AppUserEventStatusLedgers.Add(new AppUserEventStatusLedger
                {
                    AppUserEventId = dupId, EventId = eventId, AppUserId = "bob", ActionType = "Verify", OccurredAt = Now,
                });
                await db.SaveChangesAsync();
            }

            await using (var db = _pg.CreateContext())
            {
                await db.Database.ExecuteSqlRawAsync(OneParticipationPerEvent.MergeDuplicateParticipationsSql);
            }

            await using (var db = _pg.CreateContext())
            {
                var rows = await db.AppUserEvents.AsNoTracking().OrderBy(p => p.Id).ToListAsync();
                var bob = Assert.Single(rows, p => p.AppUserId == "bob");
                Assert.Equal(keepId, bob.Id);
                Assert.True(bob.IsVerified);
                Assert.Equal("Bob", bob.CharacterName);
                Assert.Equal("WAR", bob.JobName);
                Assert.Equal(2, bob.WdArrivalWindow);
                Assert.Equal(4, bob.WdDepartureWindow);
                Assert.Single(rows, p => p.Id == carolId);
                Assert.Equal(2, rows.Count(p => p.AppUserId == null));

                // Bob's windows 1, 2, 3, each once, all on the kept row.
                var bobWindows = await db.AppUserEventWindows.AsNoTracking()
                    .Where(w => w.AppUserEventId == keepId).Select(w => w.EventAttendanceWindowId).OrderBy(id => id).ToListAsync();
                Assert.Equal(new[] { w1, w2, w3 }, bobWindows);
                Assert.False(await db.AppUserEventWindows.AnyAsync(w => w.AppUserEventId == dupId));
                Assert.Equal(carolId, (await db.AppUserEventWindows.AsNoTracking().SingleAsync(w => w.AppUserId == "carol")).AppUserEventId);

                // The status history moved rather than cascading away with the deleted row.
                Assert.Equal(keepId, (await db.AppUserEventStatusLedgers.AsNoTracking().SingleAsync()).AppUserEventId);

                // Idempotent, then the index builds.
                await db.Database.ExecuteSqlRawAsync(OneParticipationPerEvent.MergeDuplicateParticipationsSql);
                Assert.Equal(rows.Count, await db.AppUserEvents.CountAsync());
                await db.Database.ExecuteSqlRawAsync(CreateParticipationIndexSql);
            }
        }
        finally
        {
            await _pg.ResetAsync();
            await using var db = _pg.CreateContext();
            await db.Database.ExecuteSqlRawAsync(CreateParticipationIndexSql);
        }
    }

    [Fact]
    public async Task SecondParticipationForTheSameAccountIsRejected_AsADuplicateParticipation()
    {
        var (_, eventId) = await SeedEventAsync("bob");
        await using (var db = _pg.CreateContext())
        {
            db.AppUserEvents.Add(Participation(eventId, "bob"));
            await db.SaveChangesAsync();
        }

        await using (var db = _pg.CreateContext())
        {
            db.AppUserEvents.Add(Participation(eventId, "bob"));
            var ex = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
            Assert.True(UniqueRaceRetry.IsDuplicateParticipation(ex));
            Assert.False(UniqueRaceRetry.IsDuplicateMembership(ex));
        }

        // Still allowed: outside sign-ups without an account.
        await using (var db = _pg.CreateContext())
        {
            db.AppUserEvents.AddRange(Participation(eventId, null), Participation(eventId, null));
            await db.SaveChangesAsync();
            Assert.Equal(3, await db.AppUserEvents.CountAsync());
        }
    }

    // Lets one save wait while another request runs to completion first.
    private sealed class BeforeParticipationSave : SaveChangesInterceptor
    {
        private readonly Func<Task> _then;
        private bool _fired;

        public BeforeParticipationSave(Func<Task> then) => _then = then;

        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            var addsParticipation = eventData.Context!.ChangeTracker.Entries<AppUserEvent>()
                .Any(e => e.State == EntityState.Added);
            if (!_fired && addsParticipation)
            {
                _fired = true;
                await _then();
            }
            return result;
        }
    }

    // The sign-up paths' shape (find the member's row, add one if none, save) run as a double
    // click: click B has already read "no row" when click A's insert commits. B's insert is turned
    // away by the index, B re-runs, finds A's row and switches its job in place. One row, B's job.
    [Fact]
    public async Task DoubleClickedSignUp_EndsWithOneRow()
    {
        var (_, eventId) = await SeedEventAsync("bob");

        Task<bool> SignUp(ApplicationDbContext db, string job) =>
            UniqueRaceRetry.RunAsync(db, async () =>
            {
                var existing = await db.AppUserEvents.FirstOrDefaultAsync(p => p.EventId == eventId && p.AppUserId == "bob");
                if (existing is not null)
                {
                    existing.JobName = job;
                }
                else
                {
                    var row = Participation(eventId, "bob");
                    row.JobName = job;
                    db.AppUserEvents.Add(row);
                }
                await db.SaveChangesAsync();
                return true;
            }, UniqueRaceRetry.Participations);

        var clickA = new BeforeParticipationSave(async () =>
        {
            await using var dbA = _pg.CreateContext();
            await SignUp(dbA, "WAR");
        });
        await using (var dbB = _pg.CreateContext(clickA))
        {
            await SignUp(dbB, "PLD");
        }

        await using (var db = _pg.CreateContext())
        {
            var bob = await db.AppUserEvents.AsNoTracking().SingleAsync(p => p.AppUserId == "bob");
            Assert.Equal("PLD", bob.JobName);
        }
    }
}
