using System;
using System.Threading.Tasks;
using LinkshellManagerDiscordApp.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using Xunit;

namespace LinkshellManager.PostgresTests;

// Phase 7 dropped the three Identity role tables. The fixture only proves the migration runs on an
// EMPTY database. This one checks a rollback too: Up on a database with a user, Down (the tables
// come back with their keys, foreign keys and unique index, so the old build works), and Up again
// with rows in the role tables.
//
// Its OWN database inside the test container, like Phase4BMigrationRoundTripTests, so stepping the
// schema backwards never breaks another test. It is deleted at the end.
[Collection("postgres")]
public class DropIdentityRoleTablesRoundTripTests
{
    // The migration just before the drop.
    private const string BeforeDrop = "20261113192840_EfCore10ModelSnapshot";

    // Named, not "latest", so a later migration's Down never joins this test.
    private const string Drop = "20261113201529_DropIdentityRoleTables";

    private static readonly string[] RoleTables = { "AspNetRoles", "AspNetUserRoles", "AspNetRoleClaims" };

    private readonly PostgresFixture _pg;

    public DropIdentityRoleTablesRoundTripTests(PostgresFixture pg) => _pg = pg;

    [Fact]
    public async Task DropIdentityRoleTables_GoesUpDownAndUpAgain()
    {
        var databaseName = "identity_roles_roundtrip_" + Guid.NewGuid().ToString("N")[..8];
        await ExecuteAsync(_pg.ConnectionString, $"CREATE DATABASE \"{databaseName}\"");

        var connectionString = new NpgsqlConnectionStringBuilder(_pg.ConnectionString)
        {
            Database = databaseName,
            // No pooled connections left behind, so DROP DATABASE at the end is not blocked.
            Pooling = false,
        }.ConnectionString;

        try
        {
            await MigrateAsync(connectionString, BeforeDrop);
            await using (var c = await OpenAsync(connectionString))
            {
                await ScalarAsync(c,
                    "INSERT INTO \"AspNetUsers\" (\"Id\", \"UserName\", \"EmailConfirmed\", \"PhoneNumberConfirmed\", " +
                    "\"TwoFactorEnabled\", \"LockoutEnabled\", \"AccessFailedCount\", \"IsSuperAdmin\", \"IsPlaceholder\") " +
                    "VALUES ('role-user', 'roleuser', false, false, false, false, 0, false, false)");
            }

            // Up: the role tables go, the user and the other Identity tables stay.
            await MigrateAsync(connectionString, Drop);
            await using (var c = await OpenAsync(connectionString))
            {
                await AssertRoleTablesGoneAsync(c);
                await AssertUserTablesKeptAsync(c);
            }

            // Down: the tables come back as InitialCreate made them.
            await MigrateAsync(connectionString, BeforeDrop);
            await using (var c = await OpenAsync(connectionString))
            {
                await AssertUserTablesKeptAsync(c);
                foreach (var table in RoleTables)
                {
                    Assert.True(await TableExistsAsync(c, table), $"{table} should be back after Down");
                }

                await ScalarAsync(c, "INSERT INTO \"AspNetRoles\" (\"Id\", \"Name\", \"NormalizedName\") VALUES ('r1', 'Admin', 'ADMIN')");
                await ScalarAsync(c, "INSERT INTO \"AspNetUserRoles\" (\"UserId\", \"RoleId\") VALUES ('role-user', 'r1')");
                await ScalarAsync(c, "INSERT INTO \"AspNetRoleClaims\" (\"RoleId\", \"ClaimType\", \"ClaimValue\") VALUES ('r1', 't', 'v')");

                // RoleNameIndex is unique again.
                await Assert.ThrowsAsync<PostgresException>(() =>
                    ScalarAsync(c, "INSERT INTO \"AspNetRoles\" (\"Id\", \"NormalizedName\") VALUES ('r2', 'ADMIN')"));
                // The foreign key to AspNetUsers is back.
                await Assert.ThrowsAsync<PostgresException>(() =>
                    ScalarAsync(c, "INSERT INTO \"AspNetUserRoles\" (\"UserId\", \"RoleId\") VALUES ('no-such-user', 'r1')"));
                // Deleting a role cascades to its user roles and claims, as before.
                await ScalarAsync(c, "DELETE FROM \"AspNetRoles\" WHERE \"Id\" = 'r1'");
                Assert.Equal(0L, await ScalarAsync(c, "SELECT count(*) FROM \"AspNetUserRoles\""));
                Assert.Equal(0L, await ScalarAsync(c, "SELECT count(*) FROM \"AspNetRoleClaims\""));

                // Leave rows behind so Up runs on tables that are not empty.
                await ScalarAsync(c, "INSERT INTO \"AspNetRoles\" (\"Id\", \"Name\", \"NormalizedName\") VALUES ('r3', 'Officer', 'OFFICER')");
                await ScalarAsync(c, "INSERT INTO \"AspNetUserRoles\" (\"UserId\", \"RoleId\") VALUES ('role-user', 'r3')");
                await ScalarAsync(c, "INSERT INTO \"AspNetRoleClaims\" (\"RoleId\", \"ClaimType\", \"ClaimValue\") VALUES ('r3', 't', 'v')");
            }

            // Up again: the drop still applies after a rollback.
            await MigrateAsync(connectionString, Drop);
            await using (var c = await OpenAsync(connectionString))
            {
                await AssertRoleTablesGoneAsync(c);
                await AssertUserTablesKeptAsync(c);
            }
        }
        finally
        {
            await ExecuteAsync(_pg.ConnectionString, $"DROP DATABASE IF EXISTS \"{databaseName}\" WITH (FORCE)");
        }
    }

    private static async Task AssertRoleTablesGoneAsync(NpgsqlConnection c)
    {
        foreach (var table in RoleTables)
        {
            Assert.False(await TableExistsAsync(c, table), $"{table} should be dropped");
        }
    }

    private static async Task AssertUserTablesKeptAsync(NpgsqlConnection c)
    {
        foreach (var table in new[] { "AspNetUsers", "AspNetUserClaims", "AspNetUserLogins", "AspNetUserTokens" })
        {
            Assert.True(await TableExistsAsync(c, table), $"{table} should be kept");
        }
        Assert.Equal(1L, await ScalarAsync(c, "SELECT count(*) FROM \"AspNetUsers\" WHERE \"Id\" = 'role-user'"));
    }

    // Moves the schema of the given database to exactly this migration, forwards or backwards.
    private static async Task MigrateAsync(string connectionString, string targetMigration)
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseNpgsql(connectionString)
            .UseApplicationServiceProvider(PostgresFixture.IdentityServices)
            .Options;
        await using var db = new ApplicationDbContext(options);
        await db.GetService<IMigrator>().MigrateAsync(targetMigration);
    }

    private static async Task<NpgsqlConnection> OpenAsync(string connectionString)
    {
        var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        return connection;
    }

    private static async Task<bool> TableExistsAsync(NpgsqlConnection c, string table) =>
        (bool)(await ScalarAsync(c, $"SELECT to_regclass('public.\"{table}\"') IS NOT NULL"))!;

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
