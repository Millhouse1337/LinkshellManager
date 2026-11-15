using LinkshellManagerDiscordApp.Data;
using LinkshellManagerDiscordApp.Migrations;
using LinkshellManagerDiscordApp.Models;
using LinkshellManagerDiscordApp.Services;
using LinkshellManagerDiscordApp.Utils;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using Xunit;

namespace LinkshellManager.PostgresTests;

// TodSwitch re-fills the derived ToD columns, deletes the standing boards nobody can use (a blank
// monster name, or a second board for one spawn), and then makes the board's SpawnKey required and
// unique per linkshell.
//
// Seeded at the migration before with the rows an older build or Data Admin can leave behind: ToDs,
// pending ToDs and boards whose derived columns are missing or wrong; "Fafnir/Nidhogg" (enabled)
// beside "fafnir/nidhogg" (disabled, newer); " Tiamat" beside "tiamat" (both enabled, the second
// newer); "Cerberus" beside "CERBERUS" (same switch and time, so the newer id wins); two blank names;
// a "/" board (key "", which is kept); and a Tiamat board in another linkshell, which is not a
// duplicate. Running the SQL again changes nothing. The new rules are checked. Down keeps every row;
// an old build then adds rows the rules refuse, and Up again cleans them up.
//
// Its OWN database inside the test container, like DropRedundantJobRatingsIndexRoundTripTests.
// Deleted at the end.
[Collection("postgres")]
public class TodSwitchRoundTripTests
{
    private const string Before = "20261115053404_RosterSwitch";

    // Named, not "latest", so a later migration's Down never joins this test.
    private const string Switch = "20261115064934_TodSwitch";

    private const string SpawnIndex = "IX_HnmRecurringBoards_LinkshellId_SpawnKey";

    private readonly PostgresFixture _pg;

    public TodSwitchRoundTripTests(PostgresFixture pg) => _pg = pg;

    [Fact]
    public async Task TodSwitch_MergesBoardsAndRefills_AndGoesUpDownAndUpAgain()
    {
        var databaseName = "tod_switch_" + Guid.NewGuid().ToString("N")[..8];
        await ExecuteAsync(_pg.ConnectionString, $"CREATE DATABASE \"{databaseName}\"");

        var connectionString = new NpgsqlConnectionStringBuilder(_pg.ConnectionString)
        {
            Database = databaseName,
            // No pooled connections left behind, so DROP DATABASE at the end is not blocked.
            Pooling = false,
        }.ConnectionString;

        try
        {
            await MigrateAsync(connectionString, Before);
            var seed = await SeedAsync(connectionString);

            // Nothing points at a board's id, so deleting a board can't break or cascade anything.
            await using (var c = await OpenAsync(connectionString))
            {
                Assert.Equal(0L, await ScalarAsync(c,
                    "SELECT count(*) FROM pg_constraint WHERE contype = 'f' AND confrelid = '\"HnmRecurringBoards\"'::regclass"));
            }

            // Up: every derived column matches its text; one board per spawn is left, the right one.
            await MigrateAsync(connectionString, Switch);
            await using (var c = await OpenAsync(connectionString))
            {
                await AssertSchemaAsync(c, present: true);
                await AssertFilledLikeCSharpAsync(c);
                Assert.Equal(seed.Kept, await BoardIdsAsync(c));
                Assert.Equal(seed.TodCount, await ScalarAsync(c, "SELECT count(*) FROM \"Tods\""));
                Assert.Equal(seed.PendingCount, await ScalarAsync(c, "SELECT count(*) FROM \"PendingTodSubmissions\""));

                // Running the SQL again changes nothing, and says so.
                var notices = new List<string>();
                c.Notice += (_, e) => notices.Add(e.Notice.MessageText);
                await ScalarAsync(c, AddTodDurationsAndSpawnKeys.FillSql);
                await ScalarAsync(c, TodSwitch.DeleteUnusableBoardsSql);
                Assert.Contains("AddTodDurationsAndSpawnKeys: filled 0 ToD(s), 0 pending ToD(s), 0 standing board(s).", notices);
                Assert.Contains("TodSwitch: deleted 0 standing board(s) with no monster name (ids: none) and 0 second board(s) for one spawn (ids: none).", notices);
                Assert.Equal(seed.Kept, await BoardIdsAsync(c));

                // The new rules: a board needs a key, and a spawn gets one board per linkshell.
                var noKey = await Assert.ThrowsAsync<PostgresException>(() => ScalarAsync(c,
                    Board(seed.Main, "Vrtra", spawnKey: null, enabled: true, updated: "now()")));
                Assert.Equal(PostgresErrorCodes.NotNullViolation, noKey.SqlState);
                var secondBoard = await Assert.ThrowsAsync<PostgresException>(() => ScalarAsync(c,
                    Board(seed.Main, "Nidhogg", spawnKey: "fafnir/nidhogg", enabled: true, updated: "now()")));
                Assert.Equal(PostgresErrorCodes.UniqueViolation, secondBoard.SqlState);
                // Another linkshell's board for the same spawn is fine (the seed already has one).
            }

            // The app's save hook gives a new board its key, and the unique index stops a second one.
            await using (var db = NewContext(connectionString))
            {
                db.HnmRecurringBoards.Add(new HnmRecurringBoard
                {
                    LinkshellId = seed.Main, MonsterName = "Jormungand", Enabled = true, LeadHours = 1,
                    CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
                });
                await db.SaveChangesAsync();
            }
            await using (var db = NewContext(connectionString))
            {
                db.HnmRecurringBoards.Add(new HnmRecurringBoard
                {
                    LinkshellId = seed.Main, MonsterName = " JORMUNGAND ", Enabled = true, LeadHours = 1,
                    CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
                });
                var refused = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
                Assert.Equal(PostgresErrorCodes.UniqueViolation, Assert.IsType<PostgresException>(refused.InnerException).SqlState);
            }
            var kept = seed.Kept.Append(await JormungandIdAsync(connectionString)).Order().ToList();

            // Down: the index is plain again and the column may be NULL; every row stays.
            await MigrateAsync(connectionString, Before);
            await using (var c = await OpenAsync(connectionString))
            {
                await AssertSchemaAsync(c, present: false);
                Assert.Equal(kept, await BoardIdsAsync(c));

                // Rows a build without these rules could write: a board with no key, a second board
                // for a spawn, and a ToD whose derived columns were never stamped.
                var lateBlank = await InsertAsync(c, Board(seed.Main, "  ", spawnKey: null, enabled: true, updated: "now()"));
                var lateDuplicate = await InsertAsync(c, Board(seed.Main, "TIAMAT", spawnKey: "tiamat", enabled: false, updated: "now()"));
                await ScalarAsync(c, Tod(seed.Main, "Fafnir", "84 Hour", null, spawnKey: null, cooldownMinutes: null));

                // The same SQL cleans them up and lists the ids it deleted.
                var notices = new List<string>();
                c.Notice += (_, e) => notices.Add(e.Notice.MessageText);
                await ScalarAsync(c, AddTodDurationsAndSpawnKeys.FillSql);
                await ScalarAsync(c, TodSwitch.DeleteUnusableBoardsSql);
                Assert.Contains("AddTodDurationsAndSpawnKeys: filled 1 ToD(s), 0 pending ToD(s), 0 standing board(s).", notices);
                Assert.Contains($"TodSwitch: deleted 1 standing board(s) with no monster name (ids: {lateBlank}) and 1 second board(s) for one spawn (ids: {lateDuplicate}).", notices);
                Assert.Equal(kept, await BoardIdsAsync(c));

                // And one more the migration itself must clean up on the way up.
                await ScalarAsync(c, Board(seed.Main, "Cerberus ", spawnKey: null, enabled: false, updated: "now() - interval '1 day'"));
            }

            // Up again: the rules come back, and the last rollback row is gone.
            await MigrateAsync(connectionString, Switch);
            await using (var c = await OpenAsync(connectionString))
            {
                await AssertSchemaAsync(c, present: true);
                await AssertFilledLikeCSharpAsync(c);
                Assert.Equal(kept, await BoardIdsAsync(c));
                Assert.Equal(seed.TodCount + 1, await ScalarAsync(c, "SELECT count(*) FROM \"Tods\""));

                // Deleting a linkshell still takes its boards with it.
                await ScalarAsync(c, $"DELETE FROM \"Linkshells\" WHERE \"Id\" = {seed.Other}");
                Assert.Equal(0L, await ScalarAsync(c,
                    $"SELECT count(*) FROM \"HnmRecurringBoards\" WHERE \"LinkshellId\" = {seed.Other}"));
            }
        }
        finally
        {
            await ExecuteAsync(_pg.ConnectionString, $"DROP DATABASE IF EXISTS \"{databaseName}\" WITH (FORCE)");
        }
    }

    private sealed record Seed(int Main, int Other, List<int> Kept, long TodCount, long PendingCount);

    // Linkshells through EF (they did not change); ToDs, pending ToDs and boards by SQL, the way an
    // older build or Data Admin could have left them.
    private static async Task<Seed> SeedAsync(string connectionString)
    {
        int main, other;
        await using (var db = NewContext(connectionString))
        {
            var mainLinkshell = new Linkshell { LinkshellName = "Main LS" };
            var otherLinkshell = new Linkshell { LinkshellName = "Other LS" };
            db.Linkshells.AddRange(mainLinkshell, otherLinkshell);
            await db.SaveChangesAsync();
            main = mainLinkshell.Id;
            other = otherLinkshell.Id;
        }

        await using var c = await OpenAsync(connectionString);

        // Derived columns missing (NULL) or wrong, as a build that never stamped them leaves them.
        var tods = new (string? Monster, string? Cooldown, string? Interval, string? Key, int? Minutes)[]
        {
            ("Fafnir", "84 Hour", "1 Hour", null, null),
            ("Nidhogg", "84 Hour", "1 Hour", "nidhogg", 5040),
            ("Fafnir/Nidhogg", "84 Hour", null, "fafnir/nidhogg", 5040),
            (" Tiamat ", "84 Hour", "1 Hour", "wrong", 1),
            ("King Behemoth", "22 Hour", "10 Min", null, 1320),
            ("Cerberus", "72 Hour", "6 Hour", "cerberus", null),
            ("Khimaira", "48 Hour", null, "khimaira", 2880),
            ("", null, null, "stale", 60),
        };
        foreach (var (monster, cooldown, interval, key, minutes) in tods)
        {
            await ScalarAsync(c, Tod(main, monster, cooldown, interval, key, minutes));
            await ScalarAsync(c,
                "INSERT INTO \"PendingTodSubmissions\" (\"LinkshellId\", \"SubmittedAtUtc\", \"MonsterName\", \"Cooldown\", \"Interval\", \"Hq\", \"AdditionalSeconds\", \"SpawnKey\", \"CooldownMinutes\") " +
                $"VALUES ({main}, now(), {Text(monster)}, {Text(cooldown)}, {Text(interval)}, false, 0, {Text(key)}, {Number(minutes)})");
        }

        // Boards. Ranked per (linkshell, spawn): enabled first, then newest UpdatedAt, then newest id.
        var fafnirEnabled = await InsertAsync(c, Board(main, "Fafnir/Nidhogg", null, true, "now() - interval '3 days'"));
        await InsertAsync(c, Board(main, "fafnir/nidhogg", "fafnir/nidhogg", false, "now()"));
        await InsertAsync(c, Board(main, " Tiamat", "tiamat", true, "now() - interval '2 days'"));
        var tiamatNewer = await InsertAsync(c, Board(main, "tiamat", null, true, "now() - interval '1 day'"));
        await InsertAsync(c, Board(main, "Cerberus", "cerberus", true, "'2026-11-01T00:00:00Z'"));
        var cerberusNewerId = await InsertAsync(c, Board(main, "CERBERUS", "cerberus", true, "'2026-11-01T00:00:00Z'"));
        await InsertAsync(c, Board(main, "", null, true, "now()"));
        await InsertAsync(c, Board(main, "   ", "stale", true, "now()"));
        var slash = await InsertAsync(c, Board(main, "/", null, false, "now()"));
        var khimaira = await InsertAsync(c, Board(main, "Khimaira", "wrong", true, "now()"));
        var otherTiamat = await InsertAsync(c, Board(other, "Tiamat", null, true, "now()"));

        var kept = new List<int> { fafnirEnabled, tiamatNewer, cerberusNewerId, slash, khimaira, otherTiamat };
        kept.Sort();
        return new Seed(main, other, kept, tods.Length, tods.Length);
    }

    private static string Tod(int linkshellId, string? monster, string? cooldown, string? interval, string? spawnKey, int? cooldownMinutes) =>
        "INSERT INTO \"Tods\" (\"LinkshellId\", \"MonsterName\", \"Cooldown\", \"Interval\", \"Hq\", \"AdditionalSeconds\", \"Time\", \"SpawnKey\", \"CooldownMinutes\") " +
        $"VALUES ({linkshellId}, {Text(monster)}, {Text(cooldown)}, {Text(interval)}, false, 0, now(), {Text(spawnKey)}, {Number(cooldownMinutes)})";

    private static string Board(int linkshellId, string monster, string? spawnKey, bool enabled, string updated) =>
        "INSERT INTO \"HnmRecurringBoards\" (\"LinkshellId\", \"MonsterName\", \"SpawnKey\", \"Enabled\", \"LeadHours\", \"CreatedAt\", \"UpdatedAt\") " +
        $"VALUES ({linkshellId}, {Text(monster)}, {Text(spawnKey)}, {(enabled ? "true" : "false")}, 1, now(), {updated}) RETURNING \"Id\"";

    private static string Text(string? value) => value is null ? "NULL" : "'" + value.Replace("'", "''") + "'";

    private static string Number(int? value) => value?.ToString() ?? "NULL";

    private static async Task<int> InsertAsync(NpgsqlConnection c, string insert) =>
        (int)(await ScalarAsync(c, insert))!;

    private static async Task<List<int>> BoardIdsAsync(NpgsqlConnection c)
    {
        var ids = new List<int>();
        await using var command = c.CreateCommand();
        command.CommandText = "SELECT \"Id\" FROM \"HnmRecurringBoards\" ORDER BY \"Id\"";
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            ids.Add(reader.GetInt32(0));
        }
        return ids;
    }

    private static async Task<int> JormungandIdAsync(string connectionString)
    {
        await using var db = NewContext(connectionString);
        return await db.HnmRecurringBoards.Where(b => b.SpawnKey == "jormungand").Select(b => b.Id).SingleAsync();
    }

    // Every ToD, pending ToD and board holds what the C# derives from its text.
    private static async Task AssertFilledLikeCSharpAsync(NpgsqlConnection c)
    {
        foreach (var table in new[] { "Tods", "PendingTodSubmissions" })
        {
            await using var command = c.CreateCommand();
            command.CommandText =
                $"SELECT \"Id\", \"MonsterName\", \"Cooldown\", \"Interval\", \"SpawnKey\", \"CooldownMinutes\", \"IntervalMinutes\" FROM \"{table}\"";
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                var where = $"{table} {reader.GetInt32(0)}";
                Assert.True(HnmConfig.SpawnKey(Read(reader, 1)) == Read(reader, 4), $"{where}: SpawnKey");
                Assert.True(TodDurationFormat.StoredMinutes(Read(reader, 2), TodDurationFormat.HoursUnit) == ReadNumber(reader, 5), $"{where}: CooldownMinutes");
                Assert.True(TodDurationFormat.StoredMinutes(Read(reader, 3), TodDurationFormat.MinutesUnit) == ReadNumber(reader, 6), $"{where}: IntervalMinutes");
            }
        }

        await using (var command = c.CreateCommand())
        {
            command.CommandText = "SELECT \"MonsterName\", \"SpawnKey\" FROM \"HnmRecurringBoards\"";
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                Assert.Equal(HnmConfig.SpawnKey(reader.GetString(0)), Read(reader, 1));
            }
        }
    }

    private static string? Read(NpgsqlDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    private static int? ReadNumber(NpgsqlDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetInt32(ordinal);

    private static async Task AssertSchemaAsync(NpgsqlConnection c, bool present)
    {
        var required = (long)(await ScalarAsync(c,
            "SELECT count(*) FROM information_schema.columns " +
            "WHERE table_name = 'HnmRecurringBoards' AND column_name = 'SpawnKey' AND is_nullable = 'NO'"))!;
        Assert.True(required == (present ? 1L : 0L), $"HnmRecurringBoards.SpawnKey required={present}");

        var unique = (long)(await ScalarAsync(c,
            $"SELECT count(*) FROM pg_indexes WHERE indexname = '{SpawnIndex}' AND indexdef LIKE 'CREATE UNIQUE INDEX%'"))!;
        Assert.True(unique == (present ? 1L : 0L), $"{SpawnIndex} unique={present}");
        Assert.Equal(1L, await ScalarAsync(c, $"SELECT count(*) FROM pg_indexes WHERE indexname = '{SpawnIndex}'"));

        // No default sneaks in: a board with no monster must be refused, not given an empty key.
        Assert.Equal(0L, await ScalarAsync(c,
            "SELECT count(*) FROM information_schema.columns " +
            "WHERE table_name = 'HnmRecurringBoards' AND column_name = 'SpawnKey' AND column_default IS NOT NULL"));

        // The old name indexes stay either way.
        Assert.Equal(1L, await ScalarAsync(c,
            "SELECT count(*) FROM pg_indexes WHERE indexname = 'IX_HnmRecurringBoards_LinkshellId_MonsterName' AND indexdef LIKE 'CREATE UNIQUE INDEX%'"));
        Assert.Equal(true, await ScalarAsync(c, "SELECT to_regclass('public.\"IX_Tods_LinkshellId_MonsterName\"') IS NOT NULL"));
    }

    private static ApplicationDbContext NewContext(string connectionString)
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseNpgsql(connectionString)
            .UseApplicationServiceProvider(PostgresFixture.IdentityServices)
            .Options;
        return new ApplicationDbContext(options);
    }

    // Moves the schema of the given database to exactly this migration, forwards or backwards.
    private static async Task MigrateAsync(string connectionString, string targetMigration)
    {
        await using var db = NewContext(connectionString);
        await db.GetService<IMigrator>().MigrateAsync(targetMigration);
    }

    private static async Task<NpgsqlConnection> OpenAsync(string connectionString)
    {
        var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        return connection;
    }

    private static async Task<object?> ScalarAsync(NpgsqlConnection c, string sql)
    {
        await using var command = c.CreateCommand();
        command.CommandText = sql;
        return await command.ExecuteScalarAsync();
    }

    private static async Task ExecuteAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }
}
