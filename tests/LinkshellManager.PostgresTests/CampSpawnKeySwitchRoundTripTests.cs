using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LinkshellManagerDiscordApp.Data;
using LinkshellManagerDiscordApp.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Logging;
using Npgsql;
using NpgsqlTypes;
using Xunit;

namespace LinkshellManager.PostgresTests;

// Round 5, R5.3: CampSpawnKeySwitch re-fills SpawnKey on the four camp tables and makes
// IX_LinkshellMonsterTimings_LinkshellId_SpawnKey unique (guarded).
//
// - Its copied SpawnKey SQL gives the same key as HnmConfig.SpawnKey for every input.
// - Up, Down and Up again from MembershipRoleRequired on rows an older build wrote (NULL or stale
//   keys): every key is repaired, the index becomes unique (and plain again on Down), no row is
//   lost, and the NOTICE says what was written. A second Monster Setups row for one spawn is then
//   refused.
// - Two rows of one linkshell naming one spawn: the index stays plain, a WARNING reaches the log,
//   and MigrationHealth reports it until the rows are fixed and Up runs again.
//
// Its OWN database inside the test container, dropped at the end.
[Collection("postgres")]
public class CampSpawnKeySwitchRoundTripTests
{
    // Named, not "latest", so a later migration's Down never joins this test.
    private const string Before = "20261116021841_MembershipRoleRequired";
    private const string Switch = "20261116032606_CampSpawnKeySwitch";

    private const string Index = "IX_LinkshellMonsterTimings_LinkshellId_SpawnKey";

    private static readonly (string Table, string NameColumn)[] Tables =
    {
        ("Events", "AssignedMonsterName"),
        ("PartySetups", "AssignedMonsterName"),
        ("ClaimShieldCaptures", "MonsterName"),
        ("LinkshellMonsterTimings", "MonsterName"),
    };

    private readonly PostgresFixture _pg;

    public CampSpawnKeySwitchRoundTripTests(PostgresFixture pg) => _pg = pg;

    [Fact]
    public async Task MigrationSpawnKeySql_MatchesTheHook_ForEveryInput()
    {
        var names = AddCampSpawnKeysRoundTripTests.ProductionNames.Cast<string?>()
            .Concat(TodDerivedColumnsParityTests.MonsterNames)
            .Concat(new string?[] { "Fafnir", " fafnir/NIDHOGG ", "Foo / Bar", "", "  ", null })
            .Append(new string('x', 300))
            .Append("Fafnir/" + new string(' ', 50) + "/Nidhogg")
            // A no-break space and an ideographic space, which .NET trims and an ASCII-only trim does not.
            .Append((char)0x00A0 + "Nidhogg" + (char)0x3000)
            .Append("Tiamat" + (char)0x2005 + "/" + (char)0x205F + "Foo")
            .ToList();

        await using var c = new NpgsqlConnection(_pg.ConnectionString);
        await c.OpenAsync();
        var mismatches = new List<string>();
        foreach (var name in names)
        {
            await using var command = c.CreateCommand();
            command.CommandText =
                $"SELECT {LinkshellManagerDiscordApp.Migrations.CampSpawnKeySwitch.SpawnKeySql("q.v")} FROM (SELECT @v::text AS v) q";
            command.Parameters.Add(new NpgsqlParameter("v", NpgsqlDbType.Text) { Value = (object?)name ?? DBNull.Value });
            var result = await command.ExecuteScalarAsync();
            var migrationKey = result is DBNull or null ? null : (string)result;
            var hookKey = HnmConfig.SpawnKey(name);
            if (migrationKey != hookKey)
            {
                mismatches.Add($"{Show(name)}: migration {Show(migrationKey)}, C# {Show(hookKey)}");
            }
        }
        Assert.True(mismatches.Count == 0, string.Join("\n", mismatches));
    }

    [Fact]
    public async Task CampSpawnKeySwitch_GoesUpDownAndUpAgain()
    {
        var connectionString = await SchemaTestDatabase.CreateAsync(_pg, "camp_switch_");
        try
        {
            await SchemaTestDatabase.MigrateToAsync(connectionString, Before);
            await using (var c = await OpenAsync(connectionString))
            {
                await SeedLinkshellsAsync(c);

                // What an older build leaves: no key at all, or the key of the name before a rename.
                // One event row is already right and must not be written again.
                await InsertEventAsync(c, "Fafnir", key: null);
                await InsertEventAsync(c, " fafnir/NIDHOGG ", key: null);
                await InsertEventAsync(c, "King Behemoth", key: "tiamat");
                await InsertEventAsync(c, "Foo / Bar", key: null);
                await InsertEventAsync(c, "Tiamat", key: "tiamat");
                await InsertEventAsync(c, "", key: null);
                await InsertEventAsync(c, null, key: null);

                foreach (var name in new[] { "Fafnir", " fafnir/NIDHOGG ", "King Behemoth", "Foo / Bar", "Tiamat", "", null })
                {
                    await InsertSetupAsync(c, name, key: null);
                }
                foreach (var name in new[] { "Fafnir", " fafnir/NIDHOGG ", "King Behemoth", "Foo / Bar", "Tiamat", "" })
                {
                    await InsertCaptureAsync(c, name, key: null);
                }

                // One row per spawn in each linkshell, plus two blank names (two NULL keys, which
                // never clash). Linkshell 2 may have its own Fafnir/Nidhogg row.
                await InsertTimingAsync(c, 1, " fafnir/NIDHOGG ", "t1", key: null);
                await InsertTimingAsync(c, 1, "King Behemoth", "t2", key: "tiamat");
                await InsertTimingAsync(c, 1, "Foo / Bar", "t3", key: null);
                await InsertTimingAsync(c, 1, "Tiamat", "t4", key: null);
                await InsertTimingAsync(c, 1, "", "t5", key: null);
                await InsertTimingAsync(c, 1, "  ", "t6", key: null);
                await InsertTimingAsync(c, 2, "Nidhogg", "t7", key: null);
            }
            var rowsBefore = await CountRowsAsync(connectionString);

            // Up.
            var log = await MigrateAsync(connectionString, Switch);
            Assert.Contains(
                "Postgres NOTICE during migration: CampSpawnKeySwitch: re-keyed 4 event(s), 5 party setup(s), " +
                "5 claim shield capture(s), 5 monster timing(s).",
                Messages(log));
            Assert.Contains($"Postgres NOTICE during migration: CampSpawnKeySwitch: {Index} is now unique.", Messages(log));
            Assert.DoesNotContain(log.Entries, e => e.Level >= LogLevel.Warning);
            await AssertKeyedAsync(connectionString);
            Assert.True(await IsUniqueAsync(connectionString));
            Assert.Equal(rowsBefore, await CountRowsAsync(connectionString));

            // A second row for one spawn in one linkshell is refused, whatever it is called.
            await using (var c = await OpenAsync(connectionString))
            {
                var refused = await Assert.ThrowsAsync<PostgresException>(
                    () => InsertTimingAsync(c, 1, "Nidhogg", "nidhogg", key: "fafnir/nidhogg"));
                Assert.Equal(PostgresErrorCodes.UniqueViolation, refused.SqlState);
                Assert.Equal(Index, refused.ConstraintName);
            }

            // Down: the plain index again, the keys stay, no row is lost.
            var keysAfterUp = await KeysAsync(connectionString);
            await SchemaTestDatabase.MigrateToAsync(connectionString, Before);
            Assert.False(await IsUniqueAsync(connectionString));
            Assert.Equal(keysAfterUp, await KeysAsync(connectionString));
            Assert.Equal(rowsBefore, await CountRowsAsync(connectionString));

            // An older build saves while rolled back: new rows with no key, a rename keeps the old key.
            await using (var c = await OpenAsync(connectionString))
            {
                await InsertEventAsync(c, "Nidhogg", key: null);
                await ExecuteAsync(c, "UPDATE \"Events\" SET \"AssignedMonsterName\" = 'Vrtra' WHERE \"AssignedMonsterName\" = 'Foo / Bar'");
                await InsertTimingAsync(c, 1, "Cerberus", "t8", key: null);
            }

            // Up again: the three rows are repaired and the index is unique again.
            log = await MigrateAsync(connectionString, Switch);
            Assert.Contains(
                "Postgres NOTICE during migration: CampSpawnKeySwitch: re-keyed 2 event(s), 0 party setup(s), " +
                "0 claim shield capture(s), 1 monster timing(s).",
                Messages(log));
            Assert.DoesNotContain(log.Entries, e => e.Level >= LogLevel.Warning);
            await AssertKeyedAsync(connectionString);
            Assert.True(await IsUniqueAsync(connectionString));

            // Both steps once more by hand: nothing left to write, and the index is built again.
            await using (var c = await OpenAsync(connectionString))
            {
                var notices = new List<string>();
                c.Notice += (_, e) => notices.Add(e.Notice.MessageText);
                await ExecuteAsync(c, LinkshellManagerDiscordApp.Migrations.CampSpawnKeySwitch.FillSql);
                await ExecuteAsync(c, LinkshellManagerDiscordApp.Migrations.CampSpawnKeySwitch.UniqueIndexSql);
                Assert.Contains(
                    "CampSpawnKeySwitch: re-keyed 0 event(s), 0 party setup(s), 0 claim shield capture(s), 0 monster timing(s).",
                    notices);
                Assert.Contains($"CampSpawnKeySwitch: {Index} is now unique.", notices);
            }
            Assert.True(await IsUniqueAsync(connectionString));

            // Built from Baseline to here, the database is healthy.
            await using (var db = SchemaTestDatabase.CreateContext(connectionString))
            {
                var health = new MigrationNoticeLoggingTests.CapturingLogger();
                Assert.Equal(0, await MigrationHealth.LogProblemsAsync(db, health, CancellationToken.None));
            }
        }
        finally
        {
            await SchemaTestDatabase.DropAsync(_pg, connectionString);
        }
    }

    // Two Monster Setups rows of one linkshell name one spawn ("Fafnir" and "Nidhogg"): the unique
    // index would fail, so it stays plain and the WARNING says so. Start-up goes on, and
    // MigrationHealth keeps reporting it. Once a row is fixed, Down and Up make it unique.
    [Fact]
    public async Task CampSpawnKeySwitch_KeepsThePlainIndex_WithAWarning_WhenTwoRowsNameOneSpawn()
    {
        var connectionString = await SchemaTestDatabase.CreateAsync(_pg, "camp_switch_skip_");
        try
        {
            await SchemaTestDatabase.MigrateToAsync(connectionString, Before);
            await using (var c = await OpenAsync(connectionString))
            {
                await SeedLinkshellsAsync(c);
                await InsertTimingAsync(c, 1, "Fafnir", "fafnir", key: null);
                await InsertTimingAsync(c, 1, "Nidhogg", "nidhogg", key: null);
                await InsertTimingAsync(c, 1, "Tiamat", "tiamat", key: null);
                await InsertTimingAsync(c, 2, "Fafnir", "fafnir", key: null);
            }
            var timingsBefore = await TimingCountAsync(connectionString);

            var log = await MigrateAsync(connectionString, Switch);
            var warning = Assert.Single(log.Entries, e => e.Level == LogLevel.Warning);
            Assert.Equal(
                $"Postgres WARNING during migration: CampSpawnKeySwitch: {Index} stays non-unique: 1 spawn(s) have two or more " +
                "Monster Setups rows in one linkshell. Run pre-check E2, keep one row per spawn, and a later migration makes it unique.",
                warning.Message);
            Assert.False(await IsUniqueAsync(connectionString));
            await AssertTimingsKeyedAsync(connectionString);
            Assert.Equal(timingsBefore, await TimingCountAsync(connectionString));
            await using (var db = SchemaTestDatabase.CreateContext(connectionString))
            {
                Assert.Contains(Switch, await db.Database.GetAppliedMigrationsAsync());
                var health = new MigrationNoticeLoggingTests.CapturingLogger();
                Assert.Equal(1, await MigrationHealth.LogProblemsAsync(db, health, CancellationToken.None));
                var healthWarning = Assert.Single(health.Entries, e => e.Level == LogLevel.Warning);
                Assert.Contains(Index, healthWarning.Message);
                Assert.Contains("not unique", healthWarning.Message);
            }

            // Down works on the half-done index.
            await SchemaTestDatabase.MigrateToAsync(connectionString, Before);
            Assert.False(await IsUniqueAsync(connectionString));

            // An officer renames one row with the older build, so its key goes stale. Up re-keys it
            // first, and the unique index now goes in.
            await SchemaTestDatabase.ExecuteAsync(connectionString,
                "UPDATE \"LinkshellMonsterTimings\" SET \"MonsterName\" = 'Cerberus', \"NormalizedMonsterName\" = 'cerberus' " +
                "WHERE \"LinkshellId\" = 1 AND \"MonsterName\" = 'Nidhogg'");
            log = await MigrateAsync(connectionString, Switch);
            Assert.DoesNotContain(log.Entries, e => e.Level >= LogLevel.Warning);
            Assert.True(await IsUniqueAsync(connectionString));
            await AssertTimingsKeyedAsync(connectionString);
            await using (var db = SchemaTestDatabase.CreateContext(connectionString))
            {
                var health = new MigrationNoticeLoggingTests.CapturingLogger();
                Assert.Equal(0, await MigrationHealth.LogProblemsAsync(db, health, CancellationToken.None));
            }
        }
        finally
        {
            await SchemaTestDatabase.DropAsync(_pg, connectionString);
        }
    }

    private static async Task SeedLinkshellsAsync(NpgsqlConnection c)
    {
        foreach (var id in new[] { 1, 2 })
        {
            await ExecuteAsync(c,
                "INSERT INTO \"Linkshells\" (\"Id\", \"LinkshellName\", \"AuctionsLocked\", \"DkpRoundingIncrement\", " +
                "\"EnableAuctions\", \"EnableDkp\", \"EnableEndgame\", \"EnableEvents\", \"EnableHnmSection\", \"EnableToDs\", " +
                "\"EnableTreasury\", \"HiddenTodMonsters\", \"LootStructure\") " +
                $"VALUES ({id}, 'LS{id}', false, '0.25', false, true, false, true, true, true, false, '', 'Dkp')");
        }
    }

    // Raw SQL, as an older build that does not stamp the key would leave the row.
    private static Task InsertEventAsync(NpgsqlConnection c, string? name, string? key) =>
        ExecuteAsync(c,
            "INSERT INTO \"Events\" (\"LinkshellId\", \"AutoStart\", \"EventName\", \"AssignedMonsterName\", \"SpawnKey\") " +
            $"VALUES (1, false, 'Camp', {Literal(name)}, {Literal(key)})");

    private static Task InsertSetupAsync(NpgsqlConnection c, string? name, string? key) =>
        ExecuteAsync(c,
            "INSERT INTO \"PartySetups\" (\"LinkshellId\", \"Name\", \"AssignedMonsterName\", \"CreatedAt\", \"UpdatedAt\", \"SpawnKey\") " +
            $"VALUES (1, 'Setup', {Literal(name)}, now(), now(), {Literal(key)})");

    private static Task InsertCaptureAsync(NpgsqlConnection c, string name, string? key) =>
        ExecuteAsync(c,
            "INSERT INTO \"ClaimShieldCaptures\" (\"LinkshellId\", \"MonsterName\", \"Won\", \"TotalPlayers\", " +
            $"\"CapturedAtUtc\", \"CreatedAtUtc\", \"SpawnKey\") VALUES (1, {Literal(name)}, true, 3, now(), now(), {Literal(key)})");

    private static Task InsertTimingAsync(NpgsqlConnection c, int linkshellId, string name, string normalized, string? key) =>
        ExecuteAsync(c,
            "INSERT INTO \"LinkshellMonsterTimings\" (\"LinkshellId\", \"MonsterName\", \"NormalizedMonsterName\", " +
            "\"CooldownMinutes\", \"IsCustom\", \"SortOrder\", \"CreatedAtUtc\", \"UpdatedAtUtc\", \"SpawnKey\") " +
            $"VALUES ({linkshellId}, {Literal(name)}, {Literal(normalized)}, 1320, false, 0, now(), now(), {Literal(key)})");

    // Every row of every camp table carries the key its monster name gives.
    private static async Task AssertKeyedAsync(string connectionString)
    {
        foreach (var (table, nameColumn) in Tables)
        {
            await AssertTableKeyedAsync(connectionString, table, nameColumn);
        }
    }

    private static Task AssertTimingsKeyedAsync(string connectionString) =>
        AssertTableKeyedAsync(connectionString, "LinkshellMonsterTimings", "MonsterName");

    private static async Task AssertTableKeyedAsync(string connectionString, string table, string nameColumn)
    {
        await using var c = await OpenAsync(connectionString);
        var rows = await RowsAsync(c, $"SELECT \"{nameColumn}\", \"SpawnKey\" FROM \"{table}\" ORDER BY \"Id\"");
        Assert.NotEmpty(rows);
        foreach (var (name, key) in rows)
        {
            Assert.True(key == HnmConfig.SpawnKey(name), $"{table}: {Show(name)} keyed {Show(key)}");
        }
    }

    private static async Task<List<string>> KeysAsync(string connectionString)
    {
        await using var c = await OpenAsync(connectionString);
        var keys = new List<string>();
        foreach (var (table, _) in Tables)
        {
            foreach (var (id, key) in await RowsAsync(c, $"SELECT \"Id\"::text, \"SpawnKey\" FROM \"{table}\" ORDER BY \"Id\""))
            {
                keys.Add($"{table} {id} {Show(key)}");
            }
        }
        return keys;
    }

    private static async Task<long> CountRowsAsync(string connectionString)
    {
        await using var c = await OpenAsync(connectionString);
        long total = 0;
        foreach (var (table, _) in Tables)
        {
            await using var command = c.CreateCommand();
            command.CommandText = $"SELECT count(*) FROM \"{table}\"";
            total += (long)(await command.ExecuteScalarAsync())!;
        }
        return total;
    }

    private static async Task<long> TimingCountAsync(string connectionString)
    {
        await using var c = await OpenAsync(connectionString);
        await using var command = c.CreateCommand();
        command.CommandText = "SELECT count(*) FROM \"LinkshellMonsterTimings\"";
        return (long)(await command.ExecuteScalarAsync())!;
    }

    private static async Task<bool> IsUniqueAsync(string connectionString)
    {
        await using var c = await OpenAsync(connectionString);
        await using var command = c.CreateCommand();
        command.CommandText =
            "SELECT i.indisunique FROM pg_index i JOIN pg_class x ON x.oid = i.indexrelid " +
            $"WHERE x.relname = '{Index}'";
        var result = await command.ExecuteScalarAsync();
        Assert.True(result is bool, $"{Index} is missing");
        return (bool)result!;
    }

    private static async Task<List<(string? First, string? Second)>> RowsAsync(NpgsqlConnection c, string sql)
    {
        var rows = new List<(string?, string?)>();
        await using var command = c.CreateCommand();
        command.CommandText = sql;
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            rows.Add((reader.IsDBNull(0) ? null : reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1)));
        }
        return rows;
    }

    // Moves the database to exactly this migration with the notices logged the way start-up logs them.
    private static async Task<MigrationNoticeLoggingTests.CapturingLogger> MigrateAsync(string connectionString, string target)
    {
        var log = new MigrationNoticeLoggingTests.CapturingLogger();
        await using var db = SchemaTestDatabase.CreateContext(connectionString);
        await MigrationNotices.RunWithNoticesLoggedAsync(db, log, async () =>
        {
            await db.GetService<IMigrator>().MigrateAsync(target);
            return 0;
        });
        return log;
    }

    private static List<string> Messages(MigrationNoticeLoggingTests.CapturingLogger log) =>
        log.Entries.Select(e => e.Message).ToList();

    private static string Literal(string? value) =>
        value is null ? "NULL" : "'" + value.Replace("'", "''") + "'";

    private static string Show(string? value) => value is null ? "NULL" : "\"" + value + "\"";

    private static async Task<NpgsqlConnection> OpenAsync(string connectionString)
    {
        var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        return connection;
    }

    private static async Task ExecuteAsync(NpgsqlConnection c, string sql)
    {
        await using var command = c.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }
}
