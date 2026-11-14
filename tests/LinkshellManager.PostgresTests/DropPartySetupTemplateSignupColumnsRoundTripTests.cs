using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using LinkshellManagerDiscordApp.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using Xunit;

namespace LinkshellManager.PostgresTests;

// Phase 9 dropped the six old template sign-up columns on PartySetupSlots. The fixture only proves
// the migration runs on an EMPTY database. This one runs it on a slot that still holds a sign-up
// (as production may), then Down (the columns come back, empty, and the slot survives) and Up again.
//
// Its OWN database inside the test container, like Phase4BMigrationRoundTripTests, so stepping the
// schema backwards never breaks another test. It is deleted at the end.
[Collection("postgres")]
public class DropPartySetupTemplateSignupColumnsRoundTripTests
{
    // The migration just before the drop.
    private const string BeforeDrop = "20261113201529_DropIdentityRoleTables";

    // Named, not "latest", so a later migration's Down never joins this test.
    private const string Drop = "20261114005510_DropPartySetupTemplateSignupColumns";

    private static readonly string[] SignupColumns =
    {
        "SignedUpAppUserId", "SignedUpCharacterName", "SignedUpAtUtc",
        "SignedUpRole", "SignedUpMainJob", "SignedUpSubJob",
    };

    private readonly PostgresFixture _pg;

    public DropPartySetupTemplateSignupColumnsRoundTripTests(PostgresFixture pg) => _pg = pg;

    [Fact]
    public async Task DropTemplateSignupColumns_GoesUpDownAndUpAgain_OnASlotWithASignup()
    {
        var databaseName = "signup_cols_roundtrip_" + Guid.NewGuid().ToString("N")[..8];
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

            int slotId;
            await using (var c = await OpenAsync(connectionString))
            {
                var linkshellId = await InsertAsync(c, "Linkshells", new() { ["LinkshellName"] = "Round Trip LS" });
                var setupId = await InsertAsync(c, "PartySetups", new()
                {
                    ["LinkshellId"] = linkshellId,
                    ["AssignedMonsterName"] = "Cerberus",
                });
                var allianceId = await InsertAsync(c, "PartySetupAlliances", new() { ["PartySetupId"] = setupId });
                var partyId = await InsertAsync(c, "PartySetupParties", new() { ["PartySetupAllianceId"] = allianceId });
                slotId = await InsertAsync(c, "PartySetupSlots", new()
                {
                    ["PartySetupPartyId"] = partyId,
                    ["RequirementType"] = "Job",
                    ["MainJob"] = "PLD",
                    ["SignedUpAppUserId"] = "old-user",
                    ["SignedUpCharacterName"] = "Oldname",
                    ["SignedUpAtUtc"] = DateTime.UtcNow,
                    ["SignedUpRole"] = "Tank",
                    ["SignedUpMainJob"] = "PLD",
                    ["SignedUpSubJob"] = "WAR",
                });
            }

            // Up: the columns go, the slot and its own columns stay.
            await MigrateAsync(connectionString, Drop);
            await using (var c = await OpenAsync(connectionString))
            {
                await AssertSlotKeptAsync(c, slotId);
                foreach (var column in SignupColumns)
                {
                    Assert.False(await ColumnExistsAsync(c, column), $"PartySetupSlots.{column} should be dropped");
                }
            }

            // Down: the columns come back, empty, on a table that has rows.
            await MigrateAsync(connectionString, BeforeDrop);
            await using (var c = await OpenAsync(connectionString))
            {
                await AssertSlotKeptAsync(c, slotId);
                foreach (var column in SignupColumns)
                {
                    Assert.True(await ColumnExistsAsync(c, column), $"PartySetupSlots.{column} should be back after Down");
                    Assert.Equal(DBNull.Value, await ScalarAsync(c,
                        $"SELECT \"{column}\" FROM \"PartySetupSlots\" WHERE \"Id\" = {slotId}"));
                }

                // The old build can write them again.
                await ScalarAsync(c,
                    $"UPDATE \"PartySetupSlots\" SET \"SignedUpAppUserId\" = 'again' WHERE \"Id\" = {slotId}");
            }

            // Up again: the drop still applies after a rollback.
            await MigrateAsync(connectionString, Drop);
            await using (var c = await OpenAsync(connectionString))
            {
                await AssertSlotKeptAsync(c, slotId);
                foreach (var column in SignupColumns)
                {
                    Assert.False(await ColumnExistsAsync(c, column), $"PartySetupSlots.{column} should be dropped");
                }
            }
        }
        finally
        {
            await ExecuteAsync(_pg.ConnectionString, $"DROP DATABASE IF EXISTS \"{databaseName}\" WITH (FORCE)");
        }
    }

    private static async Task AssertSlotKeptAsync(NpgsqlConnection c, int slotId)
    {
        Assert.Equal("PLD", await ScalarAsync(c,
            $"SELECT \"MainJob\" FROM \"PartySetupSlots\" WHERE \"Id\" = {slotId}"));
        Assert.Equal("Job", await ScalarAsync(c,
            $"SELECT \"RequirementType\" FROM \"PartySetupSlots\" WHERE \"Id\" = {slotId}"));
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

    // Inserts one row with raw SQL, because the C# model no longer has the dropped columns. Any NOT
    // NULL column the test does not name, and that has no database default, gets a plain fill-in
    // value for its type. Returns the new row's "Id".
    private static async Task<int> InsertAsync(NpgsqlConnection c, string table, Dictionary<string, object?> values)
    {
        var all = new Dictionary<string, object?>(values);

        await using (var lookup = c.CreateCommand())
        {
            lookup.CommandText =
                "SELECT column_name, data_type FROM information_schema.columns " +
                "WHERE table_schema = 'public' AND table_name = @table " +
                "AND is_nullable = 'NO' AND column_default IS NULL AND is_identity = 'NO'";
            lookup.Parameters.AddWithValue("table", table);
            await using var reader = await lookup.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                var column = reader.GetString(0);
                if (!all.ContainsKey(column))
                {
                    all[column] = FillInValue(table, column, reader.GetString(1));
                }
            }
        }

        await using var insert = c.CreateCommand();
        var columns = all.Keys.ToList();
        insert.CommandText =
            $"INSERT INTO \"{table}\" ({string.Join(", ", columns.Select(col => $"\"{col}\""))}) " +
            $"VALUES ({string.Join(", ", columns.Select((_, i) => $"@p{i}"))}) RETURNING \"Id\"";
        for (var i = 0; i < columns.Count; i++)
        {
            insert.Parameters.Add(new NpgsqlParameter($"p{i}", all[columns[i]] ?? DBNull.Value));
        }

        return (int)(await insert.ExecuteScalarAsync())!;
    }

    private static object FillInValue(string table, string column, string dataType) => dataType switch
    {
        "integer" or "smallint" => 0,
        "bigint" => 0L,
        "double precision" or "real" or "numeric" => 0.0,
        "boolean" => false,
        "text" or "character varying" or "character" => "x",
        "timestamp with time zone" => DateTime.UtcNow,
        _ => throw new InvalidOperationException(
            $"{table}.{column} is NOT NULL with type '{dataType}': add a fill-in value for that type."),
    };

    private static async Task<bool> ColumnExistsAsync(NpgsqlConnection c, string column)
    {
        await using var command = c.CreateCommand();
        command.CommandText =
            "SELECT EXISTS (SELECT 1 FROM information_schema.columns " +
            "WHERE table_schema = 'public' AND table_name = 'PartySetupSlots' AND column_name = @column)";
        command.Parameters.AddWithValue("column", column);
        return (bool)(await command.ExecuteScalarAsync())!;
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
