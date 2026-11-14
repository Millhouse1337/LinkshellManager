using System;
using System.Threading.Tasks;
using LinkshellManagerDiscordApp.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using Xunit;

namespace LinkshellManager.PostgresTests;

// AddJoinRequestDetails: four nullable columns on Invites, duplicate open join requests marked
// cancelled, then the one-open-request index. Up, Down and Up again on a table with rows; no row
// is lost either way, and the plain LinkshellId index stays throughout.
//
// Its OWN database inside the test container, like DropRedundantJobRatingsIndexRoundTripTests.
[Collection("postgres")]
public class AddJoinRequestDetailsRoundTripTests
{
    private const string Before = "20261114010957_BackfillLiveCardLootLinkshell";

    // Named, not "latest", so a later migration's Down never joins this test.
    private const string After = "20261114155541_AddJoinRequestDetails";

    private const string OpenRequestIndex = "IX_Invites_OneOpenJoinRequest";
    private const string LinkshellIndex = "IX_Invites_LinkshellId";

    private readonly PostgresFixture _pg;

    public AddJoinRequestDetailsRoundTripTests(PostgresFixture pg) => _pg = pg;

    [Fact]
    public async Task AddJoinRequestDetails_GoesUpDownAndUpAgain()
    {
        var databaseName = "join_request_roundtrip_" + Guid.NewGuid().ToString("N")[..8];
        await ExecuteAsync(_pg.ConnectionString, $"CREATE DATABASE \"{databaseName}\"");

        var connectionString = new NpgsqlConnectionStringBuilder(_pg.ConnectionString)
        {
            Database = databaseName,
            Pooling = false,
        }.ConnectionString;

        try
        {
            await MigrateAsync(connectionString, Before);
            await using (var c = await OpenAsync(connectionString))
            {
                // No users or linkshells in this database: switch the foreign keys off for this
                // session only, so the rows can point at ids that are not there.
                await ScalarAsync(c, "SET session_replication_role = replica");
                await ScalarAsync(c,
                    "INSERT INTO \"Invites\" (\"AppUserId\", \"LinkshellId\", \"Status\") VALUES " +
                    "('u1', 1, 'PendingJoinRequest'), ('u1', 1, 'PendingJoinRequest'), ('u1', 1, 'PendingInvite')");
            }

            // Up: the second open request is cancelled, nothing is deleted, the index refuses a new one.
            await MigrateAsync(connectionString, After);
            await using (var c = await OpenAsync(connectionString))
            {
                Assert.True(await IndexExistsAsync(c, OpenRequestIndex));
                Assert.True(await IndexExistsAsync(c, LinkshellIndex));
                Assert.Equal(3L, await ScalarAsync(c, "SELECT count(*) FROM \"Invites\""));
                Assert.Equal(1L, await ScalarAsync(c, "SELECT count(*) FROM \"Invites\" WHERE \"Status\" = 'PendingJoinRequest'"));
                Assert.Equal(1L, await ScalarAsync(c,
                    "SELECT count(*) FROM \"Invites\" WHERE \"Status\" = 'JoinRequestCancelled' AND \"ResolvedAtUtc\" IS NOT NULL"));
                Assert.Equal(0L, await ScalarAsync(c, "SELECT count(*) FROM \"Invites\" WHERE \"Message\" IS NOT NULL"));

                await ScalarAsync(c, "SET session_replication_role = replica");
                await Assert.ThrowsAsync<PostgresException>(() => ScalarAsync(c,
                    "INSERT INTO \"Invites\" (\"AppUserId\", \"LinkshellId\", \"Status\") VALUES ('u1', 1, 'PendingJoinRequest')"));
            }

            // Down: the columns and the index go; every row stays.
            await MigrateAsync(connectionString, Before);
            await using (var c = await OpenAsync(connectionString))
            {
                Assert.False(await IndexExistsAsync(c, OpenRequestIndex));
                Assert.True(await IndexExistsAsync(c, LinkshellIndex));
                Assert.Equal(3L, await ScalarAsync(c, "SELECT count(*) FROM \"Invites\""));
                Assert.Equal(0L, await ScalarAsync(c,
                    "SELECT count(*) FROM information_schema.columns WHERE table_name = 'Invites' " +
                    "AND column_name IN ('Message', 'CreatedAtUtc', 'ResolvedAtUtc', 'ResolvedByAppUserId')"));
            }

            // Up again.
            await MigrateAsync(connectionString, After);
            await using (var c = await OpenAsync(connectionString))
            {
                Assert.True(await IndexExistsAsync(c, OpenRequestIndex));
                Assert.Equal(3L, await ScalarAsync(c, "SELECT count(*) FROM \"Invites\""));
                Assert.Equal(1L, await ScalarAsync(c, "SELECT count(*) FROM \"Invites\" WHERE \"Status\" = 'PendingJoinRequest'"));
            }
        }
        finally
        {
            await ExecuteAsync(_pg.ConnectionString, $"DROP DATABASE IF EXISTS \"{databaseName}\" WITH (FORCE)");
        }
    }

    private static async Task<bool> IndexExistsAsync(NpgsqlConnection c, string index) =>
        (bool)(await ScalarAsync(c, $"SELECT to_regclass('public.\"{index}\"') IS NOT NULL"))!;

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
