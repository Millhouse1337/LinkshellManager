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
using NpgsqlTypes;
using Xunit;

namespace LinkshellManager.PostgresTests;

// AddCharacters builds the Characters table from the accounts and makes the key-item tick's
// MembershipId optional.
//
// Seeded at the migration before with the accounts production has and the messy ones it could have:
// two accounts with the same name, a placeholder sharing a real player's name, a main equal to an
// alt of the same account, padding with tabs, no-break and ideographic spaces, a blank main, an
// empty alt, an account with no names at all, a 300-character name, a non-ASCII name, a name that is
// only a zero-width space (not whitespace to .NET), and alts with jobs. The rows the migration
// writes are exactly what the C# rules give (Trim, 256 characters, ToLowerInvariant), and exactly
// what the app's own copy (CharacterSync.SyncSql) gives: running it over every account changes no
// row. The new keys and checks are tested. Down drops the table, gives NULL-membership ticks their
// membership back (or deletes them) and makes the column required; an older build then adds an
// account, and Up again copies it.
//
// Its OWN database inside the test container, like DropRedundantJobRatingsIndexRoundTripTests.
// Deleted at the end.
[Collection("postgres")]
public class AddCharactersRoundTripTests
{
    private const string Before = "20261115064934_TodSwitch";

    // Named, not "latest", so a later migration's Down never joins this test.
    private const string Expand = "20261115074138_AddCharacters";

    private readonly PostgresFixture _pg;

    public AddCharactersRoundTripTests(PostgresFixture pg) => _pg = pg;

    // (account, placeholder?, main, alt 1, alt 2)
    private static readonly (string Id, bool Placeholder, string? Main, string? Alt1, string? Alt2)[] Accounts =
    {
        ("u-bob", false, "Bob", "Bobalt", null),
        ("u-bob2", false, "Bob", null, null),
        ("u-ph", true, "Bob", null, null),
        ("u-twin", false, "Twin", "Twin", "  twin\t"),
        ("u-blank", false, "   ", " Nbsp　", ""),
        ("u-none", false, null, null, null),
        ("u-long", false, new string('L', 300), "Short", null),
        ("u-accent", false, "Ébène", null, null),
        ("u-zw", false, "​", null, null),
        ("u-del", false, "Gone", null, null),
    };

    [Fact]
    public async Task AddCharacters_CopiesEveryAccount_AndGoesUpDownAndUpAgain()
    {
        var databaseName = "add_characters_" + Guid.NewGuid().ToString("N")[..8];
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

            // Up: one row per non-blank name that fits, as the C# rules say.
            var notices = new List<string>();
            await using (var db = NewContext(connectionString))
            {
                var connection = (NpgsqlConnection)db.Database.GetDbConnection();
                connection.Notice += (_, e) => notices.Add(e.Notice.MessageText);
                await db.Database.OpenConnectionAsync();
                await db.GetService<IMigrator>().MigrateAsync(Expand);
            }
            Assert.Contains("AddCharacters: copied 12 character name(s) from the accounts; left out 1 longer than 256 characters.", notices);

            await using (var c = await OpenAsync(connectionString))
            {
                await AssertSchemaAsync(c, present: true);
                Assert.Equal(Expected(Accounts), await RowsAsync(c));
                Assert.Equal(true, await ScalarAsync(c,
                    "SELECT \"JobLevels\" = '[75, 0]'::jsonb AND \"StrongJobs\" = '[1]'::jsonb AND \"MeritJobs\" = '[\"m\"]'::jsonb " +
                    "AND \"CraftLevels\" = '[100]'::jsonb FROM \"Characters\" WHERE \"AppUserId\" = 'u-bob' AND \"Slot\" = 0"));
                Assert.Equal(true, await ScalarAsync(c,
                    "SELECT \"JobLevels\" = '[10]'::jsonb AND \"StrongJobs\" = '[0, 1]'::jsonb AND \"MeritJobs\" = '[\"a\"]'::jsonb " +
                    "AND \"CraftLevels\" = '[5]'::jsonb FROM \"Characters\" WHERE \"AppUserId\" = 'u-bob' AND \"Slot\" = 1"));
                Assert.Equal(true, await ScalarAsync(c,
                    "SELECT \"JobLevels\" IS NULL AND \"CraftLevels\" IS NULL FROM \"Characters\" WHERE \"AppUserId\" = 'u-bob2'"));

                // Running the fill again adds nothing, and says so.
                var again = new List<string>();
                c.Notice += (_, e) => again.Add(e.Notice.MessageText);
                await ScalarAsync(c, AddCharacters.FillSql);
                Assert.Contains("AddCharacters: copied 0 character name(s) from the accounts; left out 1 longer than 256 characters.", again);

                // The app's own copy agrees with the migration row for row: over every account it
                // changes nothing, not even UpdatedAt.
                var before = await DumpAsync(c);
                Assert.Equal(0, await SyncAsync(c, Accounts.Select(a => a.Id).ToArray()));
                Assert.Equal(before, await DumpAsync(c));

                // The new keys and checks.
                await AssertRefusedAsync(c, Character("u-bob", 2, "   "), PostgresErrorCodes.CheckViolation);
                await AssertRefusedAsync(c, Character("u-bob", 2, " 　"), PostgresErrorCodes.CheckViolation);
                await AssertRefusedAsync(c, Character("u-bob", 3, "Fourth"), PostgresErrorCodes.CheckViolation);
                await AssertRefusedAsync(c, Character("u-bob", 0, "Again"), PostgresErrorCodes.UniqueViolation);
                await AssertRefusedAsync(c, Character("nobody", 0, "Ghost"), PostgresErrorCodes.ForeignKeyViolation);
                // Names are not unique: another account may use one (no name index yet).
                await ScalarAsync(c, Character("u-bob2", 1, "Bobalt"));
                await ScalarAsync(c, "DELETE FROM \"Characters\" WHERE \"AppUserId\" = 'u-bob2' AND \"Slot\" = 1");

                // A deleted account takes its characters with it.
                await ScalarAsync(c, "DELETE FROM \"AspNetUsers\" WHERE \"Id\" = 'u-del'");
                Assert.Equal(0L, await ScalarAsync(c, "SELECT count(*) FROM \"Characters\" WHERE \"AppUserId\" = 'u-del'"));

                // A tick may now have no membership id; every tick from before kept its id.
                Assert.Equal(2L, await ScalarAsync(c, "SELECT count(*) FROM \"ChartMemberKeyItems\" WHERE \"MembershipId\" IS NOT NULL"));
                await ScalarAsync(c, KeyItem(seed.Linkshell, null, "Restorable", "u-bob"));
                await ScalarAsync(c, KeyItem(seed.Linkshell, null, "NoMembership", "u-twin"));
            }

            // Down: the table goes; the tick whose account is a member gets that membership back,
            // the one whose account is not is deleted, and the column is required again.
            var downNotices = new List<string>();
            await using (var db = NewContext(connectionString))
            {
                var connection = (NpgsqlConnection)db.Database.GetDbConnection();
                connection.Notice += (_, e) => downNotices.Add(e.Notice.MessageText);
                await db.Database.OpenConnectionAsync();
                await db.GetService<IMigrator>().MigrateAsync(Before);
            }
            Assert.Contains("AddCharacters (down): gave 1 key-item tick(s) their membership id again; deleted 1 with no membership.", downNotices);
            await using (var c = await OpenAsync(connectionString))
            {
                await AssertSchemaAsync(c, present: false);
                Assert.Equal(seed.BobMembership, await ScalarAsync(c,
                    "SELECT \"MembershipId\" FROM \"ChartMemberKeyItems\" WHERE \"KeyItemName\" = 'Restorable'"));
                Assert.Equal(0L, await ScalarAsync(c,
                    "SELECT count(*) FROM \"ChartMemberKeyItems\" WHERE \"KeyItemName\" = 'NoMembership'"));
                Assert.Equal(3L, await ScalarAsync(c, "SELECT count(*) FROM \"ChartMemberKeyItems\""));

                // An older build adds an account and renames another.
                await ScalarAsync(c, PostgresFixture.InsertAccountSql("u-late"));
                await SetNamesAsync(c, "u-late", " Late ", null, null);
                await SetNamesAsync(c, "u-bob2", "Robert", null, null);
            }

            // Up again: the table is built again from the accounts as they are now.
            await MigrateAsync(connectionString, Expand);
            await using (var c = await OpenAsync(connectionString))
            {
                await AssertSchemaAsync(c, present: true);
                var now = Accounts
                    .Where(a => a.Id != "u-del")
                    .Select(a => a.Id == "u-bob2" ? a with { Main = "Robert" } : a)
                    .Append(("u-late", false, " Late ", null, null))
                    .ToArray();
                Assert.Equal(Expected(now), await RowsAsync(c));
            }
        }
        finally
        {
            NpgsqlConnection.ClearAllPools();
            await ExecuteAsync(_pg.ConnectionString, $"DROP DATABASE IF EXISTS \"{databaseName}\" WITH (FORCE)");
        }
    }

    private sealed record Seed(int Linkshell, int BobMembership);

    // Accounts by SQL (the EF model already has columns later migrations add), with names and jobs
    // set by parameters so tabs and non-ASCII characters arrive exactly. Two ticks: one on u-bob's
    // membership and one on a membership that is gone.
    private static async Task<Seed> SeedAsync(string connectionString)
    {
        await using var c = await OpenAsync(connectionString);
        foreach (var account in Accounts)
        {
            await ScalarAsync(c, PostgresFixture.InsertAccountSql(account.Id));
            await SetNamesAsync(c, account.Id, account.Main, account.Alt1, account.Alt2);
            if (account.Placeholder)
            {
                await ScalarAsync(c, $"UPDATE \"AspNetUsers\" SET \"IsPlaceholder\" = true WHERE \"Id\" = '{account.Id}'");
            }
        }
        await ScalarAsync(c,
            "UPDATE \"AspNetUsers\" SET \"JobLevels\" = '[75, 0]', \"StrongJobs\" = '[1]', \"MeritJobs\" = '[\"m\"]', \"CraftLevels\" = '[100]', " +
            "\"Alt1JobLevels\" = '[10]', \"Alt1StrongJobs\" = '[0, 1]', \"Alt1MeritJobs\" = '[\"a\"]', \"Alt1CraftLevels\" = '[5]' " +
            "WHERE \"Id\" = 'u-bob'");
        // Jobs on an alt slot that has no name are not copied (there is no row to carry them).
        await ScalarAsync(c, "UPDATE \"AspNetUsers\" SET \"Alt2JobLevels\" = '[1]' WHERE \"Id\" = 'u-bob2'");

        int linkshell;
        await using (var db = NewContext(connectionString))
        {
            var row = new Linkshell { LinkshellName = "LS" };
            db.Linkshells.Add(row);
            await db.SaveChangesAsync();
            linkshell = row.Id;
        }
        var bobMembership = (int)(await ScalarAsync(c,
            "INSERT INTO \"AppUserLinkshells\" (\"LinkshellId\", \"AppUserId\", \"Rank\", \"DiscordIdConfirmed\", " +
            "\"DkpPoolLedgerFromId\", \"DkpSeedLedgerId\", \"SeededDkpEarned\", \"SeededDkpSpent\") " +
            $"VALUES ({linkshell}, 'u-bob', 'Member', false, 0, 0, 0, 0) RETURNING \"Id\""))!;
        await ScalarAsync(c, KeyItem(linkshell, bobMembership, "Held", "u-bob"));
        await ScalarAsync(c, KeyItem(linkshell, 999999, "OldMembership", "u-bob2"));
        return new Seed(linkshell, bobMembership);
    }

    // What the app's C# says each account's rows are: .NET Trim, at most 256 characters (counted
    // as Postgres counts them), lower-cased with ToLowerInvariant.
    private static List<(string, short, string, string)> Expected(
        IEnumerable<(string Id, bool Placeholder, string? Main, string? Alt1, string? Alt2)> accounts)
    {
        var rows = new List<(string, short, string, string)>();
        foreach (var account in accounts.OrderBy(a => a.Id, StringComparer.Ordinal))
        {
            var names = new[] { account.Main, account.Alt1, account.Alt2 };
            for (short slot = 0; slot < 3; slot++)
            {
                var name = names[slot]?.Trim();
                if (!string.IsNullOrEmpty(name) && name.EnumerateRunes().Count() <= LinkshellManagerDiscordApp.Models.Character.MaxNameLength)
                {
                    rows.Add((account.Id, slot, name, name.ToLowerInvariant()));
                }
            }
        }
        return rows;
    }

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

    // The app's copy, exactly as ApplicationDbContext runs it. Returns the rows it changed.
    private static async Task<int> SyncAsync(NpgsqlConnection c, string[] accountIds)
    {
        await using var command = c.CreateCommand();
        command.CommandText = CharacterSync.SyncSql;
        command.Parameters.Add(new NpgsqlParameter("ids", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = accountIds });
        command.Parameters.Add(new NpgsqlParameter("now", NpgsqlDbType.TimestampTz) { Value = DateTime.UtcNow });
        return await command.ExecuteNonQueryAsync();
    }

    private static async Task SetNamesAsync(NpgsqlConnection c, string id, string? main, string? alt1, string? alt2)
    {
        await using var command = c.CreateCommand();
        command.CommandText =
            "UPDATE \"AspNetUsers\" SET \"CharacterName\" = @main, \"AltCharacterName1\" = @alt1, \"AltCharacterName2\" = @alt2 WHERE \"Id\" = @id";
        command.Parameters.AddWithValue("main", (object?)main ?? DBNull.Value);
        command.Parameters.AddWithValue("alt1", (object?)alt1 ?? DBNull.Value);
        command.Parameters.AddWithValue("alt2", (object?)alt2 ?? DBNull.Value);
        command.Parameters.AddWithValue("id", id);
        await command.ExecuteNonQueryAsync();
    }

    private static string Character(string account, int slot, string name) =>
        "INSERT INTO \"Characters\" (\"AppUserId\", \"Slot\", \"Name\", \"NormalizedName\", \"UpdatedAt\") " +
        $"VALUES ('{account}', {slot}, '{name}', lower('{name}'), now())";

    private static string KeyItem(int linkshellId, int? membershipId, string name, string appUserId) =>
        "INSERT INTO \"ChartMemberKeyItems\" (\"LinkshellId\", \"Board\", \"KeyItemName\", \"MembershipId\", \"AppUserId\", \"SetAt\") " +
        $"VALUES ({linkshellId}, 'Dynamis', '{name}', {membershipId?.ToString() ?? "NULL"}, '{appUserId}', now())";

    private static async Task AssertRefusedAsync(NpgsqlConnection c, string sql, string sqlState)
    {
        var error = await Assert.ThrowsAsync<PostgresException>(() => ScalarAsync(c, sql));
        Assert.Equal(sqlState, error.SqlState);
    }

    private static async Task AssertSchemaAsync(NpgsqlConnection c, bool present)
    {
        Assert.Equal(present, await ScalarAsync(c, "SELECT to_regclass('public.\"Characters\"') IS NOT NULL"));
        Assert.Equal(present ? "YES" : "NO", await ScalarAsync(c,
            "SELECT is_nullable FROM information_schema.columns WHERE table_name = 'ChartMemberKeyItems' AND column_name = 'MembershipId'"));
        if (!present)
        {
            return;
        }

        // CASCADE ('c'): a deleted account takes its characters.
        Assert.Equal(1L, await ScalarAsync(c,
            "SELECT count(*) FROM pg_constraint WHERE conname = 'FK_Characters_AspNetUsers_AppUserId' AND contype = 'f' AND confdeltype = 'c'"));
        Assert.Equal(2L, await ScalarAsync(c,
            "SELECT count(*) FROM pg_constraint WHERE conname IN ('CK_Characters_Slot', 'CK_Characters_Name_NotBlank') AND contype = 'c'"));
        Assert.Equal(1L, await ScalarAsync(c,
            "SELECT count(*) FROM pg_indexes WHERE indexname = 'IX_Characters_AppUserId_Slot' AND indexdef LIKE 'CREATE UNIQUE INDEX%'"));
        Assert.Equal(1L, await ScalarAsync(c,
            "SELECT count(*) FROM pg_indexes WHERE indexname = 'IX_Characters_NormalizedName' AND indexdef NOT LIKE 'CREATE UNIQUE INDEX%'"));
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
