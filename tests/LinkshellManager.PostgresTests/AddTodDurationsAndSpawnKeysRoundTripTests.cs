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

// AddTodDurationsAndSpawnKeys adds CooldownMinutes / IntervalMinutes / SpawnKey to Tods and
// PendingTodSubmissions, and SpawnKey to HnmRecurringBoards, and fills them from the text.
//
// Seeded at the migration before with the shapes production holds (every Cooldown, Interval and
// monster name the pre-deploy check listed) and messy ones (bare numbers, padding, capitals, zero,
// overflow, words, blank, NULL, either half of a merge pair). Up must fill every row with exactly
// what the C# gives (the save hook's answer), leave nonsense NULL, and add the checks and indexes.
// Running the fill again changes nothing. Down drops it all and keeps every row; an old build then
// saves rows it cannot stamp, and Up again fills them.
//
// Its OWN database inside the test container, like DropRedundantJobRatingsIndexRoundTripTests.
// Deleted at the end.
[Collection("postgres")]
public class AddTodDurationsAndSpawnKeysRoundTripTests
{
    private const string Before = "20261115011931_AddMembershipRoleId";

    // Named, not "latest", so a later migration's Down never joins this test.
    private const string AddColumns = "20261115020908_AddTodDurationsAndSpawnKeys";

    private static readonly string[] Indexes =
    {
        "IX_Tods_LinkshellId_SpawnKey_Time",
        "IX_PendingTodSubmissions_LinkshellId_SpawnKey",
        "IX_HnmRecurringBoards_LinkshellId_SpawnKey",
    };

    private static readonly string[] Checks =
    {
        "CK_Tods_CooldownMinutes_Positive",
        "CK_Tods_IntervalMinutes_Positive",
        "CK_PendingTodSubmissions_CooldownMinutes_Positive",
        "CK_PendingTodSubmissions_IntervalMinutes_Positive",
    };

    // (MonsterName, Cooldown, Interval) for a ToD; the pending queue gets the same rows.
    private static readonly (string? Monster, string? Cooldown, string? Interval)[] TodRows =
    {
        ("Adamantoise", "22 Hour", "10 Min"),
        ("Adamantoise/Aspidochelone", "22 Hour", "10 Min"),
        ("Behemoth/King Behemoth", "22 Hour", "10 Min"),
        ("Cerberus", "48 Hour", "6 Hour"),
        ("Fafnir/Nidhogg", "22 Hour", "10 Min"),
        ("Khimaira", "48 Hour", "6 Hour"),
        ("Tiamat", "84 Hour", "1 Hour"),
        ("Tiamat", "72 Hour", null),
        (" Nidhogg ", "22", "10"),
        ("king behemoth", "1 Hour 30 Min", "5 min"),
        ("Foo / Bar", " 72 ", "1.5 h"),
        ("Aspidochelone", "2hrs", "0 Min"),
        ("/", "0", "abc"),
        ("   ", "99999999999 Hour", ""),
        (null, null, null),
    };

    private static readonly string[] BoardNames =
    {
        "Adamantoise/Aspidochelone", "Behemoth/King Behemoth", "Cerberus", "Fafnir/Nidhogg", "Hydra",
        "Jormungand", "Khimaira", "King Arthro", "Misc Monster", "Testing", "Tiamat", "Vrtra",
        " fafnir ", "Foo / Bar",
    };

    private readonly PostgresFixture _pg;

    public AddTodDurationsAndSpawnKeysRoundTripTests(PostgresFixture pg) => _pg = pg;

    [Fact]
    public async Task AddTodDurationsAndSpawnKeys_FillsLikeTheCSharp_AndGoesUpDownAndUpAgain()
    {
        var databaseName = "tod_derived_" + Guid.NewGuid().ToString("N")[..8];
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
            var (linkshellId, otherLinkshellId) = await SeedAsync(connectionString);

            // Up: every row holds exactly what the C# gives for its text.
            await MigrateAsync(connectionString, AddColumns);
            await using (var c = await OpenAsync(connectionString))
            {
                await AssertSchemaAsync(c, present: true);
                await AssertFilledLikeCSharpAsync(c);
                Assert.Equal(1320, await ScalarAsync(c,
                    "SELECT \"CooldownMinutes\" FROM \"Tods\" WHERE \"MonsterName\" = 'Fafnir/Nidhogg'"));
                Assert.Equal(3L, await ScalarAsync(c,
                    "SELECT count(*) FROM \"Tods\" WHERE \"SpawnKey\" = 'adamantoise/aspidochelone'"));
                Assert.Equal(0L, await ScalarAsync(c,
                    "SELECT count(*) FROM \"Tods\" WHERE \"MonsterName\" IS NOT NULL AND \"MonsterName\" ~ '[^ ]' AND \"SpawnKey\" IS NULL"));

                // Running the fill again changes nothing, and says so.
                var notices = new List<string>();
                c.Notice += (_, e) => notices.Add(e.Notice.MessageText);
                await ScalarAsync(c, AddTodDurationsAndSpawnKeys.FillSql);
                Assert.Contains("AddTodDurationsAndSpawnKeys: filled 0 ToD(s), 0 pending ToD(s), 0 standing board(s).", notices);

                // Zero or negative minutes are refused on both tables.
                foreach (var check in Checks)
                {
                    var table = check.Split('_')[1];
                    var column = check.Split('_')[2];
                    var refused = await Assert.ThrowsAsync<PostgresException>(() => ScalarAsync(c,
                        $"UPDATE \"{table}\" SET \"{column}\" = 0"));
                    Assert.Equal(PostgresErrorCodes.CheckViolation, refused.SqlState);
                    Assert.Equal(check, refused.ConstraintName);
                }

                // Two boards for one spawn are still allowed: the index is not unique until they are merged.
                Assert.Equal(2L, await ScalarAsync(c,
                    $"SELECT count(*) FROM \"HnmRecurringBoards\" WHERE \"LinkshellId\" = {linkshellId} AND \"SpawnKey\" = 'fafnir/nidhogg'"));
            }

            // Down: the columns, checks and indexes go; every row stays.
            await MigrateAsync(connectionString, Before);
            await using (var c = await OpenAsync(connectionString))
            {
                await AssertSchemaAsync(c, present: false);
                Assert.Equal((long)TodRows.Length, await ScalarAsync(c, "SELECT count(*) FROM \"Tods\""));
                Assert.Equal((long)TodRows.Length, await ScalarAsync(c, "SELECT count(*) FROM \"PendingTodSubmissions\""));
                Assert.Equal((long)BoardNames.Length + 1, await ScalarAsync(c, "SELECT count(*) FROM \"HnmRecurringBoards\""));

                // An old build running after a rollback saves rows it cannot stamp.
                await ScalarAsync(c, Tod(otherLinkshellId, "Nidhogg", "22 Hour", "10 Min"));
                await ScalarAsync(c, Pending(otherLinkshellId, "Behemoth", "21 Hour", "10 Min"));
                await ScalarAsync(c, Board(otherLinkshellId, "Aspidochelone"));
                await ScalarAsync(c, "UPDATE \"Tods\" SET \"Cooldown\" = '72 Hour', \"MonsterName\" = 'Tiamat' WHERE \"MonsterName\" = 'Cerberus'");
            }

            // Up again: everything is filled again, the rows the old build saved included.
            await MigrateAsync(connectionString, AddColumns);
            await using (var c = await OpenAsync(connectionString))
            {
                await AssertSchemaAsync(c, present: true);
                await AssertFilledLikeCSharpAsync(c);
                Assert.Equal("fafnir/nidhogg", await ScalarAsync(c,
                    $"SELECT \"SpawnKey\" FROM \"Tods\" WHERE \"LinkshellId\" = {otherLinkshellId}"));
                Assert.Equal(1260, await ScalarAsync(c,
                    $"SELECT \"CooldownMinutes\" FROM \"PendingTodSubmissions\" WHERE \"LinkshellId\" = {otherLinkshellId}"));
                Assert.Equal("adamantoise/aspidochelone", await ScalarAsync(c,
                    $"SELECT \"SpawnKey\" FROM \"HnmRecurringBoards\" WHERE \"LinkshellId\" = {otherLinkshellId} AND \"MonsterName\" = 'Aspidochelone'"));
                Assert.Equal(0L, await ScalarAsync(c,
                    "SELECT count(*) FROM \"Tods\" WHERE \"MonsterName\" = 'Tiamat' AND (\"SpawnKey\" <> 'tiamat' OR \"CooldownMinutes\" NOT IN (4320, 5040))"));
            }
        }
        finally
        {
            await ExecuteAsync(_pg.ConnectionString, $"DROP DATABASE IF EXISTS \"{databaseName}\" WITH (FORCE)");
        }
    }

    // Two linkshells through EF (they did not change); ToDs, pending ToDs and boards by SQL, the way
    // an old build would have saved them. The main linkshell also holds a second board for Fafnir's
    // spawn (" fafnir " next to "Fafnir/Nidhogg"), the case the later merge handles.
    private static async Task<(int Main, int Other)> SeedAsync(string connectionString)
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
        foreach (var (monster, cooldown, interval) in TodRows)
        {
            await ScalarAsync(c, Tod(main, monster, cooldown, interval));
            await ScalarAsync(c, Pending(main, monster, cooldown, interval));
        }
        foreach (var name in BoardNames)
        {
            await ScalarAsync(c, Board(main, name));
        }
        await ScalarAsync(c, Board(other, "Tiamat"));
        return (main, other);
    }

    private static string Tod(int linkshellId, string? monster, string? cooldown, string? interval) =>
        "INSERT INTO \"Tods\" (\"LinkshellId\", \"MonsterName\", \"Cooldown\", \"Interval\", \"Hq\", \"AdditionalSeconds\", \"Time\") " +
        $"VALUES ({linkshellId}, {SqlText(monster)}, {SqlText(cooldown)}, {SqlText(interval)}, false, 0, now())";

    private static string Pending(int linkshellId, string? monster, string? cooldown, string? interval) =>
        "INSERT INTO \"PendingTodSubmissions\" (\"LinkshellId\", \"SubmittedAtUtc\", \"MonsterName\", \"Cooldown\", \"Interval\", \"Hq\", \"AdditionalSeconds\") " +
        $"VALUES ({linkshellId}, now(), {SqlText(monster)}, {SqlText(cooldown)}, {SqlText(interval)}, false, 0)";

    private static string Board(int linkshellId, string monster) =>
        "INSERT INTO \"HnmRecurringBoards\" (\"LinkshellId\", \"MonsterName\", \"Enabled\", \"LeadHours\", \"CreatedAt\", \"UpdatedAt\") " +
        $"VALUES ({linkshellId}, {SqlText(monster)}, true, 1, now(), now())";

    private static string SqlText(string? value) =>
        value is null ? "NULL" : "'" + value.Replace("'", "''") + "'";

    // Every row in all three tables holds what TodDurationFormat.StoredMinutes and HnmConfig.SpawnKey
    // give for its text: the save hook's answer.
    private static async Task AssertFilledLikeCSharpAsync(NpgsqlConnection c)
    {
        foreach (var table in new[] { "Tods", "PendingTodSubmissions" })
        {
            await using var command = c.CreateCommand();
            command.CommandText =
                $"SELECT \"Id\", \"MonsterName\", \"Cooldown\", \"Interval\", \"SpawnKey\", \"CooldownMinutes\", \"IntervalMinutes\" FROM \"{table}\"";
            await using var reader = await command.ExecuteReaderAsync();
            var rows = 0;
            while (await reader.ReadAsync())
            {
                rows++;
                var monster = Text(reader, 1);
                var cooldown = Text(reader, 2);
                var interval = Text(reader, 3);
                var where = $"{table} {reader.GetInt32(0)} ({monster ?? "NULL"}, {cooldown ?? "NULL"}, {interval ?? "NULL"})";
                Assert.True(HnmConfig.SpawnKey(monster) == Text(reader, 4), $"{where}: SpawnKey {Text(reader, 4) ?? "NULL"}");
                Assert.True(TodDurationFormat.StoredMinutes(cooldown, TodDurationFormat.HoursUnit) == Number(reader, 5), $"{where}: CooldownMinutes");
                Assert.True(TodDurationFormat.StoredMinutes(interval, TodDurationFormat.MinutesUnit) == Number(reader, 6), $"{where}: IntervalMinutes");
            }
            Assert.True(rows > 0, $"{table} has rows");
        }

        await using (var command = c.CreateCommand())
        {
            command.CommandText = "SELECT \"MonsterName\", \"SpawnKey\" FROM \"HnmRecurringBoards\"";
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                Assert.Equal(HnmConfig.SpawnKey(reader.GetString(0)), Text(reader, 1));
            }
        }
    }

    private static string? Text(NpgsqlDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    private static int? Number(NpgsqlDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetInt32(ordinal);

    private static async Task AssertSchemaAsync(NpgsqlConnection c, bool present)
    {
        var columns = (long)(await ScalarAsync(c,
            "SELECT count(*) FROM information_schema.columns WHERE is_nullable = 'YES' AND (" +
            "(table_name IN ('Tods', 'PendingTodSubmissions') AND column_name IN ('CooldownMinutes', 'IntervalMinutes', 'SpawnKey')) " +
            "OR (table_name = 'HnmRecurringBoards' AND column_name = 'SpawnKey'))"))!;
        Assert.Equal(present ? 7L : 0L, columns);

        foreach (var index in Indexes)
        {
            var exists = (bool)(await ScalarAsync(c, $"SELECT to_regclass('public.\"{index}\"') IS NOT NULL"))!;
            Assert.True(exists == present, $"{index} present={present}");
        }
        foreach (var check in Checks)
        {
            var exists = (long)(await ScalarAsync(c, $"SELECT count(*) FROM pg_constraint WHERE conname = '{check}' AND contype = 'c'"))!;
            Assert.True(exists == (present ? 1L : 0L), $"{check} present={present}");
        }

        // EF swaps the plain LinkshellId index on the pending queue for the (LinkshellId, SpawnKey) one.
        var oldIndex = (bool)(await ScalarAsync(c, "SELECT to_regclass('public.\"IX_PendingTodSubmissions_LinkshellId\"') IS NOT NULL"))!;
        Assert.True(oldIndex == !present, $"IX_PendingTodSubmissions_LinkshellId present={!present}");

        // The old unique (LinkshellId, MonsterName) board index stays either way.
        Assert.True((bool)(await ScalarAsync(c, "SELECT to_regclass('public.\"IX_HnmRecurringBoards_LinkshellId_MonsterName\"') IS NOT NULL"))!);
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
