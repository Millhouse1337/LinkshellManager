using System.Data.Common;
using Microsoft.EntityFrameworkCore;

namespace LinkshellManagerDiscordApp.Data;

// Read-only checks run once at start-up, right after Database.Migrate. Each problem found becomes
// one Warning line starting "Migration health:". Nothing is changed and start-up never stops here.
//
// What it looks for (all in the app's schema):
// - a constraint still NOT VALID (added without checking the old rows, never validated);
// - an index Postgres marks invalid (a failed CREATE INDEX CONCURRENTLY leaves one behind);
// - a guarded index that is missing. A guarded migration skips a unique index with RAISE WARNING
//   instead of failing start-up when old rows would break it, so the index can be silently absent.
public static class MigrationHealth
{
    // The guarded indexes that exist today. Add a name here only in the deploy whose migration
    // creates it (for example IX_LinkshellMonsterTimings_LinkshellId_SpawnKey in Round 5), so a
    // healthy database never warns.
    public static readonly IReadOnlyList<string> GuardedIndexes =
    [
        "IX_EventAttendanceWindows_OneClosePerEvent",
        "IX_EventAttendanceWindows_OneClosePerEventHistory",
        "IX_JournalEntries_OneReversalPerEntry",
        "IX_Characters_AppUserId_NormalizedName",
        "IX_HnmRecurringBoards_LinkshellId_SpawnKey",
    ];

    private const string NotValidConstraintsSql = """
        SELECT t.relname, c.conname
        FROM pg_constraint c
        JOIN pg_class t ON t.oid = c.conrelid
        JOIN pg_namespace n ON n.oid = c.connamespace
        WHERE NOT c.convalidated AND n.nspname = current_schema()
        ORDER BY 1, 2
        """;

    private const string InvalidIndexesSql = """
        SELECT t.relname, ic.relname
        FROM pg_index i
        JOIN pg_class ic ON ic.oid = i.indexrelid
        JOIN pg_class t ON t.oid = i.indrelid
        JOIN pg_namespace n ON n.oid = ic.relnamespace
        WHERE NOT i.indisvalid AND n.nspname = current_schema()
        ORDER BY 1, 2
        """;

    private const string MissingGuardedIndexesSql = """
        SELECT g.name
        FROM unnest(@names) AS g(name)
        WHERE NOT EXISTS (
            SELECT 1 FROM pg_class c
            JOIN pg_namespace n ON n.oid = c.relnamespace
            WHERE c.relname = g.name AND c.relkind = 'i' AND n.nspname = current_schema())
        ORDER BY 1
        """;

    // Returns how many problems were logged (0 on a healthy database).
    public static async Task<int> LogProblemsAsync(DbContext db, ILogger log, CancellationToken cancellationToken)
    {
        var problems = 0;
        await db.Database.OpenConnectionAsync(cancellationToken);
        try
        {
            var connection = db.Database.GetDbConnection();

            foreach (var (table, constraint) in await ReadPairsAsync(connection, NotValidConstraintsSql, cancellationToken))
            {
                log.LogWarning(
                    "Migration health: constraint {Constraint} on {Table} is NOT VALID (old rows were never checked against it).",
                    constraint, table);
                problems++;
            }

            foreach (var (table, index) in await ReadPairsAsync(connection, InvalidIndexesSql, cancellationToken))
            {
                log.LogWarning(
                    "Migration health: index {Index} on {Table} is invalid (Postgres does not use or enforce it).",
                    index, table);
                problems++;
            }

            await using var command = connection.CreateCommand();
            command.CommandText = MissingGuardedIndexesSql;
            var names = command.CreateParameter();
            names.ParameterName = "names";
            names.Value = GuardedIndexes.ToArray();
            command.Parameters.Add(names);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                log.LogWarning(
                    "Migration health: guarded index {Index} is missing (its migration skipped it; see the Postgres WARNING lines).",
                    reader.GetString(0));
                problems++;
            }
        }
        finally
        {
            await db.Database.CloseConnectionAsync();
        }

        return problems;
    }

    private static async Task<List<(string First, string Second)>> ReadPairsAsync(
        DbConnection connection, string sql, CancellationToken cancellationToken)
    {
        var rows = new List<(string, string)>();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add((reader.GetString(0), reader.GetString(1)));
        }

        return rows;
    }
}
