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
// Each table only writes rows whose stored key differs from the key its name gives
// (TodDerivedSql.SpawnKeySql, the SQL copy of HnmConfig.SpawnKey). A healthy database writes
// nothing and logs nothing. Where the key may be NULL, those rows are first cleared and then
// filled, in one transaction: an older build's renames can leave two Monster Setups rows holding
// each other's keys (or a new row named like a renamed row's old key), and Postgres checks a
// unique index row by row, so writing the right keys in a single UPDATE would clash with a row not
// yet re-keyed. NULLs never clash, so clearing first lets every right key in. A table that fails (say a re-key would put two Monster Setups rows of
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

    // One table's repair: every row whose stored key differs gets the derived key. Returns the
    // rows it wrote (ExecuteNonQuery). keyRequired leaves out rows whose name gives no key.
    public static string RefillSql(string table, string nameColumn, bool keyRequired) =>
        UpdateDifferingSql(table, nameColumn, "d.k", keyRequired ? " AND d.k IS NOT NULL" : "");

    // Sets SpawnKey to NULL on every row whose stored key differs from the derived one, so the
    // RefillSql that follows (same transaction) cannot clash with an old key. Only for a column
    // that allows NULL. Returns the rows that differed.
    public static string ClearSql(string table, string nameColumn) =>
        UpdateDifferingSql(table, nameColumn, "NULL", "");

    private static string UpdateDifferingSql(string table, string nameColumn, string value, string extra) => $"""
        WITH d AS (SELECT x."Id", {TodDerivedSql.SpawnKeySql($"x.\"{nameColumn}\"")} AS k FROM "{table}" x)
        UPDATE "{table}" x SET "SpawnKey" = {value}
        FROM d
        WHERE x."Id" = d."Id" AND x."SpawnKey" IS DISTINCT FROM d.k{extra}
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
                    var written = await RefillTableAsync(connection, table, nameColumn, keyRequired, cancellationToken);
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

    // One table. A NOT NULL key column cannot be cleared, so it is one UPDATE (every build since
    // Round 4 stamps those). Otherwise clear the differing keys, then fill them, in one transaction:
    // if the fill fails (a real duplicate) the clear is undone with it and the old keys stay.
    private static async Task<int> RefillTableAsync(
        DbConnection connection, string table, string nameColumn, bool keyRequired, CancellationToken cancellationToken)
    {
        if (keyRequired)
        {
            return await ExecuteAsync(connection, null, RefillSql(table, nameColumn, keyRequired), cancellationToken);
        }

        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var differing = await ExecuteAsync(connection, transaction, ClearSql(table, nameColumn), cancellationToken);
        if (differing > 0)
        {
            await ExecuteAsync(connection, transaction, RefillSql(table, nameColumn, keyRequired), cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
        return differing;
    }

    // A plain command on the open connection (as MigrationHealth does), so the row count comes
    // straight from Postgres and EF never parses the SQL text.
    private static async Task<int> ExecuteAsync(
        DbConnection connection, DbTransaction? transaction, string sql, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        return await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
