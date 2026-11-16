using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LinkshellManagerDiscordApp.Data;
using LinkshellManagerDiscordApp.Models;
using LinkshellManagerDiscordApp.Services;
using Microsoft.Extensions.Logging;
using Npgsql;
using Xunit;

namespace LinkshellManager.PostgresTests;

// Round 5, update A: the start-up SpawnKey refill (Program.cs runs it after every Migrate).
//
// Rows saved by an older build while the app was rolled back have no key or a stale one, and the
// migrations that fill keys have already run, so only this repairs them. It must fix every one of
// the seven tables, write nothing on a healthy database, log only tables it changed, and keep going
// when one table fails.
[Collection("postgres")]
public class SpawnKeyRefillTests : IAsyncLifetime
{
    private static readonly DateTime At = new(2026, 11, 15, 12, 0, 0, DateTimeKind.Utc);

    private readonly PostgresFixture _pg;

    public SpawnKeyRefillTests(PostgresFixture pg) => _pg = pg;

    public Task InitializeAsync() => _pg.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public void EveryTableWithASpawnKey_IsRefilled()
    {
        Assert.Equal(
            new[]
            {
                "Events", "PartySetups", "ClaimShieldCaptures", "LinkshellMonsterTimings",
                "Tods", "PendingTodSubmissions", "HnmRecurringBoards",
            },
            SpawnKeyRefill.Tables.Select(t => t.Table));
    }

    [Fact]
    public async Task RowsAnOlderBuildSaved_AreReKeyed_AndAHealthyDatabaseIsLeftAlone()
    {
        await SeedAsync();

        // Healthy: the hook stamped every row, so nothing is written or logged.
        var quiet = new MigrationNoticeLoggingTests.CapturingLogger();
        await using (var db = _pg.CreateContext())
        {
            Assert.Equal(0, await SpawnKeyRefill.RunAsync(db, quiet, CancellationToken.None));
        }
        Assert.Empty(quiet.Entries);

        // What a rolled-back build leaves behind: no key on new rows, the old key after a rename.
        await using (var c = await OpenAsync())
        {
            await ExecuteAsync(c, "UPDATE \"Events\" SET \"SpawnKey\" = NULL WHERE \"AssignedMonsterName\" = 'Nidhogg'");
            await ExecuteAsync(c, "UPDATE \"Events\" SET \"AssignedMonsterName\" = 'Vrtra' WHERE \"AssignedMonsterName\" = 'Tiamat'");
            await ExecuteAsync(c, "UPDATE \"PartySetups\" SET \"SpawnKey\" = NULL");
            await ExecuteAsync(c, "UPDATE \"ClaimShieldCaptures\" SET \"SpawnKey\" = 'tiamat' WHERE \"MonsterName\" = 'King Behemoth'");
            await ExecuteAsync(c, "UPDATE \"LinkshellMonsterTimings\" SET \"SpawnKey\" = NULL WHERE \"MonsterName\" = ' Fafnir/Nidhogg '");
            await ExecuteAsync(c, "UPDATE \"Tods\" SET \"SpawnKey\" = NULL");
            await ExecuteAsync(c, "UPDATE \"PendingTodSubmissions\" SET \"SpawnKey\" = 'fafnir'");
            await ExecuteAsync(c, "UPDATE \"HnmRecurringBoards\" SET \"SpawnKey\" = 'aspidochelone'");
        }

        var log = new MigrationNoticeLoggingTests.CapturingLogger();
        await using (var db = _pg.CreateContext())
        {
            // Events 2, setups 4 (the blank-named ones have no key to give), captures 1, timings 1,
            // ToDs 2, pending 1, boards 1.
            Assert.Equal(12, await SpawnKeyRefill.RunAsync(db, log, CancellationToken.None));
        }
        Assert.All(log.Entries, e => Assert.Equal(LogLevel.Information, e.Level));
        Assert.Equal(
            new[]
            {
                "SpawnKey refill: re-keyed 2 row(s) in Events (saved by an older build).",
                "SpawnKey refill: re-keyed 4 row(s) in PartySetups (saved by an older build).",
                "SpawnKey refill: re-keyed 1 row(s) in ClaimShieldCaptures (saved by an older build).",
                "SpawnKey refill: re-keyed 1 row(s) in LinkshellMonsterTimings (saved by an older build).",
                "SpawnKey refill: re-keyed 2 row(s) in Tods (saved by an older build).",
                "SpawnKey refill: re-keyed 1 row(s) in PendingTodSubmissions (saved by an older build).",
                "SpawnKey refill: re-keyed 1 row(s) in HnmRecurringBoards (saved by an older build).",
            },
            log.Entries.Select(e => e.Message));
        await AssertEveryKeyMatchesItsNameAsync();

        // A second start-up has nothing left to do.
        var again = new MigrationNoticeLoggingTests.CapturingLogger();
        await using (var db = _pg.CreateContext())
        {
            Assert.Equal(0, await SpawnKeyRefill.RunAsync(db, again, CancellationToken.None));
        }
        Assert.Empty(again.Entries);
    }

    // A table the unique index refuses (an older build renamed a Monster Setups row onto a spawn the
    // linkshell already has) is skipped with a Warning; the other tables are still repaired.
    [Fact]
    public async Task ATableThatFails_IsLoggedAndSkipped_TheOthersStillRun()
    {
        await SeedAsync();
        await using (var c = await OpenAsync())
        {
            await ExecuteAsync(c,
                "UPDATE \"LinkshellMonsterTimings\" SET \"MonsterName\" = 'Nidhogg', \"NormalizedMonsterName\" = 'nidhogg' " +
                "WHERE \"MonsterName\" = 'Tiamat'");
            await ExecuteAsync(c, "UPDATE \"Events\" SET \"SpawnKey\" = NULL WHERE \"AssignedMonsterName\" = 'Nidhogg'");
            await ExecuteAsync(c, "UPDATE \"Tods\" SET \"SpawnKey\" = NULL");
        }

        var log = new MigrationNoticeLoggingTests.CapturingLogger();
        await using (var db = _pg.CreateContext())
        {
            Assert.Equal(3, await SpawnKeyRefill.RunAsync(db, log, CancellationToken.None));
        }
        var warning = Assert.Single(log.Entries, e => e.Level == LogLevel.Warning);
        Assert.Equal("SpawnKey refill: LinkshellMonsterTimings could not be re-keyed; start-up continues.", warning.Message);
        Assert.Contains("SpawnKey refill: re-keyed 1 row(s) in Events (saved by an older build).", log.Entries.Select(e => e.Message));
        Assert.Contains("SpawnKey refill: re-keyed 2 row(s) in Tods (saved by an older build).", log.Entries.Select(e => e.Message));

        await using (var c = await OpenAsync())
        {
            // The timing row keeps its old key; the rest is right.
            Assert.Equal("tiamat", await ScalarAsync(c,
                "SELECT \"SpawnKey\" FROM \"LinkshellMonsterTimings\" WHERE \"MonsterName\" = 'Nidhogg'"));
            Assert.Equal(0L, await ScalarAsync(c,
                "SELECT count(*) FROM \"Events\" e WHERE e.\"SpawnKey\" IS DISTINCT FROM " +
                TodDerivedSql.SpawnKeySql("e.\"AssignedMonsterName\"")));
            Assert.Equal(0L, await ScalarAsync(c,
                "SELECT count(*) FROM \"Tods\" t WHERE t.\"SpawnKey\" IS DISTINCT FROM " +
                TodDerivedSql.SpawnKeySql("t.\"MonsterName\"")));
        }
    }

    // Through EF, so the hook stamps every key.
    private async Task SeedAsync()
    {
        await using var db = _pg.CreateContext();
        var linkshell = new Linkshell { LinkshellName = "LS" };
        db.Linkshells.Add(linkshell);
        await db.SaveChangesAsync();
        var id = linkshell.Id;

        var names = new[] { "Nidhogg", " Fafnir/Nidhogg ", "King Behemoth", "Tiamat", "", null };
        foreach (var name in names)
        {
            db.Events.Add(new Event { LinkshellId = id, EventName = "Camp", AssignedMonsterName = name });
            db.PartySetups.Add(new PartySetup { LinkshellId = id, Name = "Setup", AssignedMonsterName = name, CreatedAt = At, UpdatedAt = At });
            db.ClaimShieldCaptures.Add(new ClaimShieldCapture
            {
                LinkshellId = id, MonsterName = name ?? string.Empty, CapturedAtUtc = At, CreatedAtUtc = At,
            });
        }
        var sort = 0;
        foreach (var name in new[] { " Fafnir/Nidhogg ", "King Behemoth", "Tiamat" })
        {
            db.LinkshellMonsterTimings.Add(new LinkshellMonsterTiming
            {
                LinkshellId = id, MonsterName = name, NormalizedMonsterName = MonsterTimingDefaults.Normalize(name),
                CooldownMinutes = 1320, CreatedAtUtc = At, UpdatedAtUtc = At, SortOrder = sort++,
            });
        }
        db.Tods.Add(new Tod { LinkshellId = id, MonsterName = "Nidhogg", Time = At });
        db.Tods.Add(new Tod { LinkshellId = id, MonsterName = "Cerberus", Time = At });
        db.PendingTodSubmissions.Add(new PendingTodSubmission { LinkshellId = id, SubmittedAtUtc = At, MonsterName = "King Behemoth" });
        db.HnmRecurringBoards.Add(new HnmRecurringBoard
        {
            LinkshellId = id, MonsterName = "Adamantoise/Aspidochelone", CreatedAt = At, UpdatedAt = At,
        });
        await db.SaveChangesAsync();
    }

    private static readonly (string Table, string NameColumn)[] Checks =
        SpawnKeyRefill.Tables.Select(t => (t.Table, t.NameColumn)).ToArray();

    private async Task AssertEveryKeyMatchesItsNameAsync()
    {
        await using var c = await OpenAsync();
        foreach (var (table, nameColumn) in Checks)
        {
            Assert.True((long)(await ScalarAsync(c, $"SELECT count(*) FROM \"{table}\""))! > 0, $"{table} has no rows");
            var rows = new List<(string? Name, string? Key)>();
            await using (var command = c.CreateCommand())
            {
                command.CommandText = $"SELECT \"{nameColumn}\", \"SpawnKey\" FROM \"{table}\"";
                await using var reader = await command.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    rows.Add((reader.IsDBNull(0) ? null : reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1)));
                }
            }
            foreach (var (name, key) in rows)
            {
                Assert.True(key == HnmConfig.SpawnKey(name), $"{table}: \"{name}\" keyed \"{key}\"");
            }
        }
    }

    private async Task<NpgsqlConnection> OpenAsync()
    {
        var connection = new NpgsqlConnection(_pg.ConnectionString);
        await connection.OpenAsync();
        return connection;
    }

    private static async Task ExecuteAsync(NpgsqlConnection c, string sql)
    {
        await using var command = c.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<object?> ScalarAsync(NpgsqlConnection c, string sql)
    {
        await using var command = c.CreateCommand();
        command.CommandText = sql;
        return await command.ExecuteScalarAsync();
    }
}
