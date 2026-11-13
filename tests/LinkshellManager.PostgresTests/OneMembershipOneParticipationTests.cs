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
    private const string ParticipationIndex = "IX_AppUserEvents_EventId_AppUserId";

    // What the migrations' CreateIndex produces, so a test that drops an index can put it back.
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

    // No test runs OneMembershipPerLinkshell.MergeDuplicateMembershipsSql any more. That SQL also
    // moves the chart columns ChartPopItems.HeldByMembershipId, ChartPopItemCredits.MembershipId and
    // ChartWishlistRequests.RequestedByMembershipId, which DropDeadAccountAuctionChartColumns removed,
    // so it can only run on the schema of its own day. It already ran on production; on a fresh
    // database it still runs in migration order (before the drop) on empty tables.

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
                    AppUserEventId = dupId, EventId = eventId, ActionType = "BreakStart", OccurredAt = Now,
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
