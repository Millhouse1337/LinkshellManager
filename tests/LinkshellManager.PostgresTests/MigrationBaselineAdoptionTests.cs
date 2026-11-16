using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LinkshellManagerDiscordApp.Data;
using LinkshellManagerDiscordApp.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Npgsql;
using Xunit;

namespace LinkshellManager.PostgresTests;

// The migration squash (Round 4, D11). The 173 old migrations became one Baseline migration, and
// MigrationBaseline.AdoptAsync (run by Program.cs just before Database.Migrate) tells EF that a
// database the old chain built is already at Baseline, by adding one history row.
//
// A database built from the old chain has exactly Baseline's schema (SchemaCatalogGoldenTests), so
// "production" here is a Baseline-built database whose history is swapped for the old chain's 173
// ids, as production holds them (Snapshots/PreSquashMigrationIds.txt: ids only, no production data).
//
// Each test has its OWN database inside the test container (SchemaTestDatabase), dropped at the end.
[Collection("postgres")]
public class MigrationBaselineAdoptionTests
{
    private const string OldIdsFileName = "PreSquashMigrationIds.txt";

    // What production's old rows say (the last ones were written by EF 10.0.15).
    private const string OldProductVersion = "10.0.15";

    private readonly PostgresFixture _pg;

    public MigrationBaselineAdoptionTests(PostgresFixture pg) => _pg = pg;

    [Fact]
    public void TheOldIds_EndAtTheSquashPoint_AndAllSortBeforeBaseline()
    {
        var oldIds = ReadOldIds();
        Assert.Equal(173, oldIds.Count);
        Assert.Equal(oldIds.Count, oldIds.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(MigrationBaseline.SquashPointMigrationId, oldIds.Max(StringComparer.Ordinal));
        Assert.All(oldIds, id => Assert.True(
            string.CompareOrdinal(id, MigrationBaseline.BaselineMigrationId) < 0, $"{id} sorts after Baseline."));

        // The build starts at Baseline (later migrations, Round 5 on, come after it), and no old id
        // is reused.
        using var db = SchemaTestDatabase.CreateContext("Host=unused");
        var known = db.Database.GetMigrations().ToList();
        Assert.Equal(MigrationBaseline.BaselineMigrationId, known[0]);
        Assert.All(known, id => Assert.True(
            string.CompareOrdinal(id, MigrationBaseline.BaselineMigrationId) >= 0, $"{id} sorts before Baseline."));
        Assert.Empty(known.Intersect(oldIds));
    }

    // A database the old chain built has exactly Baseline's schema, so it is built here to Baseline
    // only; the migrations added after the squash then run on it like on any adopted database.
    [Fact]
    public async Task ProductionShapedHistory_IsAdopted_AndNothingElseChanges()
    {
        var connectionString = await SchemaTestDatabase.CreateAsync(_pg, "adopt_");
        var latestConnectionString = await SchemaTestDatabase.CreateAsync(_pg, "adopt_latest_");
        try
        {
            var oldIds = ReadOldIds();
            await SchemaTestDatabase.MigrateToAsync(connectionString, MigrationBaseline.BaselineMigrationId);
            await ReplaceHistoryAsync(connectionString, oldIds);
            await SchemaTestDatabase.MigrateToLatestAsync(latestConnectionString);
            var latest = await SchemaTestDatabase.FingerprintAsync(latestConnectionString);

            List<string> known;
            await using (var db = SchemaTestDatabase.CreateContext(connectionString))
            {
                // Without adoption EF would try to build every table again.
                known = db.Database.GetMigrations().ToList();
                Assert.Equal(known, db.Database.GetPendingMigrations());

                // What Program.cs runs at start-up, in the same order.
                Assert.Equal(MigrationBaseline.Outcome.Adopted, await MigrationBaseline.AdoptAsync(db));
                db.Database.Migrate();
                Assert.True((await AccountNameMirrorCheck.CountAsync(db, CancellationToken.None)).AllMatch);

                Assert.Empty(db.Database.GetPendingMigrations());
            }

            // Baseline did not run again: only the later migrations moved the schema, to exactly what
            // a fresh database gets. The old rows are all kept, and Baseline's row (plus one per later
            // migration) was added.
            var before = await SchemaTestDatabase.FingerprintAsync(connectionString);
            Assert.Equal(latest, before);
            var history = await ReadHistoryAsync(connectionString);
            Assert.Equal(oldIds.Count + known.Count, history.Count);
            Assert.All(oldIds, id => Assert.Equal(OldProductVersion, history[id]));
            Assert.All(known, id => Assert.Equal(ProductInfo.GetVersion(), history[id]));

            // Running it again (the next restart) changes nothing.
            await using (var db = SchemaTestDatabase.CreateContext(connectionString))
            {
                Assert.Equal(MigrationBaseline.Outcome.AlreadyAdopted, await MigrationBaseline.AdoptAsync(db));
                db.Database.Migrate();
            }
            Assert.Equal(history, await ReadHistoryAsync(connectionString));
            Assert.Equal(before, await SchemaTestDatabase.FingerprintAsync(connectionString));

            // And the app works: a save (with its hooks) and a read.
            await using (var db = SchemaTestDatabase.CreateContext(connectionString))
            {
                db.Users.Add(new AppUser { Id = "after-squash", UserName = "after-squash", CharacterName = "Aeris" });
                await db.SaveChangesAsync();
            }
            await using (var db = SchemaTestDatabase.CreateContext(connectionString))
            {
                Assert.Equal("Aeris", (await db.Characters.AsNoTracking().SingleAsync(ch => ch.AppUserId == "after-squash")).Name);
            }
        }
        finally
        {
            await SchemaTestDatabase.DropAsync(_pg, connectionString);
            await SchemaTestDatabase.DropAsync(_pg, latestConnectionString);
        }
    }

    // A database that stopped before the squash point (here: half the old chain) is refused with a
    // plain message, and nothing is written.
    [Fact]
    public async Task HistoryOlderThanTheSquashPoint_Throws_AndAddsNothing()
    {
        var connectionString = await SchemaTestDatabase.CreateAsync(_pg, "adopt_old_");
        try
        {
            var olderIds = ReadOldIds().Take(100).ToList();
            await SchemaTestDatabase.MigrateToLatestAsync(connectionString);
            await ReplaceHistoryAsync(connectionString, olderIds);

            await using (var db = SchemaTestDatabase.CreateContext(connectionString))
            {
                var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => MigrationBaseline.AdoptAsync(db));
                Assert.Contains(MigrationBaseline.SquashPointMigrationId, ex.Message);
                Assert.Contains("4b05ee2", ex.Message);
            }

            var history = await ReadHistoryAsync(connectionString);
            Assert.Equal(olderIds.OrderBy(id => id, StringComparer.Ordinal), history.Keys.OrderBy(id => id, StringComparer.Ordinal));
        }
        finally
        {
            await SchemaTestDatabase.DropAsync(_pg, connectionString);
        }
    }

    // A new, empty database: nothing to adopt, and Migrate builds it from Baseline and the migrations after it.
    [Fact]
    public async Task FreshDatabase_IsBuiltFromBaseline()
    {
        var connectionString = await SchemaTestDatabase.CreateAsync(_pg, "adopt_fresh_");
        try
        {
            await using (var db = SchemaTestDatabase.CreateContext(connectionString))
            {
                Assert.Equal(MigrationBaseline.Outcome.FreshDatabase, await MigrationBaseline.AdoptAsync(db));
                db.Database.Migrate();

                SchemaTestDatabase.AssertBuiltFromBaseline(db);
                Assert.Empty(db.Database.GetPendingMigrations());

                // The next start-up finds Baseline's own row and does nothing.
                Assert.Equal(MigrationBaseline.Outcome.AlreadyAdopted, await MigrationBaseline.AdoptAsync(db));
            }
        }
        finally
        {
            await SchemaTestDatabase.DropAsync(_pg, connectionString);
        }
    }

    // Two instances starting at the same moment on a production-shaped history: Baseline's row is
    // added once, and only one of them reports (and so logs) that it added it.
    [Fact]
    public async Task TwoStartsAtOnce_AddTheRowOnce()
    {
        var connectionString = await SchemaTestDatabase.CreateAsync(_pg, "adopt_race_");
        try
        {
            var oldIds = ReadOldIds();
            await SchemaTestDatabase.MigrateToLatestAsync(connectionString);
            await ReplaceHistoryAsync(connectionString, oldIds);

            var outcomes = await Task.WhenAll(Enumerable.Range(0, 4).Select(async _ =>
            {
                await using var db = SchemaTestDatabase.CreateContext(connectionString);
                return await MigrationBaseline.AdoptAsync(db);
            }));

            Assert.Single(outcomes, o => o == MigrationBaseline.Outcome.Adopted);
            Assert.All(outcomes.Where(o => o != MigrationBaseline.Outcome.Adopted),
                o => Assert.Equal(MigrationBaseline.Outcome.AlreadyAdopted, o));
            var history = await ReadHistoryAsync(connectionString);
            Assert.Equal(oldIds.Count + 1, history.Count);
            Assert.True(history.ContainsKey(MigrationBaseline.BaselineMigrationId));
        }
        finally
        {
            await SchemaTestDatabase.DropAsync(_pg, connectionString);
        }
    }

    // No database at all yet: adoption must not fail by opening it, because Migrate creates it.
    [Fact]
    public async Task MissingDatabase_IsFresh_AndMigrateCreatesIt()
    {
        var connectionString = new NpgsqlConnectionStringBuilder(_pg.ConnectionString)
        {
            Database = "adopt_missing_" + Guid.NewGuid().ToString("N")[..8],
            Pooling = false,
        }.ConnectionString;
        try
        {
            await using var db = SchemaTestDatabase.CreateContext(connectionString);
            Assert.Equal(MigrationBaseline.Outcome.FreshDatabase, await MigrationBaseline.AdoptAsync(db));
            db.Database.Migrate();
            SchemaTestDatabase.AssertBuiltFromBaseline(db);
        }
        finally
        {
            await SchemaTestDatabase.DropAsync(_pg, connectionString);
        }
    }

    // A history table with no rows is treated like a fresh database, not refused.
    [Fact]
    public async Task EmptyHistoryTable_IsNotRefused()
    {
        var connectionString = await SchemaTestDatabase.CreateAsync(_pg, "adopt_empty_");
        try
        {
            await SchemaTestDatabase.ExecuteAsync(connectionString,
                "CREATE TABLE \"__EFMigrationsHistory\" (\"MigrationId\" character varying(150) NOT NULL " +
                "CONSTRAINT \"PK___EFMigrationsHistory\" PRIMARY KEY, \"ProductVersion\" character varying(32) NOT NULL)");

            await using var db = SchemaTestDatabase.CreateContext(connectionString);
            Assert.Equal(MigrationBaseline.Outcome.EmptyHistory, await MigrationBaseline.AdoptAsync(db));
            db.Database.Migrate();
            SchemaTestDatabase.AssertBuiltFromBaseline(db);
        }
        finally
        {
            await SchemaTestDatabase.DropAsync(_pg, connectionString);
        }
    }

    // The history production has before the squash build first starts: the old ids only.
    private static async Task ReplaceHistoryAsync(string connectionString, IReadOnlyList<string> ids)
    {
        await using var c = new NpgsqlConnection(connectionString);
        await c.OpenAsync();
        await using var transaction = await c.BeginTransactionAsync();
        await using (var delete = new NpgsqlCommand("DELETE FROM \"__EFMigrationsHistory\"", c, transaction))
        {
            await delete.ExecuteNonQueryAsync();
        }
        await using (var insert = new NpgsqlCommand(
            "INSERT INTO \"__EFMigrationsHistory\" (\"MigrationId\", \"ProductVersion\") SELECT unnest(@ids), @version",
            c, transaction))
        {
            insert.Parameters.AddWithValue("ids", ids.ToArray());
            insert.Parameters.AddWithValue("version", OldProductVersion);
            await insert.ExecuteNonQueryAsync();
        }
        await transaction.CommitAsync();
    }

    private static async Task<Dictionary<string, string>> ReadHistoryAsync(string connectionString)
    {
        var rows = new Dictionary<string, string>(StringComparer.Ordinal);
        await using var c = new NpgsqlConnection(connectionString);
        await c.OpenAsync();
        await using var command = new NpgsqlCommand("SELECT \"MigrationId\", \"ProductVersion\" FROM \"__EFMigrationsHistory\"", c);
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            rows.Add(reader.GetString(0), reader.GetString(1));
        }
        return rows;
    }

    private static List<string> ReadOldIds()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var path = Path.Combine(directory.FullName, "tests", "LinkshellManager.PostgresTests", "Snapshots", OldIdsFileName);
            if (File.Exists(path))
            {
                return File.ReadAllLines(path).Select(line => line.Trim()).Where(line => line.Length > 0).ToList();
            }
            directory = directory.Parent;
        }
        throw new InvalidOperationException($"Could not find Snapshots/{OldIdsFileName} above {AppContext.BaseDirectory}");
    }
}
