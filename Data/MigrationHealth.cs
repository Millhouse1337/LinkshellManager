using System.Data.Common;
using Microsoft.EntityFrameworkCore;

namespace LinkshellManagerDiscordApp.Data;

// Read-only checks run once at start-up, right after Database.Migrate. Each problem found becomes
// one Warning line starting "Migration health:". Nothing is changed and start-up never stops here.
//
// What it looks for (all in the app's schema):
// - a constraint still NOT VALID (added without checking the old rows, never validated);
// - an index Postgres marks invalid (a failed CREATE INDEX CONCURRENTLY leaves one behind);
// - a guarded index that is missing, or there but not unique. A guarded migration skips a unique
//   index with RAISE WARNING instead of failing start-up when old rows would break it, so the index
//   can be silently absent, or left as the plain index an earlier migration built under the same
//   name (CampSpawnKeySwitch works that way);
// - a guarded NOT NULL column that still allows NULL (MembershipRoleRequired skips NOT NULL the
//   same way when a member has no role).
public static class MigrationHealth
{
    // The guarded indexes that exist today, each unique. Add a name here only in the deploy whose
    // migration creates it, so a healthy database never warns.
    public static readonly IReadOnlyList<string> GuardedIndexes =
    [
        "IX_EventAttendanceWindows_OneClosePerEvent",
        "IX_EventAttendanceWindows_OneClosePerEventHistory",
        "IX_JournalEntries_OneReversalPerEntry",
        "IX_Characters_AppUserId_NormalizedName",
        "IX_HnmRecurringBoards_LinkshellId_SpawnKey",
        "IX_LinkshellMonsterTimings_LinkshellId_SpawnKey",
    ];

    // The columns a guarded migration makes NOT NULL, as "Table.Column". Same rule as above: add one
    // only in the deploy whose migration sets it.
    public static readonly IReadOnlyList<string> GuardedNotNullColumns =
    [
        "AppUserLinkshells.LinkshellRoleId",
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

    // Each guarded index that is missing or not unique, and which of the two.
    private const string BadGuardedIndexesSql = """
        SELECT g.name, CASE WHEN x.is_unique IS NULL THEN 'missing' ELSE 'not unique' END
        FROM unnest(@names) AS g(name)
        LEFT JOIN LATERAL (
            SELECT i.indisunique AS is_unique
            FROM pg_class c
            JOIN pg_namespace n ON n.oid = c.relnamespace
            JOIN pg_index i ON i.indexrelid = c.oid
            WHERE c.relname = g.name AND c.relkind = 'i' AND n.nspname = current_schema()) x ON true
        WHERE x.is_unique IS NOT TRUE
        ORDER BY 1
        """;

    // Each guarded column that still allows NULL. A column that does not exist is not reported.
    private const string NullableGuardedColumnsSql = """
        SELECT c.table_name::text, c.column_name::text
        FROM information_schema.columns c
        WHERE c.table_schema = current_schema()
          AND c.is_nullable = 'YES'
          AND (c.table_name || '.' || c.column_name) = ANY(@names)
        ORDER BY 1, 2
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

            foreach (var (index, state) in await ReadPairsAsync(connection, BadGuardedIndexesSql, cancellationToken, GuardedIndexes))
            {
                if (state == "missing")
                {
                    log.LogWarning(
                        "Migration health: guarded index {Index} is missing (its migration skipped it; see the Postgres WARNING lines).",
                        index);
                }
                else
                {
                    log.LogWarning(
                        "Migration health: guarded index {Index} is not unique (its migration kept the plain index and skipped the unique one; see the Postgres WARNING lines).",
                        index);
                }
                problems++;
            }

            foreach (var (table, column) in await ReadPairsAsync(connection, NullableGuardedColumnsSql, cancellationToken, GuardedNotNullColumns))
            {
                log.LogWarning(
                    "Migration health: {Table}.{Column} still allows NULL (its migration skipped NOT NULL; see the Postgres WARNING lines).",
                    table, column);
                problems++;
            }
        }
        finally
        {
            await db.Database.CloseConnectionAsync();
        }

        return problems;
    }

    // Runs sql and returns its first two columns. names, when given, is passed as @names.
    private static async Task<List<(string First, string Second)>> ReadPairsAsync(
        DbConnection connection, string sql, CancellationToken cancellationToken, IReadOnlyList<string>? names = null)
    {
        var rows = new List<(string, string)>();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        if (names is not null)
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = "names";
            parameter.Value = names.ToArray();
            command.Parameters.Add(parameter);
        }
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add((reader.GetString(0), reader.GetString(1)));
        }

        return rows;
    }
}
