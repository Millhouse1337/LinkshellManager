using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LinkshellManagerDiscordApp.Data;
using LinkshellManagerDiscordApp.Models;
using LinkshellManagerDiscordApp.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Xunit;

namespace LinkshellManager.PostgresTests;

// R2.1 (dead-dkp-03) on the real database: Past Event DKP edits append "Correction" rows and never
// rewrite the rows the event paid. Postgres gives every row a new xmin when it is UPDATEd, so an
// unchanged xmin on the payout rows is proof nothing touched them. Also runs the reads that now
// fold corrections (Past Events, the DKP sheet's lifetime totals, the pool type catalog) on Npgsql.
[Collection("postgres")]
public class PastEventCorrectionTests : IAsyncLifetime
{
    private static readonly DateTime Now = new(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DkpCorrectionNote Note = new("Boss", "late");

    private readonly PostgresFixture _pg;

    public PastEventCorrectionTests(PostgresFixture pg) => _pg = pg;

    public Task InitializeAsync() => _pg.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    private static EventHistoryEditService Edits(ApplicationDbContext db)
    {
        var pools = new DkpPoolResolver(db, new DkpPoolProvisioner(db));
        return new EventHistoryEditService(db, new DkpLedgerWriter(db, pools, NullLogger<DkpLedgerWriter>.Instance), pools);
    }

    private sealed record Seeded(int LinkshellId, int HistoryId, int BobRow, int AliceRow, int BobEarnedId, int AliceEarnedId);

    // Bob and Alice each earned 10 at a Sky event.
    private async Task<Seeded> SeedAsync()
    {
        await using var db = _pg.CreateContext();
        var linkshell = new Linkshell { LinkshellName = "LS" };
        db.Linkshells.Add(linkshell);
        await db.SaveChangesAsync();
        db.DkpPools.Add(DkpPoolDefaults.BuildDefaultPool(linkshell.Id));
        db.Users.AddRange(
            new AppUser { Id = "bob", UserName = "bob", CharacterName = "Bob" },
            new AppUser { Id = "alice", UserName = "alice", CharacterName = "Alice" });
        db.AppUserLinkshells.AddRange(
            new AppUserLinkshell { LinkshellId = linkshell.Id, AppUserId = "bob", LinkshellDkp = 0 },
            new AppUserLinkshell { LinkshellId = linkshell.Id, AppUserId = "alice", LinkshellDkp = 0 });
        var history = new EventHistory
        {
            LinkshellId = linkshell.Id, EventName = "Sky", EventType = "Sky", DkpPerHour = 10, Duration = 1, EndTime = Now,
            AppUserEventHistories =
            {
                new AppUserEventHistory { AppUserId = "bob", CharacterName = "Bob", EventDkp = 10, Duration = 1 },
                new AppUserEventHistory { AppUserId = "alice", CharacterName = "Alice", EventDkp = 10, Duration = 1 },
            },
        };
        db.EventHistories.Add(history);
        await db.SaveChangesAsync();

        var pools = new DkpPoolResolver(db, new DkpPoolProvisioner(db));
        var writer = new DkpLedgerWriter(db, pools, NullLogger<DkpLedgerWriter>.Instance);
        var earned = new Dictionary<string, DkpLedgerEntry>();
        foreach (var member in await db.AppUserLinkshells.ToListAsync())
        {
            earned[member.AppUserId!] = (await writer.AppendAsync(member, "EventEarned", 10, Now, DkpPoolRef.Derived("Sky"),
                new DkpEntryContext(EventName: "Sky", EventType: "Sky", EventHistory: history), CancellationToken.None))!;
        }
        await db.SaveChangesAsync();

        var rows = history.AppUserEventHistories.ToDictionary(p => p.AppUserId!, p => p.Id);
        return new Seeded(linkshell.Id, history.Id, rows["bob"], rows["alice"], earned["bob"].Id, earned["alice"].Id);
    }

    private async Task<long> XminAsync(int ledgerId)
    {
        await using var connection = new NpgsqlConnection(_pg.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT xmin::text::bigint FROM \"DkpLedgerEntries\" WHERE \"Id\" = @id";
        command.Parameters.AddWithValue("id", ledgerId);
        return (long)(await command.ExecuteScalarAsync())!;
    }

    [Fact]
    public async Task EveryEditPath_AppendsCorrections_AndNeverRewritesThePayout()
    {
        var seed = await SeedAsync();
        var bobXmin = await XminAsync(seed.BobEarnedId);
        var aliceXmin = await XminAsync(seed.AliceEarnedId);

        // Each edit in its own context, as each request has its own.
        async Task RunAsync(Func<EventHistoryEditService, Task<EventHistoryEditOutcome>> edit)
        {
            await using var db = _pg.CreateContext();
            Assert.Equal(EventHistoryEditOutcome.Done, await edit(Edits(db)));
        }

        await RunAsync(e => e.SetParticipantDkpAsync(seed.HistoryId, seed.BobRow, 6, Note, CancellationToken.None));
        await RunAsync(e => e.SetParticipantDkpAsync(seed.HistoryId, seed.BobRow, 9, Note, CancellationToken.None));
        // Rate 10 -> 20 doubles what each has: Bob 9 -> 18, Alice 10 -> 20.
        await RunAsync(e => e.EditEventAsync(seed.HistoryId,
            new EventHistoryEditInput("Sky", "Sky", null, null, 20, Note), CancellationToken.None));
        await RunAsync(e => e.RemoveParticipantAsync(seed.HistoryId, seed.AliceRow, Note, CancellationToken.None));

        await using var check = _pg.CreateContext();
        Assert.Equal(bobXmin, await XminAsync(seed.BobEarnedId));
        Assert.Equal(aliceXmin, await XminAsync(seed.AliceEarnedId));

        var rows = await check.DkpLedgerEntries.AsNoTracking().OrderBy(e => e.Id).ToListAsync();
        Assert.Equal(new[] { 10d, -4d, 3d, 9d }, rows.Where(e => e.AppUserId == "bob").Select(e => e.Amount));
        Assert.Equal(new[] { 10d, 10d, -20d }, rows.Where(e => e.AppUserId == "alice").Select(e => e.Amount));
        var corrections = rows.Where(e => e.EntryType == DkpCorrections.EntryType).ToList();
        Assert.Equal(5, corrections.Count);
        Assert.All(corrections, c =>
        {
            Assert.Equal(c.AppUserId == "bob" ? seed.BobEarnedId : seed.AliceEarnedId, c.AuditRelatedLedgerEntryId);
            Assert.Equal(seed.HistoryId, c.EventHistoryId);
            Assert.Equal("late", c.EditReason);
            Assert.StartsWith("Correction by Boss: late.", c.Details);
        });

        var balances = await check.AppUserLinkshells.AsNoTracking().ToDictionaryAsync(m => m.AppUserId!, m => m.LinkshellDkp);
        Assert.Equal(18, balances["bob"]);
        Assert.Equal(0, balances["alice"]);

        // What the officer sees: Past Events, and the DKP sheet's lifetime totals.
        var bobAttendee = await check.AppUserEventHistories.AsNoTracking().SingleAsync(p => p.Id == seed.BobRow);
        Assert.Equal(18, (await PastEventDkp.LoadAsync(check, new[] { seed.HistoryId }, CancellationToken.None)).For(bobAttendee));
        Assert.False(await check.AppUserEventHistories.AnyAsync(p => p.Id == seed.AliceRow));

        var pools = new DkpPoolResolver(check, new DkpPoolProvisioner(check));
        var sheet = await new DkpSheetService(check, pools).BuildAsync(seed.LinkshellId, CancellationToken.None);
        var bob = sheet.Members.Single(m => m.Name == "Bob");
        var alice = sheet.Members.Single(m => m.Name == "Alice");
        Assert.Equal((18d, 0d), (bob.Total, bob.Spent));
        Assert.Equal((0d, 0d), (alice.Total, alice.Spent));

        // The pool catalog's earned-per-type sum counts the corrections too.
        var options = await new DkpPoolEventTypeCatalog(check).LoadAsync(seed.LinkshellId, CancellationToken.None);
        Assert.Equal(18, options.Single(o => o.Key == "Sky").EarnedTotal);
    }

    // Deleting the whole event takes everything back, corrections included.
    [Fact]
    public async Task DeletingTheEvent_TakesTheCorrectionsWithIt()
    {
        var seed = await SeedAsync();
        await using (var db = _pg.CreateContext())
        {
            await Edits(db).SetParticipantDkpAsync(seed.HistoryId, seed.BobRow, 4, Note, CancellationToken.None);
        }
        await using (var db = _pg.CreateContext())
        {
            Assert.True(await Edits(db).DeleteEventAsync(seed.HistoryId, CancellationToken.None));
        }

        await using var check = _pg.CreateContext();
        Assert.Empty(await check.DkpLedgerEntries.ToListAsync());
        Assert.All(await check.AppUserLinkshells.ToListAsync(), m => Assert.Equal(0, m.LinkshellDkp));
    }
}
