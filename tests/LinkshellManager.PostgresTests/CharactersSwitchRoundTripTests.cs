using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using LinkshellManagerDiscordApp.Data;
using LinkshellManagerDiscordApp.Migrations;
using LinkshellManagerDiscordApp.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using Xunit;

namespace LinkshellManager.PostgresTests;

// CharactersSwitch rebuilds Characters from the accounts and adds the one-name-per-account index.
//
// Seeded at AddCharacters with accounts copied by that migration, then changed the way an older
// build would change them without copying (a rename, a swap of main and alt, a cleared alt, a
// change of capitals, new jobs, a new account). Some accounts repeat a name across their own
// slots, others share a name with another account. Up must leave exactly what the app's C# rules
// say (CharacterSync.DesiredRows), keep the row id of a row that only changed capitals, and agree
// with the app's own copy (CharacterSync.SyncSql changes nothing afterwards). The index refuses a
// repeat inside one account and allows the same name on two accounts. Down drops the index and
// puts back the repeated slots; the guard then skips the index with a WARNING while a repeat
// exists; and Up again rebuilds and creates it.
//
// Its OWN database inside the test container, like DropRedundantJobRatingsIndexRoundTripTests.
// Deleted at the end.
[Collection("postgres")]
public class CharactersSwitchRoundTripTests
{
    private const string Before = "20261115074138_AddCharacters";

    // Named, not "latest", so a later migration's Down never joins this test.
    private const string Switch = "20261115092616_CharactersSwitch";

    private const string NameIndex = "IX_Characters_AppUserId_NormalizedName";

    private readonly PostgresFixture _pg;

    public CharactersSwitchRoundTripTests(PostgresFixture pg) => _pg = pg;

    private sealed record Account(string Id, bool Placeholder, string? Main, string? Alt1, string? Alt2);

    // As AddCharacters copied them.
    private static readonly Account[] Copied =
    {
        new("u-bob", false, "Bob", "Bobalt", null),
        new("u-bob2", false, "Bob", null, null),
        new("u-ph", true, "Bob", "Bobalt", null),
        new("u-twin", false, "Twin", "twin", "  TWIN\t"),
        new("u-alts", false, null, "Same", "same "),
        new("u-blank", false, "   ", " Nbsp　", ""),
        new("u-long", false, new string('L', 300), "Short", null),
        new("u-accent", false, "Ébène", null, null),
        new("u-swap", false, "Ann", "Bea", null),
        new("u-renamed", false, "Old", null, null),
        new("u-cleared", false, "Keep", "Gone", null),
        new("u-case", false, "case", null, null),
        new("u-jobs", false, "Jobs", null, null),
    };

    // What an older build then saved without copying, and one account it added.
    private static readonly Account[] Drifted = Copied
        .Select(a => a.Id switch
        {
            "u-swap" => a with { Main = "Bea", Alt1 = "Ann" },
            "u-renamed" => a with { Main = "New" },
            "u-cleared" => a with { Alt1 = null },
            "u-case" => a with { Main = "Case" },
            _ => a,
        })
        .Append(new Account("u-late", false, " Late ", "Late", null))
        .ToArray();

    [Fact]
    public async Task CharactersSwitch_RebuildsTheCopy_AddsTheNameIndex_AndGoesUpDownAndUpAgain()
    {
        var databaseName = "characters_switch_" + Guid.NewGuid().ToString("N")[..8];
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
            long caseRowId;
            await using (var c = await OpenAsync(connectionString))
            {
                foreach (var account in Copied)
                {
                    await ScalarAsync(c, PostgresFixture.InsertAccountSql(account.Id));
                    await SetNamesAsync(c, account);
                    if (account.Placeholder)
                    {
                        await ScalarAsync(c, $"UPDATE \"AspNetUsers\" SET \"IsPlaceholder\" = true WHERE \"Id\" = '{account.Id}'");
                    }
                }
                await ScalarAsync(c, "UPDATE \"AspNetUsers\" SET \"JobLevels\" = '[1]', \"Alt1JobLevels\" = '[10]', \"Alt1CraftLevels\" = '[5]' WHERE \"Id\" IN ('u-bob', 'u-jobs')");
                await ScalarAsync(c, AddCharacters.FillSql);
                // AddCharacters copied every name that fits, the repeats too.
                Assert.Equal(CopiedRowCount(Copied), (long)(await ScalarAsync(c, "SELECT count(*) FROM \"Characters\""))!);
                caseRowId = (long)(int)(await ScalarAsync(c, "SELECT \"Id\" FROM \"Characters\" WHERE \"AppUserId\" = 'u-case'"))!;

                // The older build's saves, with no copy.
                foreach (var account in Drifted.Where(a => !Copied.Contains(a)))
                {
                    if (account.Id == "u-late")
                    {
                        await ScalarAsync(c, PostgresFixture.InsertAccountSql(account.Id));
                    }
                    await SetNamesAsync(c, account);
                }
                await ScalarAsync(c, "UPDATE \"AspNetUsers\" SET \"JobLevels\" = '[2]' WHERE \"Id\" = 'u-jobs'");
            }

            // Up: the copy is exactly what the C# rules give, repeats left out, and the index exists.
            var notices = new List<string>();
            await using (var db = NewContext(connectionString))
            {
                var connection = (NpgsqlConnection)db.Database.GetDbConnection();
                connection.Notice += (_, e) => notices.Add(e.Notice.MessageText);
                await db.Database.OpenConnectionAsync();
                await db.GetService<IMigrator>().MigrateAsync(Switch);
            }
            // Removed: u-twin x2, u-alts x1 (repeats), u-swap x2 (renamed both ways), u-renamed,
            // u-cleared's alt. Written: u-swap x2, u-renamed, u-case (capitals), u-jobs, u-late.
            // Repeats now: u-twin x2, u-alts x1, u-late x1.
            Assert.Contains(
                "CharactersSwitch: rebuilt Characters from the accounts: removed 7 row(s), added or updated 6 row(s); " +
                "left out 4 name(s) that repeat an earlier name of the same account and 1 longer than 256 characters.",
                notices);
            Assert.Contains("CharactersSwitch: IX_Characters_AppUserId_NormalizedName is in place.", notices);

            await using (var c = await OpenAsync(connectionString))
            {
                Assert.True(await IndexIsUniqueAsync(c));
                Assert.Equal(Expected(Drifted), await RowsAsync(c));
                Assert.Equal(caseRowId, (long)(int)(await ScalarAsync(c, "SELECT \"Id\" FROM \"Characters\" WHERE \"AppUserId\" = 'u-case'"))!);
                Assert.Equal(true, await ScalarAsync(c,
                    "SELECT \"JobLevels\" = '[2]'::jsonb FROM \"Characters\" WHERE \"AppUserId\" = 'u-jobs' AND \"Slot\" = 0"));
                Assert.Equal(true, await ScalarAsync(c,
                    "SELECT \"JobLevels\" = '[10]'::jsonb AND \"CraftLevels\" = '[5]'::jsonb FROM \"Characters\" WHERE \"AppUserId\" = 'u-bob' AND \"Slot\" = 1"));

                // Running the rebuild again changes nothing, and the app's own copy agrees with it
                // row for row (UpdatedAt included).
                var before = await DumpAsync(c);
                var again = new List<string>();
                c.Notice += (_, e) => again.Add(e.Notice.MessageText);
                await ScalarAsync(c, CharactersSwitch.RebuildSql);
                Assert.Contains(
                    "CharactersSwitch: rebuilt Characters from the accounts: removed 0 row(s), added or updated 0 row(s); " +
                    "left out 4 name(s) that repeat an earlier name of the same account and 1 longer than 256 characters.",
                    again);
                await SyncAsync(c, Drifted.Select(a => a.Id).ToArray());
                Assert.Equal(before, await DumpAsync(c));

                // One row per name per account; two accounts may share a name.
                var error = await Assert.ThrowsAsync<PostgresException>(() => ScalarAsync(c, Character("u-bob", 2, "BOB")));
                Assert.Equal(PostgresErrorCodes.UniqueViolation, error.SqlState);
                Assert.Equal(NameIndex, error.ConstraintName);
                await ScalarAsync(c, Character("u-bob2", 1, "Bobalt"));
                await ScalarAsync(c, "DELETE FROM \"Characters\" WHERE \"AppUserId\" = 'u-bob2' AND \"Slot\" = 1");
            }

            // Down: the index goes and the repeated slots come back (the full copy the older build
            // keeps).
            var downNotices = new List<string>();
            await using (var db = NewContext(connectionString))
            {
                var connection = (NpgsqlConnection)db.Database.GetDbConnection();
                connection.Notice += (_, e) => downNotices.Add(e.Notice.MessageText);
                await db.Database.OpenConnectionAsync();
                await db.GetService<IMigrator>().MigrateAsync(Before);
            }
            Assert.Contains("CharactersSwitch (down): put back 4 character row(s) that repeat a name of the same account.", downNotices);
            await using (var c = await OpenAsync(connectionString))
            {
                Assert.False(await IndexExistsAsync(c));
                Assert.Equal(CopiedRowCount(Drifted), (long)(await ScalarAsync(c, "SELECT count(*) FROM \"Characters\""))!);

                // While an account repeats a name, the guard skips the index with a WARNING.
                var warnings = new List<string>();
                c.Notice += (_, e) => warnings.Add(e.Notice.Severity + ": " + e.Notice.MessageText);
                await ScalarAsync(c, CharactersSwitch.CreateNameIndexSql);
                Assert.Contains(warnings, w => w.StartsWith("WARNING: IX_Characters_AppUserId_NormalizedName not created", StringComparison.Ordinal));
                Assert.False(await IndexExistsAsync(c));

                // The older build renames someone again.
                await SetNamesAsync(c, new Account("u-bob2", false, "Robert", "Rob", null));
            }

            // Up again: rebuilt from the accounts as they are now, and the index is created.
            await MigrateAsync(connectionString, Switch);
            await using (var c = await OpenAsync(connectionString))
            {
                Assert.True(await IndexIsUniqueAsync(c));
                var now = Drifted.Select(a => a.Id == "u-bob2" ? a with { Main = "Robert", Alt1 = "Rob" } : a);
                Assert.Equal(Expected(now), await RowsAsync(c));
            }
        }
        finally
        {
            NpgsqlConnection.ClearAllPools();
            await ExecuteAsync(_pg.ConnectionString, $"DROP DATABASE IF EXISTS \"{databaseName}\" WITH (FORCE)");
        }
    }

    // The app's C# rules, account by account.
    private static List<(string, short, string, string)> Expected(IEnumerable<Account> accounts) =>
        accounts
            .OrderBy(a => a.Id, StringComparer.Ordinal)
            .SelectMany(a => CharacterSync.DesiredRows(new AppUser
                {
                    Id = a.Id, CharacterName = a.Main, AltCharacterName1 = a.Alt1, AltCharacterName2 = a.Alt2,
                })
                .Select(row => (a.Id, row.Slot, row.Name, row.NormalizedName)))
            .ToList();

    // Rows AddCharacters' rules give (no repeat rule): every non-blank name of 256 or fewer.
    private static long CopiedRowCount(IEnumerable<Account> accounts) => accounts
        .SelectMany(a => new[] { a.Main, a.Alt1, a.Alt2 })
        .Select(name => name?.Trim())
        .Count(name => !string.IsNullOrEmpty(name) && name.EnumerateRunes().Count() <= LinkshellManagerDiscordApp.Models.Character.MaxNameLength);

    private static async Task<List<(string, short, string, string)>> RowsAsync(NpgsqlConnection c)
    {
        var rows = new List<(string, short, string, string)>();
        await using var command = c.CreateCommand();
        command.CommandText = "SELECT \"AppUserId\", \"Slot\", \"Name\", \"NormalizedName\" FROM \"Characters\" ORDER BY \"AppUserId\" COLLATE \"C\", \"Slot\"";
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            rows.Add((reader.GetString(0), reader.GetInt16(1), reader.GetString(2), reader.GetString(3)));
        }
        return rows;
    }

    // Every column of every row, for "nothing changed".
    private static async Task<string> DumpAsync(NpgsqlConnection c) => (string)(await ScalarAsync(c,
        "SELECT coalesce(string_agg(row_to_json(t)::text, E'\\n' ORDER BY t.\"Id\"), '') FROM \"Characters\" t"))!;

    // The app's copy, exactly as ApplicationDbContext runs it.
    private static async Task SyncAsync(NpgsqlConnection c, string[] accountIds)
    {
        await using var command = c.CreateCommand();
        command.CommandText = CharacterSync.SyncSql;
        foreach (var parameter in CharacterSync.Parameters(accountIds, DateTime.UtcNow))
        {
            command.Parameters.Add(parameter);
        }
        await command.ExecuteNonQueryAsync();
    }

    private static async Task SetNamesAsync(NpgsqlConnection c, Account account)
    {
        await using var command = c.CreateCommand();
        command.CommandText =
            "UPDATE \"AspNetUsers\" SET \"CharacterName\" = @main, \"AltCharacterName1\" = @alt1, \"AltCharacterName2\" = @alt2 WHERE \"Id\" = @id";
        command.Parameters.AddWithValue("main", (object?)account.Main ?? DBNull.Value);
        command.Parameters.AddWithValue("alt1", (object?)account.Alt1 ?? DBNull.Value);
        command.Parameters.AddWithValue("alt2", (object?)account.Alt2 ?? DBNull.Value);
        command.Parameters.AddWithValue("id", account.Id);
        await command.ExecuteNonQueryAsync();
    }

    private static string Character(string account, int slot, string name) =>
        "INSERT INTO \"Characters\" (\"AppUserId\", \"Slot\", \"Name\", \"NormalizedName\", \"UpdatedAt\") " +
        $"VALUES ('{account}', {slot}, '{name}', lower('{name}'), now())";

    private static async Task<bool> IndexExistsAsync(NpgsqlConnection c) =>
        (long)(await ScalarAsync(c, $"SELECT count(*) FROM pg_indexes WHERE indexname = '{NameIndex}'"))! == 1;

    // Unique, on exactly (AppUserId, NormalizedName), with no filter: what the model says.
    private static async Task<bool> IndexIsUniqueAsync(NpgsqlConnection c) =>
        (string?)await ScalarAsync(c, $"SELECT indexdef FROM pg_indexes WHERE indexname = '{NameIndex}'")
        == $"CREATE UNIQUE INDEX \"{NameIndex}\" ON public.\"Characters\" USING btree (\"AppUserId\", \"NormalizedName\")";

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
