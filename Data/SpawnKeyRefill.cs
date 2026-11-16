using System.Data.Common;
using Microsoft.EntityFrameworkCore;

namespace LinkshellManagerDiscordApp.Data;

// Start-up repair of SpawnKey, run once right after Database.Migrate (Program.cs).
//
// Why: the save hook (ApplicationDbContext.StampDerivedColumns) stamps SpawnKey from the monster
// name on every save, and the app looks rows up by it. But a build from before AddCampSpawnKeys
// (for example after a rollback by folder swap) saves Events, PartySetups, ClaimShieldCaptures and
// LinkshellMonsterTimings without a key, or keeps the old key when it renames the monster.
// CampSpawnKeySwitch repairs those rows once, when it runs; this repairs them on every start-up, so
// rows saved during a rollback that came after the migration ran get their keys back as soon as the
// newer build is deployed again. Tods, PendingTodSubmissions and HnmRecurringBoards are checked too
// (every build since Round 4 stamps them, so they should never need it).
//
// Each table is one UPDATE that only writes rows whose stored key differs from the key its name
// gives (TodDerivedSql.SpawnKeySql, the SQL copy of HnmConfig.SpawnKey). A healthy database writes
// nothing and logs nothing. A table that fails (say a re-key would put two Monster Setups rows of
// one linkshell on one spawn, which the unique index refuses) is logged as a Warning and skipped;
// the other tables still run and start-up never stops here. No DKP, gil, ledger or loot row is
// touched, and no row is deleted.
public static class SpawnKeyRefill
{
    // Table, monster-name column, and whether SpawnKey is NOT NULL there (a row whose name gives no
    // key is then left alone instead of failing the whole table).
    public static readonly IReadOnlyList<(string Table, string NameColumn, bool KeyRequired)> Tables =
    [
        ("Events", "AssignedMonsterName", false),
        ("PartySetups", "AssignedMonsterName", false),
        ("ClaimShieldCaptures", "MonsterName", false),
        ("LinkshellMonsterTimings", "MonsterName", false),
        ("Tods", "MonsterName", false),
        ("PendingTodSubmissions", "MonsterName", false),
        ("HnmRecurringBoards", "MonsterName", true),
    ];

    // One table's repair. Returns the rows it wrote (ExecuteNonQuery).
    public static string RefillSql(string table, string nameColumn, bool keyRequired) => $"""
        WITH d AS (SELECT x."Id", {TodDerivedSql.SpawnKeySql($"x.\"{nameColumn}\"")} AS k FROM "{table}" x)
        UPDATE "{table}" x SET "SpawnKey" = d.k
        FROM d
        WHERE x."Id" = d."Id" AND x."SpawnKey" IS DISTINCT FROM d.k{(keyRequired ? " AND d.k IS NOT NULL" : "")}
        """;

    // Returns the number of rows re-keyed across all tables (0 on a healthy database, and on a
    // database that is not Postgres, such as the InMemory test database).
    public static async Task<int> RunAsync(DbContext db, ILogger log, CancellationToken cancellationToken)
    {
        if (!db.Database.IsNpgsql())
        {
            return 0;
        }

        var total = 0;
        await db.Database.OpenConnectionAsync(cancellationToken);
        try
        {
            var connection = db.Database.GetDbConnection();
            foreach (var (table, nameColumn, keyRequired) in Tables)
            {
                try
                {
                    var written = await ExecuteAsync(connection, RefillSql(table, nameColumn, keyRequired), cancellationToken);
                    if (written > 0)
                    {
                        log.LogInformation(
                            "SpawnKey refill: re-keyed {Count} row(s) in {Table} (saved by an older build).",
                            written, table);
                        total += written;
                    }
                }
                catch (DbException ex)
                {
                    log.LogWarning(ex, "SpawnKey refill: {Table} could not be re-keyed; start-up continues.", table);
                }
            }
        }
        finally
        {
            await db.Database.CloseConnectionAsync();
        }

        return total;
    }

    // A plain command on the open connection (as MigrationHealth does), so the row count comes
    // straight from Postgres and EF never parses the SQL text.
    private static async Task<int> ExecuteAsync(DbConnection connection, string sql, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
