using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Npgsql;
using Xunit;

namespace LinkshellManager.PostgresTests;

// Phase 9 added CHECK constraints on five status/type columns. They were added NOT VALID (so an old
// production row could not fail the deploy) and validated later (NormalizeNamesForSquash), and the
// squash's Baseline creates them already validated. This checks, on the shared Baseline-built
// database, that all five are there and validated, that good values save and bad ones are refused.
//
// (Before the squash this test also ran the old migration over an old bad row, then Down and Up.
// Those steps needed the old migrations, which are now only in git history.)
[Collection("postgres")]
public class ClosedListCheckConstraintTests : IAsyncLifetime
{
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

    public Task InitializeAsync() => _pg.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task ClosedListChecks_AreValidated_AndRefuseNewBadValues()
    {
        await using var c = await OpenAsync(_pg.ConnectionString);
        foreach (var (table, name) in Checks)
        {
            Assert.Equal(true, await ScalarAsync(c,
                $"SELECT convalidated FROM pg_constraint WHERE conname = '{name}' AND conrelid = '\"{table}\"'::regclass"));
        }

        var linkshellId = await InsertAsync(c, "Linkshells", new() { ["HnmAttendanceMode"] = "Standard" });

        // Good values save, bad ones are refused.
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
    }

    private static async Task AssertRefusedAsync(Func<Task> action)
    {
        var ex = await Assert.ThrowsAsync<PostgresException>(action);
        Assert.Equal(PostgresErrorCodes.CheckViolation, ex.SqlState);
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
}
