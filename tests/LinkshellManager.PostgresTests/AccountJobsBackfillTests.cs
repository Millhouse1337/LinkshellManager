using LinkshellManagerDiscordApp.Data;
using LinkshellManagerDiscordApp.Migrations;
using LinkshellManagerDiscordApp.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using Xunit;

namespace LinkshellManager.PostgresTests;

// AccountJobsAndKeyItemAccounts copies the main character's jobs from the memberships onto the
// account (AspNetUsers JobLevels / StrongJobs / MeritJobs), and gives every key-item tick the
// account of its membership (ChartMemberKeyItems.AppUserId).
//
// Seeded at the migration before with every shape the memberships can hold: two memberships with
// different jobs and a primary linkshell (the primary wins, even when it is the older one); no
// primary (the newest wins); a primary whose levels are empty but whose strong flags are set (each
// column is picked on its own); a primary linkshell the account is not in; a JSON null; nothing at
// all; and a membership with no account. Key items: a normal tick, a tick whose membership is gone,
// one whose membership has no account, and one whose membership is in another linkshell -- the last
// three stay NULL. Running the fill again changes nothing. Down drops the columns and keeps every
// row; an old build then writes rows it cannot fill, and Up again fills them without touching a
// value an account already has.
//
// Its OWN database inside the test container, like DropRedundantJobRatingsIndexRoundTripTests.
// Deleted at the end.
[Collection("postgres")]
public class AccountJobsBackfillTests
{
    private const string Before = "20261115020908_AddTodDurationsAndSpawnKeys";

    // Named, not "latest", so a later migration's Down never joins this test.
    private const string AddColumns = "20261115031344_AccountJobsAndKeyItemAccounts";

    private const string KeyItemIndex = "IX_ChartMemberKeyItems_AppUserId";

    private readonly PostgresFixture _pg;

    public AccountJobsBackfillTests(PostgresFixture pg) => _pg = pg;

    [Fact]
    public async Task AccountJobsAndKeyItemAccounts_FillsPrimaryFirst_AndGoesUpDownAndUpAgain()
    {
        var databaseName = "account_jobs_" + Guid.NewGuid().ToString("N")[..8];
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

            // Up: every account gets the jobs the old profile screen showed; key items their account.
            await MigrateAsync(connectionString, AddColumns);
            await using (var c = await OpenAsync(connectionString))
            {
                await AssertSchemaAsync(c, present: true);
                await AssertAccountJobsAsync(c);
                await AssertKeyItemAccountsAsync(c, seed);

                // Running the fills again changes nothing, and says so.
                var notices = new List<string>();
                c.Notice += (_, e) => notices.Add(e.Notice.MessageText);
                await ScalarAsync(c, AccountJobsAndKeyItemAccounts.FillAccountJobsSql);
                await ScalarAsync(c, AccountJobsAndKeyItemAccounts.FillKeyItemAccountsSql);
                Assert.Contains("AccountJobsAndKeyItemAccounts: copied job levels to 0 account(s), strong jobs to 0, merit notes to 0.", notices);
                Assert.Contains("AccountJobsAndKeyItemAccounts: linked 0 key-item tick(s) to an account; 3 have no account.", notices);
                await AssertAccountJobsAsync(c);
                await AssertKeyItemAccountsAsync(c, seed);

                // The app reads the copied arrays back through EF as the same int[] / string[].
                await using var db = NewContext(connectionString);
                var primaryWins = await db.Users.AsNoTracking().SingleAsync(u => u.Id == "primary-wins");
                Assert.Equal(new[] { 0, 75, 37 }, primaryWins.JobLevels);
                Assert.Equal(new[] { 0, 1, 0 }, primaryWins.StrongJobs);
                Assert.Equal(new[] { "", "Axe x5" }, primaryWins.MeritJobs);
            }

            // Down: the columns and the index go; every account, membership and tick stays.
            await MigrateAsync(connectionString, Before);
            await using (var c = await OpenAsync(connectionString))
            {
                await AssertSchemaAsync(c, present: false);
                Assert.Equal(seed.Accounts, await ScalarAsync(c, "SELECT count(*) FROM \"AspNetUsers\""));
                Assert.Equal(seed.Memberships, await ScalarAsync(c, "SELECT count(*) FROM \"AppUserLinkshells\""));
                Assert.Equal((long)seed.Ticks.Count, await ScalarAsync(c, "SELECT count(*) FROM \"ChartMemberKeyItems\""));

                // An old build running after a rollback: a new account whose only jobs are on its
                // membership, and a new tick.
                await ScalarAsync(c, Account("after-rollback", null));
                var lateMember = (int)(await ScalarAsync(c,
                    Membership(seed.LinkshellA, "after-rollback", "'[0, 20]'", "NULL", "NULL") + " RETURNING \"Id\""))!;
                await ScalarAsync(c, KeyItem(seed.LinkshellA, lateMember, "Late"));
            }

            // Up again: everything is filled again, the rollback's rows included.
            await MigrateAsync(connectionString, AddColumns);
            await using (var c = await OpenAsync(connectionString))
            {
                await AssertSchemaAsync(c, present: true);
                await AssertAccountJobsAsync(c);
                await AssertKeyItemAccountsAsync(c, seed);
                Assert.Equal("[0, 20]", await ScalarAsync(c,
                    "SELECT \"JobLevels\"::text FROM \"AspNetUsers\" WHERE \"Id\" = 'after-rollback'"));
                Assert.Equal("after-rollback", await ScalarAsync(c,
                    "SELECT \"AppUserId\" FROM \"ChartMemberKeyItems\" WHERE \"KeyItemName\" = 'Late'"));

                // An account that already has a value keeps it, even when a membership now differs
                // (the fill only fills NULLs).
                await ScalarAsync(c, "UPDATE \"AppUserLinkshells\" SET \"JobLevels\" = '[9, 9]' WHERE \"AppUserId\" = 'no-primary'");
                await ScalarAsync(c, AccountJobsAndKeyItemAccounts.FillAccountJobsSql);
                Assert.Equal("[0, 30]", await ScalarAsync(c,
                    "SELECT \"JobLevels\"::text FROM \"AspNetUsers\" WHERE \"Id\" = 'no-primary'"));
            }
        }
        finally
        {
            await ExecuteAsync(_pg.ConnectionString, $"DROP DATABASE IF EXISTS \"{databaseName}\" WITH (FORCE)");
        }
    }

    // (account, expected JobLevels, StrongJobs, MeritJobs) as jsonb text after the fill.
    private static readonly (string Account, string? Levels, string? Strong, string? Merits)[] Expected =
    {
        // Two memberships, primary is the OLDER one: the primary still wins.
        ("primary-wins", "[0, 75, 37]", "[0, 1, 0]", "[\"\", \"Axe x5\"]"),
        // No primary: the newest membership with a value.
        ("no-primary", "[0, 30]", null, null),
        // Primary's levels are NULL (another membership's are picked) but its strong flags are set.
        ("primary-empty", "[0, 50]", "[0, 0, 1]", null),
        // Primary names a linkshell the account is not in: the newest membership.
        ("primary-elsewhere", "[0, 11]", null, null),
        // JSON null is not a job list; the real array on the older membership is picked.
        ("json-null", "[0, 5]", null, null),
        // Nothing anywhere stays NULL.
        ("all-null", null, null, null),
        // No membership at all.
        ("no-membership", null, null, null),
    };

    private sealed record Seed(
        int LinkshellA,
        int LinkshellB,
        long Accounts,
        long Memberships,
        Dictionary<string, string?> Ticks);

    // Linkshells through EF (they did not change); accounts, memberships and ticks by SQL, because
    // this schema has none of the new columns yet and the EF model already does.
    private static async Task<Seed> SeedAsync(string connectionString)
    {
        int a, b, c3;
        await using (var db = NewContext(connectionString))
        {
            var lsA = new Linkshell { LinkshellName = "LS A" };
            var lsB = new Linkshell { LinkshellName = "LS B" };
            var lsC = new Linkshell { LinkshellName = "LS C" };
            db.Linkshells.AddRange(lsA, lsB, lsC);
            await db.SaveChangesAsync();
            a = lsA.Id;
            b = lsB.Id;
            c3 = lsC.Id;
        }

        await using var c = await OpenAsync(connectionString);

        await ScalarAsync(c, Account("primary-wins", a));
        await ScalarAsync(c, Account("no-primary", null));
        await ScalarAsync(c, Account("primary-empty", b));
        await ScalarAsync(c, Account("primary-elsewhere", c3));
        await ScalarAsync(c, Account("json-null", a));
        await ScalarAsync(c, Account("all-null", a));
        await ScalarAsync(c, Account("no-membership", a));

        // Primary (A) first, so it has the LOWER id; B is newer and must still lose.
        var primaryWinsA = await InsertMembershipAsync(c, a, "primary-wins", "'[0, 75, 37]'", "'[0, 1, 0]'", "'[\"\", \"Axe x5\"]'");
        await InsertMembershipAsync(c, b, "primary-wins", "'[0, 10, 10]'", "'[0, 0, 0]'", "'[\"\", \"other\"]'");

        await InsertMembershipAsync(c, a, "no-primary", "'[0, 1]'", "NULL", "NULL");
        await InsertMembershipAsync(c, b, "no-primary", "'[0, 30]'", "NULL", "NULL");

        await InsertMembershipAsync(c, a, "primary-empty", "'[0, 50]'", "'[0, 1, 0]'", "NULL");
        await InsertMembershipAsync(c, b, "primary-empty", "NULL", "'[0, 0, 1]'", "NULL");

        await InsertMembershipAsync(c, a, "primary-elsewhere", "'[0, 2]'", "NULL", "NULL");
        await InsertMembershipAsync(c, b, "primary-elsewhere", "'[0, 11]'", "NULL", "NULL");

        await InsertMembershipAsync(c, b, "json-null", "'[0, 5]'", "NULL", "NULL");
        await InsertMembershipAsync(c, a, "json-null", "'null'", "'null'", "'null'");

        await InsertMembershipAsync(c, a, "all-null", "NULL", "NULL", "NULL");
        await InsertMembershipAsync(c, b, "all-null", "NULL", "NULL", "NULL");

        // A sheet-only row with jobs and no account: copied nowhere.
        var noAccount = await InsertMembershipAsync(c, a, null, "'[0, 60]'", "'[0, 1]'", "'[\"x\"]'");

        // Key items: tick name -> expected account.
        var ticks = new Dictionary<string, string?>
        {
            ["Normal"] = "primary-wins",
            ["Gone"] = null,
            ["NoAccount"] = null,
            ["OtherLinkshell"] = null,
        };
        await ScalarAsync(c, KeyItem(a, primaryWinsA, "Normal"));
        await ScalarAsync(c, KeyItem(a, 999999, "Gone"));
        await ScalarAsync(c, KeyItem(a, noAccount, "NoAccount"));
        // Membership primaryWinsA is in linkshell A, so a tick in linkshell C must not take it.
        await ScalarAsync(c, KeyItem(c3, primaryWinsA, "OtherLinkshell"));

        var accounts = (long)(await ScalarAsync(c, "SELECT count(*) FROM \"AspNetUsers\""))!;
        var memberships = (long)(await ScalarAsync(c, "SELECT count(*) FROM \"AppUserLinkshells\""))!;
        return new Seed(a, b, accounts, memberships, ticks);
    }

    private static async Task<int> InsertMembershipAsync(
        NpgsqlConnection c, int linkshellId, string? appUserId, string levels, string strong, string merits) =>
        (int)(await ScalarAsync(c, Membership(linkshellId, appUserId, levels, strong, merits) + " RETURNING \"Id\""))!;

    private static string Account(string id, int? primaryLinkshellId) =>
        "INSERT INTO \"AspNetUsers\" (\"Id\", \"UserName\", \"EmailConfirmed\", \"PhoneNumberConfirmed\", " +
        "\"TwoFactorEnabled\", \"LockoutEnabled\", \"AccessFailedCount\", \"IsSuperAdmin\", \"IsPlaceholder\", \"PrimaryLinkshellId\") " +
        $"VALUES ('{id}', '{id}', false, false, false, false, 0, false, false, {primaryLinkshellId?.ToString() ?? "NULL"})";

    private static string Membership(int linkshellId, string? appUserId, string levels, string strong, string merits) =>
        "INSERT INTO \"AppUserLinkshells\" (\"LinkshellId\", \"AppUserId\", \"Rank\", \"DiscordIdConfirmed\", " +
        "\"DkpPoolLedgerFromId\", \"DkpSeedLedgerId\", \"SeededDkpEarned\", \"SeededDkpSpent\", " +
        "\"JobLevels\", \"StrongJobs\", \"MeritJobs\") " +
        $"VALUES ({linkshellId}, {(appUserId is null ? "NULL" : $"'{appUserId}'")}, 'Member', false, 0, 0, 0, 0, " +
        $"{levels}, {strong}, {merits})";

    private static string KeyItem(int linkshellId, int membershipId, string name) =>
        "INSERT INTO \"ChartMemberKeyItems\" (\"LinkshellId\", \"Board\", \"KeyItemName\", \"MembershipId\", \"SetAt\") " +
        $"VALUES ({linkshellId}, 'Dynamis', '{name}', {membershipId}, now())";

    private static async Task AssertAccountJobsAsync(NpgsqlConnection c)
    {
        foreach (var (account, levels, strong, merits) in Expected)
        {
            Assert.Equal(levels, await JsonOrNullAsync(c, account, "JobLevels"));
            Assert.Equal(strong, await JsonOrNullAsync(c, account, "StrongJobs"));
            Assert.Equal(merits, await JsonOrNullAsync(c, account, "MeritJobs"));
        }
    }

    private static async Task<string?> JsonOrNullAsync(NpgsqlConnection c, string account, string column)
    {
        var value = await ScalarAsync(c, $"SELECT \"{column}\"::text FROM \"AspNetUsers\" WHERE \"Id\" = '{account}'");
        return value is DBNull ? null : (string?)value;
    }

    private static async Task AssertKeyItemAccountsAsync(NpgsqlConnection c, Seed seed)
    {
        foreach (var (name, account) in seed.Ticks)
        {
            var value = await ScalarAsync(c, $"SELECT \"AppUserId\" FROM \"ChartMemberKeyItems\" WHERE \"KeyItemName\" = '{name}'");
            Assert.True(Equals(value is DBNull ? null : value, account), $"Tick {name}: expected {account ?? "NULL"}, got {value}.");
        }
    }

    private static async Task AssertSchemaAsync(NpgsqlConnection c, bool present)
    {
        foreach (var column in new[] { "JobLevels", "StrongJobs", "MeritJobs" })
        {
            var found = (long)(await ScalarAsync(c,
                "SELECT count(*) FROM information_schema.columns " +
                $"WHERE table_name = 'AspNetUsers' AND column_name = '{column}' AND data_type = 'jsonb' AND is_nullable = 'YES'"))!;
            Assert.True(found == (present ? 1L : 0L), $"AspNetUsers.{column} present={present}");
        }

        var keyItemColumn = (long)(await ScalarAsync(c,
            "SELECT count(*) FROM information_schema.columns " +
            "WHERE table_name = 'ChartMemberKeyItems' AND column_name = 'AppUserId' " +
            "AND data_type = 'character varying' AND character_maximum_length = 450 AND is_nullable = 'YES'"))!;
        Assert.True(keyItemColumn == (present ? 1L : 0L), $"ChartMemberKeyItems.AppUserId present={present}");

        var index = (bool)(await ScalarAsync(c, $"SELECT to_regclass('public.\"{KeyItemIndex}\"') IS NOT NULL"))!;
        Assert.True(index == present, $"{KeyItemIndex} present={present}");
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
