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

// Phase 9 added CHECK constraints on five status/type columns, NOT VALID so an old row with a value
// outside the list cannot fail the deploy. This proves that on a database with such a row: the
// migration applies, the old row is still there, new bad values are refused, VALIDATE refuses while
// the bad row exists and passes once it is fixed, and Down / Up again work.
//
// Its OWN database inside the test container, like Phase4BMigrationRoundTripTests. Deleted at the end.
[Collection("postgres")]
public class ClosedListCheckConstraintTests
{
    // The migration just before the constraints.
    private const string Before = "20261114010044_DropRedundantJobRatingsIndex";

    // Named, not "latest", so a later migration's Down never joins this test.
    private const string AddChecks = "20261114010442_AddClosedListCheckConstraints";

    private static readonly (string Table, string Name)[] Checks =
    {
        ("WindowEvents", "CK_WindowEvents_EntryType"),
        ("WindowEvents", "CK_WindowEvents_Status"),
        ("Linkshells", "CK_Linkshells_HnmAttendanceMode"),
        ("AttendanceSnapshots", "CK_AttendanceSnapshots_SlotKind"),
        ("AttendanceSnapshots", "CK_AttendanceSnapshots_SnapshotStatus"),
    };

    private readonly PostgresFixture _pg;

    public ClosedListCheckConstraintTests(PostgresFixture pg) => _pg = pg;

    [Fact]
    public async Task ClosedListChecks_ApplyOverAnOldBadRow_AndRefuseNewOnes()
    {
        var databaseName = "closed_list_checks_" + Guid.NewGuid().ToString("N")[..8];
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

            int linkshellId, oldBadLinkshellId;
            await using (var c = await OpenAsync(connectionString))
            {
                linkshellId = await InsertAsync(c, "Linkshells", new() { ["HnmAttendanceMode"] = "Standard" });
                // A value nothing writes today, as an old production row might hold.
                oldBadLinkshellId = await InsertAsync(c, "Linkshells", new() { ["HnmAttendanceMode"] = "OldMode" });
            }

            // Up: applies although a row breaks the rule, and the row is untouched.
            await MigrateAsync(connectionString, AddChecks);
            await using (var c = await OpenAsync(connectionString))
            {
                foreach (var (table, name) in Checks)
                {
                    Assert.Equal(false, await ScalarAsync(c,
                        $"SELECT convalidated FROM pg_constraint WHERE conname = '{name}'"));
                }
                Assert.Equal("OldMode", await ScalarAsync(c,
                    $"SELECT \"HnmAttendanceMode\" FROM \"Linkshells\" WHERE \"Id\" = {oldBadLinkshellId}"));

                // Good values still save, bad ones are refused.
                await InsertAsync(c, "WindowEvents", new()
                {
                    ["LinkshellId"] = linkshellId, ["Status"] = "Closed", ["EntryType"] = null,
                });
                await InsertAsync(c, "WindowEvents", new()
                {
                    ["LinkshellId"] = linkshellId, ["EntryType"] = "Kings Camp",
                });
                await InsertAsync(c, "AttendanceSnapshots", new()
                {
                    ["LinkshellId"] = linkshellId, ["SnapshotStatus"] = "Pending", ["SlotKind"] = "Misc",
                });
                await AssertRefusedAsync(() => InsertAsync(c, "WindowEvents", new()
                {
                    ["LinkshellId"] = linkshellId, ["Status"] = "Done",
                }));
                await AssertRefusedAsync(() => InsertAsync(c, "WindowEvents", new()
                {
                    ["LinkshellId"] = linkshellId, ["EntryType"] = "Sky",
                }));
                await AssertRefusedAsync(() => InsertAsync(c, "AttendanceSnapshots", new()
                {
                    ["LinkshellId"] = linkshellId, ["SnapshotStatus"] = "Duplicate",
                }));
                await AssertRefusedAsync(() => InsertAsync(c, "AttendanceSnapshots", new()
                {
                    ["LinkshellId"] = linkshellId, ["SlotKind"] = "Bonus",
                }));
                await AssertRefusedAsync(() => ScalarAsync(c,
                    $"UPDATE \"Linkshells\" SET \"HnmAttendanceMode\" = 'Manual' WHERE \"Id\" = {linkshellId}"));

                // The owner's later step: VALIDATE refuses while the old row is wrong...
                await AssertRefusedAsync(() => ScalarAsync(c,
                    "ALTER TABLE \"Linkshells\" VALIDATE CONSTRAINT \"CK_Linkshells_HnmAttendanceMode\""));
                // ...and passes once it is fixed.
                await ScalarAsync(c,
                    $"UPDATE \"Linkshells\" SET \"HnmAttendanceMode\" = 'Standard' WHERE \"Id\" = {oldBadLinkshellId}");
                foreach (var (table, name) in Checks)
                {
                    await ScalarAsync(c, $"ALTER TABLE \"{table}\" VALIDATE CONSTRAINT \"{name}\"");
                }
            }

            // Down: the constraints go.
            await MigrateAsync(connectionString, Before);
            await using (var c = await OpenAsync(connectionString))
            {
                Assert.Equal(0L, await ScalarAsync(c,
                    $"SELECT count(*) FROM pg_constraint WHERE conname IN ({string.Join(", ", Checks.Select(k => $"'{k.Name}'"))})"));
                await ScalarAsync(c,
                    $"UPDATE \"Linkshells\" SET \"HnmAttendanceMode\" = 'OldMode' WHERE \"Id\" = {oldBadLinkshellId}");
            }

            // Up again, over the bad row once more.
            await MigrateAsync(connectionString, AddChecks);
            await using (var c = await OpenAsync(connectionString))
            {
                Assert.Equal((long)Checks.Length, await ScalarAsync(c,
                    $"SELECT count(*) FROM pg_constraint WHERE conname IN ({string.Join(", ", Checks.Select(k => $"'{k.Name}'"))})"));
            }
        }
        finally
        {
            await ExecuteAsync(_pg.ConnectionString, $"DROP DATABASE IF EXISTS \"{databaseName}\" WITH (FORCE)");
        }
    }

    private static async Task AssertRefusedAsync(Func<Task> action)
    {
        var ex = await Assert.ThrowsAsync<PostgresException>(action);
        Assert.Equal(PostgresErrorCodes.CheckViolation, ex.SqlState);
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

    // Inserts one row with raw SQL. Any NOT NULL column the test does not name, and that has no
    // database default, gets a plain fill-in value for its type. Returns the new row's "Id".
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
