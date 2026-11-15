using System;
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

// P2.8: one membership per account per linkshell, one participation per account per event. Two
// migrations merged the duplicates production held (money kept; before the squash, so their tests are
// in git history), then unique indexes stop new ones, and the join paths turn the index's "no" into
// "already a member" or a re-run.
[Collection("postgres")]
public class OneMembershipOneParticipationTests : IAsyncLifetime
{
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
