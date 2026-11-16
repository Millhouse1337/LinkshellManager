using System;
using System.Collections.Generic;
using System.Linq;
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

// Round 5, R5.2: AddCampSpawnKeys adds SpawnKey to Events, PartySetups, ClaimShieldCaptures and
// LinkshellMonsterTimings and fills it from the monster name with SQL copied into the migration.
//
// - The copied SQL gives the same key as HnmConfig.SpawnKey (what the save hook stamps) and as
//   TodDerivedSql.SpawnKeySql (what filled Tods and boards) for every monster name production holds
//   plus the edge cases.
// - Up, Down and Up again from Baseline on messy rows: the keys are right, the indexes come and go,
//   no row is lost, and the fill says in a NOTICE what it wrote. Running the fill again repairs a
//   key an older build left wrong and writes nothing else.
//
// Its OWN database inside the test container, dropped at the end.
[Collection("postgres")]
public class AddCampSpawnKeysRoundTripTests
{
    private const string Baseline = "20261115190359_Baseline";

    // Named, not "latest", so a later migration's Down never joins this test.
    private const string AddCampSpawnKeys = "20261116013920_AddCampSpawnKeys";

    private static readonly string[] Tables = { "Events", "PartySetups", "ClaimShieldCaptures", "LinkshellMonsterTimings" };

    private static readonly string[] Indexes =
    {
        "IX_Events_LinkshellId_SpawnKey",
        "IX_ClaimShieldCaptures_LinkshellId_SpawnKey",
        "IX_LinkshellMonsterTimings_LinkshellId_SpawnKey",
    };

    // Every monster name production holds on the camp tables (pre-check, 2026-11-15).
    public static readonly string[] ProductionNames =
    {
        "Adamantoise", "Aspidochelone", "Adamantoise/Aspidochelone",
        "Behemoth", "King Behemoth", "Behemoth/King Behemoth",
        "Fafnir", "Nidhogg", "Fafnir/Nidhogg",
        "Cerberus", "Hydra", "Jormungand", "Khimaira", "King Arthro", "Tiamat", "Vrtra",
    };

    // The names the round trip seeds into each camp table: production spellings, padding and
    // capitals, a custom combined name, and the blank ones (NULL only where the column allows it).
    private static readonly string?[] SeededNames =
    {
        "Fafnir", " fafnir/NIDHOGG ", "King Behemoth", "Foo / Bar", "Tiamat", "", "  ", null,
    };

    private readonly PostgresFixture _pg;

    public AddCampSpawnKeysRoundTripTests(PostgresFixture pg) => _pg = pg;

    [Fact]
    public async Task MigrationSpawnKeySql_MatchesTheHookAndTheTodSql_ForEveryInput()
    {
        var names = ProductionNames.Cast<string?>()
            .Concat(TodDerivedColumnsParityTests.MonsterNames)
            .Concat(SeededNames)
            .Append(new string('x', 300))
            .Append("Fafnir/" + new string(' ', 50) + "/Nidhogg")
            .ToList();

        await using var c = new NpgsqlConnection(_pg.ConnectionString);
        await c.OpenAsync();
        var mismatches = new List<string>();
        foreach (var name in names)
        {
            await using var command = c.CreateCommand();
            command.CommandText =
                $"SELECT {LinkshellManagerDiscordApp.Migrations.AddCampSpawnKeys.SpawnKeySql("q.v")}, " +
                $"{TodDerivedSql.SpawnKeySql("q.v")} FROM (SELECT @v::text AS v) q";
            command.Parameters.Add(new NpgsqlParameter("v", NpgsqlDbType.Text) { Value = (object?)name ?? DBNull.Value });
            await using var reader = await command.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            var migrationKey = reader.IsDBNull(0) ? null : reader.GetString(0);
            var todKey = reader.IsDBNull(1) ? null : reader.GetString(1);
            var hookKey = HnmConfig.SpawnKey(name);
            if (migrationKey != hookKey || todKey != hookKey)
            {
                mismatches.Add($"{Show(name)}: migration {Show(migrationKey)}, ToD SQL {Show(todKey)}, C# {Show(hookKey)}");
            }
        }
        Assert.True(mismatches.Count == 0, string.Join("\n", mismatches));
    }

    // The production names come out as the keys the ToD tracker already uses.
    [Fact]
    public void ProductionNames_GiveOneKeyPerSpawn()
    {
        var keys = ProductionNames.Select(HnmConfig.SpawnKey).Distinct().ToList();
        Assert.Equal(
            new[]
            {
                "adamantoise/aspidochelone", "behemoth/king behemoth", "fafnir/nidhogg",
                "cerberus", "hydra", "jormungand", "khimaira", "king arthro", "tiamat", "vrtra",
            },
            keys);
    }

    [Fact]
    public async Task AddCampSpawnKeys_GoesUpDownAndUpAgain()
    {
        var connectionString = await SchemaTestDatabase.CreateAsync(_pg, "camp_spawnkeys_");
        try
        {
            await MigrateAsync(connectionString, Baseline);
            await SeedAsync(connectionString);
            var rowsBefore = await CountRowsAsync(connectionString);

            // Up: every row keyed, the indexes built, one NOTICE with the counts.
            var notices = await MigrateAsync(connectionString, AddCampSpawnKeys);
            await AssertKeyedAsync(connectionString);
            Assert.Equal(rowsBefore, await CountRowsAsync(connectionString));
            Assert.Contains(
                "Postgres NOTICE during migration: AddCampSpawnKeys: keyed 5 event(s), 5 party setup(s), " +
                "5 claim shield capture(s), 5 monster timing(s).",
                notices);

            // Down: the columns and indexes go, every row stays.
            await MigrateAsync(connectionString, Baseline);
            await using (var c = await OpenAsync(connectionString))
            {
                foreach (var table in Tables)
                {
                    Assert.False(await ColumnExistsAsync(c, table), $"{table}.SpawnKey is still there after Down");
                }
                foreach (var index in Indexes)
                {
                    Assert.False(await IndexExistsAsync(c, index), $"{index} is still there after Down");
                }
                // The index Up must never replace.
                Assert.True(await IndexExistsAsync(c, "IX_Events_LinkshellId"));
            }
            Assert.Equal(rowsBefore, await CountRowsAsync(connectionString));

            // Up again: the same keys.
            await MigrateAsync(connectionString, AddCampSpawnKeys);
            await AssertKeyedAsync(connectionString);
            Assert.Equal(rowsBefore, await CountRowsAsync(connectionString));

            // The fill again: a key an older build left wrong or empty is repaired, nothing else is written.
            await using (var c = await OpenAsync(connectionString))
            {
                await ExecuteAsync(c, "UPDATE \"Events\" SET \"SpawnKey\" = 'tiamat' WHERE \"AssignedMonsterName\" = 'Fafnir'");
                await ExecuteAsync(c, "UPDATE \"LinkshellMonsterTimings\" SET \"SpawnKey\" = NULL WHERE \"MonsterName\" = 'King Behemoth'");
                var log = new NoticeLog();
                c.Notice += (_, e) => log.Add(e.Notice.MessageText);
                await ExecuteAsync(c, LinkshellManagerDiscordApp.Migrations.AddCampSpawnKeys.FillSql);
                Assert.Contains(
                    "AddCampSpawnKeys: keyed 1 event(s), 0 party setup(s), 0 claim shield capture(s), 1 monster timing(s).",
                    log.Messages);
            }
            await AssertKeyedAsync(connectionString);
        }
        finally
        {
            await SchemaTestDatabase.DropAsync(_pg, connectionString);
        }
    }

    // Raw SQL, at Baseline: the EF model already has the new columns, so an EF insert would name a
    // column that does not exist yet.
    private static async Task SeedAsync(string connectionString)
    {
        await using var c = await OpenAsync(connectionString);
        await ExecuteAsync(c,
            "INSERT INTO \"Linkshells\" (\"Id\", \"LinkshellName\", \"AuctionsLocked\", \"DkpRoundingIncrement\", " +
            "\"EnableAuctions\", \"EnableDkp\", \"EnableEndgame\", \"EnableEvents\", \"EnableHnmSection\", \"EnableToDs\", " +
            "\"EnableTreasury\", \"HiddenTodMonsters\", \"LootStructure\") " +
            "VALUES (1, 'LS', false, '0.25', false, true, false, true, true, true, false, '', 'Dkp')");

        for (var i = 0; i < SeededNames.Length; i++)
        {
            var name = SeededNames[i];
            var literal = Literal(name);
            await ExecuteAsync(c,
                "INSERT INTO \"Events\" (\"LinkshellId\", \"AutoStart\", \"EventName\", \"AssignedMonsterName\") " +
                $"VALUES (1, false, 'Camp {i}', {literal})");
            await ExecuteAsync(c,
                "INSERT INTO \"PartySetups\" (\"LinkshellId\", \"Name\", \"AssignedMonsterName\", \"CreatedAt\", \"UpdatedAt\") " +
                $"VALUES (1, 'Setup {i}', {literal}, now(), now())");

            // The capture and timing names are NOT NULL, so their blank is '' only.
            if (name is null)
            {
                continue;
            }
            await ExecuteAsync(c,
                "INSERT INTO \"ClaimShieldCaptures\" (\"LinkshellId\", \"MonsterName\", \"Won\", \"TotalPlayers\", " +
                $"\"CapturedAtUtc\", \"CreatedAtUtc\") VALUES (1, {literal}, true, 3, now(), now())");
            await ExecuteAsync(c,
                "INSERT INTO \"LinkshellMonsterTimings\" (\"LinkshellId\", \"MonsterName\", \"NormalizedMonsterName\", " +
                "\"CooldownMinutes\", \"IsCustom\", \"SortOrder\", \"CreatedAtUtc\", \"UpdatedAtUtc\") " +
                $"VALUES (1, {literal}, 'n{i}', 1320, false, {i}, now(), now())");
        }
    }

    private static async Task AssertKeyedAsync(string connectionString)
    {
        await using var c = await OpenAsync(connectionString);
        foreach (var index in Indexes)
        {
            Assert.True(await IndexExistsAsync(c, index), $"{index} is missing after Up");
        }
        Assert.True(await IndexExistsAsync(c, "IX_Events_LinkshellId"));

        var checks = new[]
        {
            ("Events", "AssignedMonsterName"),
            ("PartySetups", "AssignedMonsterName"),
            ("ClaimShieldCaptures", "MonsterName"),
            ("LinkshellMonsterTimings", "MonsterName"),
        };
        foreach (var (table, nameColumn) in checks)
        {
            var rows = new List<(string? Name, string? Key)>();
            await using (var command = c.CreateCommand())
            {
                command.CommandText = $"SELECT \"{nameColumn}\", \"SpawnKey\" FROM \"{table}\" ORDER BY \"Id\"";
                await using var reader = await command.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    rows.Add((reader.IsDBNull(0) ? null : reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1)));
                }
            }
            Assert.NotEmpty(rows);
            foreach (var (name, key) in rows)
            {
                Assert.True(key == HnmConfig.SpawnKey(name), $"{table}: {Show(name)} keyed {Show(key)}");
            }
        }

        // The spellings the plan names, spelled out once so a change in HnmConfig shows up here too.
        Assert.Equal(
            new[] { "fafnir/nidhogg", "fafnir/nidhogg", "behemoth/king behemoth", "foo/bar", "tiamat", null, null, null },
            await ColumnAsync(c, "SELECT \"SpawnKey\" FROM \"Events\" ORDER BY \"Id\""));
    }

    private static async Task<long> CountRowsAsync(string connectionString)
    {
        await using var c = await OpenAsync(connectionString);
        long total = 0;
        foreach (var table in Tables)
        {
            await using var command = c.CreateCommand();
            command.CommandText = $"SELECT count(*) FROM \"{table}\"";
            total += (long)(await command.ExecuteScalarAsync())!;
        }
        return total;
    }

    private static async Task<List<string?>> ColumnAsync(NpgsqlConnection c, string sql)
    {
        var values = new List<string?>();
        await using var command = c.CreateCommand();
        command.CommandText = sql;
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            values.Add(reader.IsDBNull(0) ? null : reader.GetString(0));
        }
        return values;
    }

    private static async Task<bool> ColumnExistsAsync(NpgsqlConnection c, string table)
    {
        await using var command = c.CreateCommand();
        command.CommandText =
            "SELECT EXISTS (SELECT 1 FROM information_schema.columns " +
            $"WHERE table_schema = 'public' AND table_name = '{table}' AND column_name = 'SpawnKey')";
        return (bool)(await command.ExecuteScalarAsync())!;
    }

    private static async Task<bool> IndexExistsAsync(NpgsqlConnection c, string index)
    {
        await using var command = c.CreateCommand();
        command.CommandText = $"SELECT to_regclass('public.\"{index}\"') IS NOT NULL";
        return (bool)(await command.ExecuteScalarAsync())!;
    }

    // Moves the database to exactly this migration, forwards or backwards, with the notices logged
    // the way start-up logs them. Returns the logged lines.
    private static async Task<IReadOnlyList<string>> MigrateAsync(string connectionString, string targetMigration)
    {
        var log = new NoticeLog();
        await using var db = SchemaTestDatabase.CreateContext(connectionString);
        await MigrationNotices.RunWithNoticesLoggedAsync(db, log, async () =>
        {
            await db.GetService<IMigrator>().MigrateAsync(targetMigration);
            return 0;
        });
        return log.Messages;
    }

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

    // Collects log lines (and raw notice texts) for the assertions above.
    private sealed class NoticeLog : ILogger
    {
        private readonly object _gate = new();
        private readonly List<string> _messages = new();

        public IReadOnlyList<string> Messages
        {
            get
            {
                lock (_gate)
                {
                    return _messages.ToList();
                }
            }
        }

        public void Add(string message)
        {
            lock (_gate)
            {
                _messages.Add(message);
            }
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Add(formatter(state, exception));
    }
}
