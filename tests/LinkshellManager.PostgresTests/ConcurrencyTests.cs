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

// P2.7: AppUserLinkshell and Event carry Postgres's xmin as a concurrency token (Version). Officer A
// pays Bob +5 while officer B charges him 3 for loot; both read Bob at 100. Before, one save
// overwrote the other (Bob ended at 105 or 97, the ledger said 102). Now B's stale save is turned
// away and ConcurrencyRetry re-runs it on top of A's: Bob ends at 102 and agrees with his ledger.
[Collection("postgres")]
public class ConcurrencyTests : IAsyncLifetime
{
    private static readonly DateTime Now = new(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc);

    private readonly PostgresFixture _pg;

    public ConcurrencyTests(PostgresFixture pg) => _pg = pg;

    public Task InitializeAsync() => _pg.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    private static DkpLedgerWriter NewWriter(ApplicationDbContext db) =>
        new(db, new DkpPoolResolver(db, new DkpPoolProvisioner(db)), NullLogger<DkpLedgerWriter>.Instance);

    private async Task<(int LinkshellId, int EventId)> SeedAsync()
    {
        await using var db = _pg.CreateContext();
        var linkshell = new Linkshell { LinkshellName = "LS" };
        db.Linkshells.Add(linkshell);
        await db.SaveChangesAsync();
        // Provisioned up front, so the actions below make exactly one save each.
        db.DkpPools.Add(DkpPoolDefaults.BuildDefaultPool(linkshell.Id));
        db.Users.Add(new AppUser { Id = "bob", UserName = "bob", CharacterName = "Bob" });
        db.AppUserLinkshells.Add(new AppUserLinkshell
        {
            LinkshellId = linkshell.Id, AppUserId = "bob", CharacterName = "Bob", LinkshellDkp = 100,
        });
        var ev = new Event { LinkshellId = linkshell.Id, EventName = "Fafnir", EventType = "HNM" };
        db.Events.Add(ev);
        await db.SaveChangesAsync();
        return (linkshell.Id, ev.Id);
    }

    // One DKP move on Bob, start to finish: load him, append through the ledger writer, save.
    private static async Task MoveBobAsync(ApplicationDbContext db, double amount)
    {
        await StageBobMoveAsync(db, amount);
        await db.SaveChangesAsync();
    }

    // Loads Bob and stages the move (balance + ledger row) without saving.
    private static async Task StageBobMoveAsync(ApplicationDbContext db, double amount)
    {
        var bob = await db.AppUserLinkshells.SingleAsync(m => m.AppUserId == "bob");
        await NewWriter(db).AppendAsync(
            bob, amount > 0 ? "AuditMisc" : "LootSpent", amount, Now,
            DkpPoolRef.Derived(null), new DkpEntryContext(), CancellationToken.None, DkpOverdraft.Allow);
    }

    [Fact]
    public async Task Xmin_IsPostgresOwnColumn_NotOneTheMigrationAdded()
    {
        await SeedAsync();

        await using var db = _pg.CreateContext();
        await using (var connection = new NpgsqlConnection(_pg.ConnectionString))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            // information_schema lists user columns only; xmin is a system column every row has.
            command.CommandText =
                "SELECT COUNT(*) FROM information_schema.columns " +
                "WHERE table_name IN ('Events', 'AppUserLinkshells') AND column_name IN ('xmin', 'Version')";
            Assert.Equal(0L, (long)(await command.ExecuteScalarAsync())!);
        }

        Assert.NotEqual(0u, (await db.AppUserLinkshells.SingleAsync()).Version);
        Assert.NotEqual(0u, (await db.Events.SingleAsync()).Version);
    }

    [Fact]
    public async Task StaleBalanceSave_IsTurnedAway_AndChangesNothing()
    {
        await SeedAsync();

        await using var dbA = _pg.CreateContext();
        await using var dbB = _pg.CreateContext();
        await StageBobMoveAsync(dbA, +5);
        await StageBobMoveAsync(dbB, -3);

        await dbA.SaveChangesAsync();
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => dbB.SaveChangesAsync());

        // B's whole save was turned away: neither its balance nor its ledger row landed.
        await using var check = _pg.CreateContext();
        Assert.Equal(105, (await check.AppUserLinkshells.SingleAsync()).LinkshellDkp);
        Assert.Equal(5, (await check.DkpLedgerEntries.SingleAsync()).Amount);
    }

    // The finding's own example, made to happen in the worst order: officer B has read Bob (100)
    // when officer A's +5 commits, and only then does B save its -3.
    [Fact]
    public async Task OverlappingCreditAndCharge_BothLand()
    {
        await SeedAsync();

        var officerACredits = new BeforeFirstSave(async () =>
        {
            await using var dbA = _pg.CreateContext();
            await MoveBobAsync(dbA, +5);
        });

        var attemptsByB = 0;
        await using (var dbB = _pg.CreateContext(officerACredits))
        {
            await ConcurrencyRetry.RunAsync(dbB, async () =>
            {
                attemptsByB++;
                await MoveBobAsync(dbB, -3);
                return true;
            });
        }

        Assert.Equal(2, attemptsByB);
        await using var check = _pg.CreateContext();
        var bob = await check.AppUserLinkshells.SingleAsync();
        var ledger = await check.DkpLedgerEntries.Select(e => e.Amount).ToListAsync();
        Assert.Equal(102, bob.LinkshellDkp);
        Assert.Equal(2, ledger.Count);
        Assert.Equal(bob.LinkshellDkp - 100, ledger.Sum());
        // The re-run read the sequence afresh: no two rows share one.
        Assert.Equal(2, await check.DkpLedgerEntries.Select(e => e.Sequence).Distinct().CountAsync());
    }

    // The same race through a real entry point: an officer corrects Bob's DKP on a Past Event
    // (10 -> 20) while another officer's +5 commits in between. The edit re-runs inside the service
    // (EventHistoryEditService wraps it in ConcurrencyRetry) and both changes land.
    [Fact]
    public async Task PastEventEdit_RacingACredit_BothLand()
    {
        var (linkshellId, _) = await SeedAsync();
        int historyId, participantId;
        await using (var db = _pg.CreateContext())
        {
            var history = new EventHistory
            {
                LinkshellId = linkshellId,
                EventName = "Sky",
                EventType = "Sky",
                AppUserEventHistories = { new AppUserEventHistory { AppUserId = "bob", CharacterName = "Bob", EventDkp = 10 } },
            };
            db.EventHistories.Add(history);
            var bob = await db.AppUserLinkshells.SingleAsync();
            await NewWriter(db).AppendAsync(
                bob, "EventEarned", 10, Now, DkpPoolRef.Derived("Sky"),
                new DkpEntryContext(EventHistory: history), CancellationToken.None);
            await db.SaveChangesAsync();
            historyId = history.Id;
            participantId = history.AppUserEventHistories.Single().Id;
        }

        var officerACredits = new BeforeFirstSave(async () =>
        {
            await using var dbA = _pg.CreateContext();
            await MoveBobAsync(dbA, +5);
        });
        await using (var dbB = _pg.CreateContext(officerACredits))
        {
            var pools = new DkpPoolResolver(dbB, new DkpPoolProvisioner(dbB));
            var edits = new EventHistoryEditService(
                dbB, new DkpLedgerWriter(dbB, pools, NullLogger<DkpLedgerWriter>.Instance), pools);
            Assert.Equal(
                EventHistoryEditOutcome.Done,
                await edits.SetParticipantDkpAsync(historyId, participantId, 20, CancellationToken.None));
        }

        await using var check = _pg.CreateContext();
        var saved = await check.AppUserLinkshells.SingleAsync();
        Assert.Equal(125, saved.LinkshellDkp);
        Assert.Equal(25, (await check.DkpLedgerEntries.Select(e => e.Amount).ToListAsync()).Sum());
        Assert.Equal(20, (await check.DkpLedgerEntries.SingleAsync(e => e.EntryType == "EventEarned")).Amount);
    }

    // Two officers end/hand off the same event at the same moment: the later save is turned away,
    // so the event can only be acted on from the state the earlier one left.
    [Fact]
    public async Task StaleEventSave_IsTurnedAway()
    {
        var (_, eventId) = await SeedAsync();

        await using var dbA = _pg.CreateContext();
        await using var dbB = _pg.CreateContext();
        var evA = await dbA.Events.SingleAsync(e => e.Id == eventId);
        var evB = await dbB.Events.SingleAsync(e => e.Id == eventId);

        evA.WdFinalizedAt = Now;
        await dbA.SaveChangesAsync();

        evB.HnmWindowNumber = 3;
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => dbB.SaveChangesAsync());
    }

    // The Discord board publisher saves the message id it just posted. An officer renamed the event
    // while the post was in flight: both changes survive, and the message id is not lost (which
    // would make the next refresh post a second board).
    [Fact]
    public async Task PublisherSave_KeepsTheOfficersEdit_AndItsOwnMessageId()
    {
        var (_, eventId) = await SeedAsync();

        await using var publisher = _pg.CreateContext();
        var ev = await publisher.Events.SingleAsync(e => e.Id == eventId);

        await using (var officer = _pg.CreateContext())
        {
            var mine = await officer.Events.SingleAsync(e => e.Id == eventId);
            mine.EventName = "Fafnir (Kings)";
            await officer.SaveChangesAsync();
        }

        ev.DiscordChannelId = "123";
        ev.DiscordMessageId = "456";
        await ConcurrencyRetry.SaveOwnEventColumnsAsync(publisher, CancellationToken.None);

        await using var check = _pg.CreateContext();
        var saved = await check.Events.SingleAsync(e => e.Id == eventId);
        Assert.Equal("Fafnir (Kings)", saved.EventName);
        Assert.Equal("123", saved.DiscordChannelId);
        Assert.Equal("456", saved.DiscordMessageId);
    }

    // Runs `then` once, just before this context's first save -- after it has read, before it writes.
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
