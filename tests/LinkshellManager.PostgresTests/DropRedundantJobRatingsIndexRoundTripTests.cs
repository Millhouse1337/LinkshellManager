using System;
using System.Threading.Tasks;
using LinkshellManagerDiscordApp.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using Xunit;

namespace LinkshellManager.PostgresTests;

// Phase 9 dropped IX_JobRatings_LinkshellId_TargetAppUserId: the unique rating index starts with
// the same two columns, so it already answers that lookup. Up, Down and Up again on a table with a
// row, and the unique index keeps refusing a duplicate rating throughout.
//
// Its OWN database inside the test container, like Phase4BMigrationRoundTripTests. Deleted at the end.
[Collection("postgres")]
public class DropRedundantJobRatingsIndexRoundTripTests
{
    // The migration just before the drop.
    private const string BeforeDrop = "20261114005510_DropPartySetupTemplateSignupColumns";

    // Named, not "latest", so a later migration's Down never joins this test.
    private const string Drop = "20261114010044_DropRedundantJobRatingsIndex";

    private const string DroppedIndex = "IX_JobRatings_LinkshellId_TargetAppUserId";

    private const string InsertRating =
        "INSERT INTO \"JobRatings\" (\"LinkshellId\", \"CharacterSlot\", \"TargetAppUserId\", \"RaterAppUserId\", " +
        "\"JobIndex\", \"Gear\", \"Skill\", \"HasRelic\", \"UpdatedAt\") " +
        "VALUES (1, 0, 'target', 'rater', 3, 4, 5, false, now())";

    private readonly PostgresFixture _pg;

    public DropRedundantJobRatingsIndexRoundTripTests(PostgresFixture pg) => _pg = pg;

    [Fact]
    public async Task DropRedundantJobRatingsIndex_GoesUpDownAndUpAgain()
    {
        var databaseName = "jobratings_index_roundtrip_" + Guid.NewGuid().ToString("N")[..8];
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
                Assert.True(await IndexExistsAsync(c));
                await ScalarAsync(c, InsertRating);
            }

            // Up: the index goes, the row stays, a duplicate rating is still refused.
            await MigrateAsync(connectionString, Drop);
            await using (var c = await OpenAsync(connectionString))
            {
                Assert.False(await IndexExistsAsync(c));
                await AssertOneRatingAndNoDuplicatesAsync(c);
            }

            // Down: the index comes back on a table that has rows.
            await MigrateAsync(connectionString, BeforeDrop);
            await using (var c = await OpenAsync(connectionString))
            {
                Assert.True(await IndexExistsAsync(c));
                await AssertOneRatingAndNoDuplicatesAsync(c);
            }

            // Up again: the drop still applies after a rollback.
            await MigrateAsync(connectionString, Drop);
            await using (var c = await OpenAsync(connectionString))
            {
                Assert.False(await IndexExistsAsync(c));
                await AssertOneRatingAndNoDuplicatesAsync(c);
            }
        }
        finally
        {
            await ExecuteAsync(_pg.ConnectionString, $"DROP DATABASE IF EXISTS \"{databaseName}\" WITH (FORCE)");
        }
    }

    private static async Task AssertOneRatingAndNoDuplicatesAsync(NpgsqlConnection c)
    {
        Assert.Equal(1L, await ScalarAsync(c, "SELECT count(*) FROM \"JobRatings\""));
        await Assert.ThrowsAsync<PostgresException>(() => ScalarAsync(c, InsertRating));
    }

    private static async Task<bool> IndexExistsAsync(NpgsqlConnection c) =>
        (bool)(await ScalarAsync(c, $"SELECT to_regclass('public.\"{DroppedIndex}\"') IS NOT NULL"))!;

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
