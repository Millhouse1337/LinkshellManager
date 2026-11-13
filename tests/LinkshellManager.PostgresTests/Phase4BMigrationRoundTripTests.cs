using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using LinkshellManagerDiscordApp.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using NpgsqlTypes;
using Xunit;

namespace LinkshellManager.PostgresTests;

// Phase 4B dropped seven tables and many columns in four migrations. The fixture only proves they
// run on an EMPTY database. Production has rows, and a rollback (Down) has to put NOT NULL columns
// back on tables that already have rows. This test does both on a database that looks like
// production before 4B: rows in the tables 4B touches, then Up, Down and Up again.
//
// It uses its OWN database inside the test container (not the shared one), so stepping the schema
// backwards can never break another test, and it is deleted at the end.
[Collection("postgres")]
public class Phase4BMigrationRoundTripTests
{
    // The newest migration before Phase 4B (the last one at commit 826f769).
    private const string BeforePhase4B = "20261112092602_OneReversalPerTreasuryEntry";

    // The last Phase 4B migration. Named, not "latest", so a later migration's Down never joins
    // this test.
    private const string LastPhase4B = "20261113043005_DropDeadLedgerLinkshellColumns";

    private readonly PostgresFixture _pg;

    public Phase4BMigrationRoundTripTests(PostgresFixture pg) => _pg = pg;

    [Fact]
    public async Task Phase4BMigrations_GoUpDownAndUpAgain_OnTablesWithRows()
    {
        var databaseName = "phase4b_roundtrip_" + Guid.NewGuid().ToString("N")[..8];
        await ExecuteAsync(_pg.ConnectionString, $"CREATE DATABASE \"{databaseName}\"");

        var connectionString = new NpgsqlConnectionStringBuilder(_pg.ConnectionString)
        {
            Database = databaseName,
            // No pooled connections left behind, so DROP DATABASE at the end is not blocked.
            Pooling = false,
        }.ConnectionString;

        try
        {
            await MigrateAsync(connectionString, BeforePhase4B);

            Seeded rows;
            await using (var connection = new NpgsqlConnection(connectionString))
            {
                await connection.OpenAsync();
                rows = await SeedPreviousSchemaAsync(connection);
            }

            // Up: the drops run on tables that have rows, and the rows survive.
            await MigrateAsync(connectionString, LastPhase4B);
            await using (var connection = new NpgsqlConnection(connectionString))
            {
                await connection.OpenAsync();
                await AssertKeptRowsSurviveAsync(connection, rows);
                await AssertDroppedAsync(connection);
                // The old Verify row is deleted by step 2; the normal history row stays.
                Assert.False(await RowExistsAsync(connection, "AppUserEventStatusLedgers", rows.VerifyLedgerId));
            }

            // Down: every table and column comes back on tables that have rows. NOT NULL columns
            // get their Down default, and the data fix-ups in Down ran.
            await MigrateAsync(connectionString, BeforePhase4B);
            await using (var connection = new NpgsqlConnection(connectionString))
            {
                await connection.OpenAsync();
                await AssertKeptRowsSurviveAsync(connection, rows);

                foreach (var table in DroppedTables)
                {
                    Assert.True(await TableExistsAsync(connection, table), $"{table} should be back after Down");
                }

                // Old code only shows 'Active' linkshells, so Down sets it on every row.
                Assert.Equal("Active", await ScalarAsync(connection,
                    $"SELECT \"Status\" FROM \"Linkshells\" WHERE \"Id\" = {rows.LinkshellId}"));
                // Values come back empty (only the pg_dump has the old data), not with the old values.
                Assert.Equal(DBNull.Value, await ScalarAsync(connection,
                    $"SELECT \"GoogleOAuthUserEmail\" FROM \"Linkshells\" WHERE \"Id\" = {rows.LinkshellId}"));
                Assert.Equal(true, await ScalarAsync(connection,
                    $"SELECT \"IsPostable\" FROM \"LedgerAccounts\" WHERE \"Id\" = {rows.LedgerAccountId}"));
            }

            // Up again: the same drops still apply after a rollback.
            await MigrateAsync(connectionString, LastPhase4B);
            await using (var connection = new NpgsqlConnection(connectionString))
            {
                await connection.OpenAsync();
                await AssertKeptRowsSurviveAsync(connection, rows);
                await AssertDroppedAsync(connection);
            }
        }
        finally
        {
            await ExecuteAsync(_pg.ConnectionString, $"DROP DATABASE IF EXISTS \"{databaseName}\" WITH (FORCE)");
        }
    }

    private static readonly string[] DroppedTables =
    {
        "ChartBossProgresses", "Notifications", "LinkshellDiscordWebhooks", "LinkshellDiscordChannels",
        "RevenueEntries", "LedgerPeriods", "TodLootDetails",
    };

    // A few of the dropped columns, one or more from each 4B step.
    private static readonly (string Table, string Column)[] DroppedColumns =
    {
        ("Items", "RevenueEntryId"),
        ("JournalEntries", "LegacyRevenueEntryId"),
        ("Events", "CreationSource"),
        ("AppUserEventHistories", "IsQuickJoin"),
        ("AppUserEventStatusLedgers", "AppUserId"),
        ("AspNetUsers", "ProfileImage"),
        ("Linkshells", "GoogleOAuthUserEmail"),
        ("Linkshells", "Status"),
        ("LedgerAccounts", "SortOrder"),
        ("Rules", "LinkshellName"),
        ("Tods", "TotalTods"),
    };

    private sealed record Seeded(
        int LinkshellId, string AppUserId, int EventId, int EventHistoryId, int AppUserEventId,
        int AppUserEventHistoryId, int JoinLedgerId, int VerifyLedgerId, int LedgerAccountId,
        int JournalEntryId, int JournalEntryLineId, int ItemId, int RuleId, int TodId);

    // Rows a production database had before 4B, including values in the columns 4B drops.
    private static async Task<Seeded> SeedPreviousSchemaAsync(NpgsqlConnection c)
    {
        var linkshellId = (int)await InsertAsync(c, "Linkshells", new()
        {
            ["LinkshellName"] = "Round Trip LS",
            ["Status"] = "Active",
            ["TodMonsterTimings"] = "",
            ["GoogleOAuthUserEmail"] = "old@example.com",
            ["GoogleOAuthRefreshTokenEnc"] = "old-token",
            ["GoogleSheetAppCreated"] = true,
        });
        var appUserId = (string)await InsertAsync(c, "AspNetUsers", new()
        {
            ["Id"] = "round-trip-user",
            ["UserName"] = "roundtrip",
            ["ProfileImage"] = new byte[] { 1, 2, 3 },
            ["PrimaryLinkshellName"] = "Round Trip LS",
        });

        var eventId = (int)await InsertAsync(c, "Events", new()
        {
            ["LinkshellId"] = linkshellId,
            ["EventName"] = "Sky",
            ["CreationSource"] = "Addon",
            ["EventDkp"] = 5.0,
        });
        var eventHistoryId = (int)await InsertAsync(c, "EventHistories", new()
        {
            ["LinkshellId"] = linkshellId,
            ["EventName"] = "Sky",
            ["EventDkp"] = 5.0,
            ["Details"] = "old details",
        });
        var appUserEventId = (int)await InsertAsync(c, "AppUserEvents", new()
        {
            ["EventId"] = eventId,
            ["AppUserId"] = appUserId,
            ["Proctor"] = "Officer",
        });
        var appUserEventHistoryId = (int)await InsertAsync(c, "AppUserEventHistories", new()
        {
            ["EventHistoryId"] = eventHistoryId,
            ["AppUserId"] = appUserId,
            ["IsQuickJoin"] = true,
            ["Proctor"] = "Officer",
        });
        var joinLedgerId = (int)await InsertAsync(c, "AppUserEventStatusLedgers", new()
        {
            ["EventId"] = eventId,
            ["AppUserEventId"] = appUserEventId,
            ["AppUserId"] = appUserId,
            ["ActionType"] = "Join",
        });
        var verifyLedgerId = (int)await InsertAsync(c, "AppUserEventStatusLedgers", new()
        {
            ["EventId"] = eventId,
            ["AppUserEventId"] = appUserEventId,
            ["AppUserId"] = appUserId,
            ["ActionType"] = "Verify",
            ["Source"] = "Addon",
        });

        var ledgerAccountId = (int)await InsertAsync(c, "LedgerAccounts", new()
        {
            ["LinkshellId"] = linkshellId,
            ["AccountNumber"] = 1000,
            ["IsCash"] = true,
            ["IsPostable"] = false,
            ["IsSystem"] = true,
            ["SortOrder"] = 3,
        });
        var revenueEntryId = (int)await InsertAsync(c, "RevenueEntries", new()
        {
            ["LinkshellId"] = linkshellId,
        });
        var journalEntryId = (int)await InsertAsync(c, "JournalEntries", new()
        {
            ["LinkshellId"] = linkshellId,
            ["EntryNumber"] = 1,
            ["Sequence"] = 1,
            ["Kind"] = "Normal",
            ["LegacyRevenueEntryId"] = revenueEntryId,
        });
        var journalEntryLineId = (int)await InsertAsync(c, "JournalEntryLines", new()
        {
            ["JournalEntryId"] = journalEntryId,
            ["LedgerAccountId"] = ledgerAccountId,
            ["LineNumber"] = 1,
            ["LineMemo"] = "old memo",
        });
        var itemId = (int)await InsertAsync(c, "Items", new()
        {
            ["LinkshellId"] = linkshellId,
            ["LinkshellName"] = "Round Trip LS",
            ["RevenueEntryId"] = revenueEntryId,
        });
        var ruleId = (int)await InsertAsync(c, "Rules", new()
        {
            ["LinkshellId"] = linkshellId,
            ["LinkshellName"] = "Round Trip LS",
        });
        var todId = (int)await InsertAsync(c, "Tods", new()
        {
            ["LinkshellId"] = linkshellId,
            ["TotalClaims"] = 1,
            ["TotalTods"] = 1,
        });
        await InsertAsync(c, "Notifications", new() { ["AppUserId"] = appUserId });

        return new Seeded(linkshellId, appUserId, eventId, eventHistoryId, appUserEventId,
            appUserEventHistoryId, joinLedgerId, verifyLedgerId, ledgerAccountId, journalEntryId,
            journalEntryLineId, itemId, ruleId, todId);
    }

    private static async Task AssertKeptRowsSurviveAsync(NpgsqlConnection c, Seeded rows)
    {
        Assert.True(await RowExistsAsync(c, "Linkshells", rows.LinkshellId));
        Assert.True(await RowExistsAsync(c, "AspNetUsers", rows.AppUserId));
        Assert.True(await RowExistsAsync(c, "Events", rows.EventId));
        Assert.True(await RowExistsAsync(c, "EventHistories", rows.EventHistoryId));
        Assert.True(await RowExistsAsync(c, "AppUserEvents", rows.AppUserEventId));
        Assert.True(await RowExistsAsync(c, "AppUserEventHistories", rows.AppUserEventHistoryId));
        Assert.True(await RowExistsAsync(c, "AppUserEventStatusLedgers", rows.JoinLedgerId));
        Assert.True(await RowExistsAsync(c, "LedgerAccounts", rows.LedgerAccountId));
        Assert.True(await RowExistsAsync(c, "JournalEntries", rows.JournalEntryId));
        Assert.True(await RowExistsAsync(c, "JournalEntryLines", rows.JournalEntryLineId));
        Assert.True(await RowExistsAsync(c, "Items", rows.ItemId));
        Assert.True(await RowExistsAsync(c, "Rules", rows.RuleId));
        Assert.True(await RowExistsAsync(c, "Tods", rows.TodId));
        Assert.Equal("Round Trip LS", await ScalarAsync(c,
            $"SELECT \"LinkshellName\" FROM \"Linkshells\" WHERE \"Id\" = {rows.LinkshellId}"));
    }

    private static async Task AssertDroppedAsync(NpgsqlConnection c)
    {
        foreach (var table in DroppedTables)
        {
            Assert.False(await TableExistsAsync(c, table), $"{table} should be dropped");
        }

        foreach (var (table, column) in DroppedColumns)
        {
            Assert.False(await ColumnExistsAsync(c, table, column), $"{table}.{column} should be dropped");
        }
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

    // Inserts one row with raw SQL, because the C# model no longer has the dropped columns. Any
    // NOT NULL column the test does not name, and that has no database default, gets a plain
    // fill-in value for its type, so the test does not have to list every column of every table.
    // Returns the new row's "Id".
    private static async Task<object> InsertAsync(NpgsqlConnection c, string table, Dictionary<string, object?> values)
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
            insert.Parameters.Add(all[columns[i]] is NpgsqlParameter typed
                ? WithName(typed, $"p{i}")
                : new NpgsqlParameter($"p{i}", all[columns[i]] ?? DBNull.Value));
        }

        return (await insert.ExecuteScalarAsync())!;
    }

    private static NpgsqlParameter WithName(NpgsqlParameter parameter, string name)
    {
        parameter.ParameterName = name;
        return parameter;
    }

    private static object FillInValue(string table, string column, string dataType) => dataType switch
    {
        "integer" or "smallint" => 0,
        "bigint" => 0L,
        "double precision" or "real" or "numeric" => 0.0,
        "boolean" => false,
        "text" or "character varying" or "character" => "x",
        "timestamp with time zone" => DateTime.UtcNow,
        "timestamp without time zone" => DateTime.SpecifyKind(DateTime.UtcNow, DateTimeKind.Unspecified),
        "date" => DateOnly.FromDateTime(DateTime.UtcNow),
        "bytea" => Array.Empty<byte>(),
        "uuid" => Guid.NewGuid(),
        "interval" => TimeSpan.Zero,
        "jsonb" => new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Jsonb, Value = "[]" },
        _ => throw new InvalidOperationException(
            $"{table}.{column} is NOT NULL with type '{dataType}': add a fill-in value for that type."),
    };

    private static async Task<bool> RowExistsAsync(NpgsqlConnection c, string table, object id)
    {
        await using var command = c.CreateCommand();
        command.CommandText = $"SELECT EXISTS (SELECT 1 FROM \"{table}\" WHERE \"Id\" = @id)";
        command.Parameters.AddWithValue("id", id);
        return (bool)(await command.ExecuteScalarAsync())!;
    }

    private static async Task<bool> TableExistsAsync(NpgsqlConnection c, string table) =>
        (bool)(await ScalarAsync(c, $"SELECT to_regclass('public.\"{table}\"') IS NOT NULL"))!;

    private static async Task<bool> ColumnExistsAsync(NpgsqlConnection c, string table, string column)
    {
        await using var command = c.CreateCommand();
        command.CommandText =
            "SELECT EXISTS (SELECT 1 FROM information_schema.columns " +
            "WHERE table_schema = 'public' AND table_name = @table AND column_name = @column)";
        command.Parameters.AddWithValue("table", table);
        command.Parameters.AddWithValue("column", column);
        return (bool)(await command.ExecuteScalarAsync())!;
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
