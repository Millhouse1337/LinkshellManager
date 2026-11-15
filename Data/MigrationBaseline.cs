using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;

namespace LinkshellManagerDiscordApp.Data;

// The migration squash (Round 4, D11). The 173 old migrations were replaced by one Baseline
// migration that builds the same schema. A database the old chain already built (production) must
// not run Baseline's Up, because every table already exists. So, before Database.Migrate(),
// AdoptAsync tells EF that such a database is already at Baseline by adding ONE row to
// __EFMigrationsHistory. Nothing else changes: no schema, no data, and the old history rows stay.
//
// Why keep the old rows: if the squash build is rolled back (a plain folder swap), the build before
// it still sees every id it knows, plus one it does not, which EF ignores
// (UnknownMigrationHistoryRowTests). So a rollback needs no SQL.
//
// See docs/migration-squash.md and deploy/RUNBOOK.md ("Migration squash").
public static class MigrationBaseline
{
    // The last migration of the old chain. A database whose history holds it has the full old
    // schema, which is exactly Baseline's schema (SchemaCatalogGoldenTests and the production
    // fingerprint check before the squash).
    public const string SquashPointMigrationId = "20261115161909_NormalizeNamesForSquash";

    // Migrations/20261115190359_Baseline.cs. Sorts after every old id.
    public const string BaselineMigrationId = "20261115190359_Baseline";

    // The last commit that still has the old migrations. A database older than the squash point
    // must be brought up to date by that build first.
    public const string LastPreSquashCommit = "4b05ee2";

    private const string HistoryTable = "\"__EFMigrationsHistory\"";

    // What AdoptAsync did, for the start-up log and the tests.
    public enum Outcome
    {
        // No history table (an empty database), or no database yet. Migrate builds it from Baseline.
        FreshDatabase,

        // The history table exists but has no rows. Migrate builds it from Baseline.
        EmptyHistory,

        // Baseline's row was already there (a database built from Baseline, adopted earlier, or
        // adopted a moment ago by another instance starting at the same time).
        AlreadyAdopted,

        // The old chain's last id was there: Baseline's row was added.
        Adopted,
    }

    // Runs in one transaction. The four cases:
    // 1. no history table                     -> nothing to do (FreshDatabase);
    // 2. history has Baseline                 -> nothing to do (AlreadyAdopted);
    // 3. history has SquashPointMigrationId   -> INSERT Baseline's row, ON CONFLICT DO NOTHING (Adopted);
    // 4. any other non-empty history          -> throw: the database is older than the squash point.
    // (An empty history table, or no database at all, is treated like case 1. Database.Migrate
    // creates a missing database, so this must not fail first by trying to open it.)
    public static async Task<Outcome> AdoptAsync(DbContext db, CancellationToken cancellationToken = default)
    {
        var database = db.Database;
        if (!await db.GetService<IRelationalDatabaseCreator>().ExistsAsync(cancellationToken))
        {
            return Outcome.FreshDatabase;
        }

        await database.OpenConnectionAsync(cancellationToken);
        try
        {
            await using var transaction = await database.BeginTransactionAsync(cancellationToken);
            var connection = database.GetDbConnection();
            var dbTransaction = transaction.GetDbTransaction();

            var tableExists = (bool)(await ScalarAsync(connection, dbTransaction,
                $"SELECT to_regclass('{HistoryTable}') IS NOT NULL", cancellationToken))!;
            if (!tableExists)
            {
                return Outcome.FreshDatabase;
            }

            if (await HasRowAsync(connection, dbTransaction, BaselineMigrationId, cancellationToken))
            {
                return Outcome.AlreadyAdopted;
            }

            if (await HasRowAsync(connection, dbTransaction, SquashPointMigrationId, cancellationToken))
            {
                await using var insert = connection.CreateCommand();
                insert.Transaction = dbTransaction;
                insert.CommandText =
                    $"INSERT INTO {HistoryTable} (\"MigrationId\", \"ProductVersion\") VALUES (@id, @version) " +
                    "ON CONFLICT (\"MigrationId\") DO NOTHING";
                AddParameter(insert, "id", BaselineMigrationId);
                AddParameter(insert, "version", ProductInfo.GetVersion());
                var inserted = await insert.ExecuteNonQueryAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);

                // Two instances starting at once: only the one whose INSERT wrote the row says so.
                return inserted == 1 ? Outcome.Adopted : Outcome.AlreadyAdopted;
            }

            var rows = (long)(await ScalarAsync(connection, dbTransaction,
                $"SELECT count(*) FROM {HistoryTable}", cancellationToken))!;
            if (rows == 0)
            {
                return Outcome.EmptyHistory;
            }

            throw new InvalidOperationException(
                $"This database's migration history does not include {SquashPointMigrationId}, the last " +
                "migration before the squash, so it is older than this build can update. Deploy commit " +
                $"{LastPreSquashCommit} first (it applies the old migrations up to that point), then this build.");
        }
        finally
        {
            await database.CloseConnectionAsync();
        }
    }

    private static async Task<bool> HasRowAsync(
        DbConnection connection, DbTransaction transaction, string migrationId, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"SELECT EXISTS (SELECT 1 FROM {HistoryTable} WHERE \"MigrationId\" = @id)";
        AddParameter(command, "id", migrationId);
        return (bool)(await command.ExecuteScalarAsync(cancellationToken))!;
    }

    private static async Task<object?> ScalarAsync(
        DbConnection connection, DbTransaction transaction, string sql, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        return await command.ExecuteScalarAsync(cancellationToken);
    }

    private static void AddParameter(DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }
}
