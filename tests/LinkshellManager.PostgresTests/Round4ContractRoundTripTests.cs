using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using LinkshellManagerDiscordApp.Migrations;
using LinkshellManagerDiscordApp.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using Xunit;

namespace LinkshellManager.PostgresTests;

// Round4Contract (Round 4 deploy 4) drops the old membership job columns, the key-item MembershipId
// with its old unique index, and two MonsterName indexes. On a database that looks like production
// before deploy 4 (rows in all four tables, old values in the columns that go):
// - Up, Down and Up again: the schema goes and comes back exactly, and no kept value changes.
// - The rollback the owner runs by hand (RollbackSql) puts back every column the deploy 3 build
//   (72cbe56) maps, so that build can run again; running it twice is fine; and the next deploy 4
//   start drops them again.
//
// Its OWN database inside the test container (SchemaTestDatabase), dropped at the end.
[Collection("postgres")]
public class Round4ContractRoundTripTests
{
    // The deploy 3 build's last migration. Its target model is the deploy 3 model.
    private const string Before = "20261115092616_CharactersSwitch";

    // Named, not "latest", so a later migration's Down never joins this test.
    private const string Contract = Round4Contract.MigrationId;

    private static readonly (string Table, string Column)[] DroppedColumns =
    {
        ("AppUserLinkshells", "JobLevels"),
        ("AppUserLinkshells", "StrongJobs"),
        ("AppUserLinkshells", "MeritJobs"),
        ("ChartMemberKeyItems", "MembershipId"),
    };

    private static readonly string[] DroppedIndexes =
    {
        "IX_ChartMemberKeyItems_LinkshellId_Board_KeyItemName_Membershi~",
        "IX_HnmRecurringBoards_LinkshellId_MonsterName",
        "IX_Tods_LinkshellId_MonsterName",
    };

    // The four tables, every kept column of every row (the dropped ones taken out), so "no kept
    // value changed" is one string compare. DKP balances are in the membership rows.
    private const string KeptRowsSql = """
        SELECT concat_ws(E'\n',
            (SELECT string_agg((to_jsonb(t) - 'JobLevels' - 'StrongJobs' - 'MeritJobs')::text, E'\n' ORDER BY t."Id") FROM "AppUserLinkshells" t),
            (SELECT string_agg((to_jsonb(t) - 'MembershipId')::text, E'\n' ORDER BY t."Id") FROM "ChartMemberKeyItems" t),
            (SELECT string_agg(to_jsonb(t)::text, E'\n' ORDER BY t."Id") FROM "HnmRecurringBoards" t),
            (SELECT string_agg(to_jsonb(t)::text, E'\n' ORDER BY t."Id") FROM "Tods" t))
        """;

    private readonly PostgresFixture _pg;

    public Round4ContractRoundTripTests(PostgresFixture pg) => _pg = pg;

    [Fact]
    public async Task Round4Contract_GoesUpDownAndUpAgain_AndTheRollbackSqlBringsBackTheDeploy3Columns()
    {
        var connectionString = await SchemaTestDatabase.CreateAsync(_pg, "round4_contract_");
        try
        {
            await MigrateAsync(connectionString, Before);
            var schemaBefore = await SchemaTestDatabase.FingerprintAsync(connectionString);
            await SeedAsync(connectionString);
            string keptRows;
            await using (var c = await OpenAsync(connectionString))
            {
                keptRows = await TextAsync(c, KeptRowsSql);
                // The seed really put old values in the columns that go.
                Assert.Equal(2L, await ScalarAsync(c, "SELECT count(*) FROM \"AppUserLinkshells\" WHERE \"JobLevels\" IS NOT NULL"));
                Assert.Equal(1L, await ScalarAsync(c, "SELECT count(*) FROM \"ChartMemberKeyItems\" WHERE \"MembershipId\" IS NOT NULL"));
            }

            // Up: exactly the dropped columns and indexes leave the catalog; every kept value stays.
            await MigrateAsync(connectionString, Contract);
            await AssertDroppedAsync(connectionString, schemaBefore, keptRows);

            // Down: the schema is exactly the deploy 3 schema again, with the columns empty.
            await MigrateAsync(connectionString, Before);
            await AssertDeploy3SchemaAsync(connectionString, schemaBefore, keptRows);

            // Up again: the drop still applies after a Down.
            await MigrateAsync(connectionString, Contract);
            await AssertDroppedAsync(connectionString, schemaBefore, keptRows);

            // The rollback to the deploy 3 build: the owner runs RollbackSql by hand (the swapped-in
            // build never runs a Down). Twice, to prove a second run is harmless.
            await SchemaTestDatabase.ExecuteAsync(connectionString, Round4Contract.RollbackSql);
            await SchemaTestDatabase.ExecuteAsync(connectionString, Round4Contract.RollbackSql);
            await AssertDeploy3SchemaAsync(connectionString, schemaBefore, keptRows);
            await AssertDeploy3ModelColumnsExistAsync(connectionString);
            await using (var c = await OpenAsync(connectionString))
            {
                Assert.Equal(0L, await ScalarAsync(c,
                    $"SELECT count(*) FROM \"__EFMigrationsHistory\" WHERE \"MigrationId\" = '{Contract}'"));
            }

            // The deploy 3 build reads and writes with the columns back (a tick with no membership,
            // as it writes them) without tripping the re-created indexes.
            await using (var c = await OpenAsync(connectionString))
            {
                await ScalarAsync(c,
                    "INSERT INTO \"ChartMemberKeyItems\" (\"LinkshellId\", \"Board\", \"KeyItemName\", \"MembershipId\", \"AppUserId\", \"SetAt\") " +
                    "SELECT \"LinkshellId\", 'Dynamis', 'After rollback', NULL, 'u-amy', now() FROM \"AppUserLinkshells\" LIMIT 1");
                keptRows = await TextAsync(c, KeptRowsSql);
            }

            // The next deploy 4 start: the migration is pending again, so the drops happen again.
            await MigrateAsync(connectionString, Contract);
            await AssertDroppedAsync(connectionString, schemaBefore, keptRows);
        }
        finally
        {
            await SchemaTestDatabase.DropAsync(_pg, connectionString);
        }
    }

    // A linkshell, two accounts with memberships (DKP and old job copies), a third membership with
    // no account, ticks with and without a membership id, two boards and two ToDs.
    private static async Task SeedAsync(string connectionString)
    {
        int linkshell;
        await using (var db = SchemaTestDatabase.CreateContext(connectionString))
        {
            var row = new Linkshell { LinkshellName = "LS" };
            db.Linkshells.Add(row);
            await db.SaveChangesAsync();
            linkshell = row.Id;
        }

        await using var c = await OpenAsync(connectionString);
        await ScalarAsync(c, PostgresFixture.InsertAccountSql("u-bob"));
        await ScalarAsync(c, PostgresFixture.InsertAccountSql("u-amy"));
        var bob = await InsertAsync(c, Membership(linkshell, "'u-bob'", 125.5, "'[0, 75]'", "'[0, 1]'", "'[\"\", \"Axe\"]'"));
        await InsertAsync(c, Membership(linkshell, "'u-amy'", 7.25, "'[0, 30]'", "NULL", "NULL"));
        await InsertAsync(c, Membership(linkshell, "NULL", 0, "NULL", "NULL", "NULL"));

        await ScalarAsync(c, KeyItem(linkshell, bob.ToString(), "Held", "u-bob"));
        await ScalarAsync(c, KeyItem(linkshell, "NULL", "New", "u-amy"));

        await ScalarAsync(c,
            "INSERT INTO \"HnmRecurringBoards\" (\"LinkshellId\", \"MonsterName\", \"SpawnKey\", \"Enabled\", \"LeadHours\", \"CreatedAt\", \"UpdatedAt\") " +
            $"VALUES ({linkshell}, 'Fafnir', 'Fafnir', true, 1, now(), now()), ({linkshell}, 'Tiamat', 'Tiamat', false, 2, now(), now())");
        await ScalarAsync(c,
            "INSERT INTO \"Tods\" (\"LinkshellId\", \"MonsterName\", \"Cooldown\", \"Interval\", \"Hq\", \"AdditionalSeconds\", \"Time\", \"SpawnKey\", \"CooldownMinutes\") " +
            $"VALUES ({linkshell}, 'Fafnir', '22', '10', false, 0, now(), 'Fafnir', 1320), ({linkshell}, 'Fafnir', '22', '10', true, 5, now(), 'Fafnir', 1320)");
    }

    private static string Membership(int linkshellId, string appUserId, double dkp, string jobs, string strong, string merits) =>
        "INSERT INTO \"AppUserLinkshells\" (\"LinkshellId\", \"AppUserId\", \"Rank\", \"DiscordIdConfirmed\", " +
        "\"DkpPoolLedgerFromId\", \"DkpSeedLedgerId\", \"SeededDkpEarned\", \"SeededDkpSpent\", \"LinkshellDkp\", " +
        "\"JobLevels\", \"StrongJobs\", \"MeritJobs\") " +
        $"VALUES ({linkshellId}, {appUserId}, 'Member', false, 0, 0, 0, 0, {dkp.ToString(System.Globalization.CultureInfo.InvariantCulture)}, " +
        $"{jobs}, {strong}, {merits}) RETURNING \"Id\"";

    private static string KeyItem(int linkshellId, string membershipId, string name, string appUserId) =>
        "INSERT INTO \"ChartMemberKeyItems\" (\"LinkshellId\", \"Board\", \"KeyItemName\", \"MembershipId\", \"AppUserId\", \"SetAt\") " +
        $"VALUES ({linkshellId}, 'Dynamis', '{name}', {membershipId}, '{appUserId}', now())";

    // The catalog lost exactly the dropped columns and indexes (nothing else changed), and every
    // kept value is as it was.
    private static async Task AssertDroppedAsync(string connectionString, string schemaBefore, string keptRows)
    {
        var after = Lines(await SchemaTestDatabase.FingerprintAsync(connectionString));
        var before = Lines(schemaBefore);

        Assert.Empty(after.Except(before, StringComparer.Ordinal));
        var expectedGone = DroppedColumns.Select(d => $"column\t{d.Table}.{d.Column}")
            .Concat(DroppedIndexes.Select(index => $"index\t{TableOf(index)}.{index}"))
            .OrderBy(prefix => prefix, StringComparer.Ordinal)
            .ToList();
        var gone = before.Except(after, StringComparer.Ordinal)
            .Select(line => string.Join('\t', line.Split('\t').Take(2)))
            .OrderBy(prefix => prefix, StringComparer.Ordinal)
            .ToList();
        Assert.Equal(expectedGone, gone);

        await using var c = await OpenAsync(connectionString);
        Assert.Equal(keptRows, await TextAsync(c, KeptRowsSql));
    }

    // The schema is exactly the deploy 3 one, the re-added columns are empty (their values come
    // back only from the dump), and every kept value is as it was.
    private static async Task AssertDeploy3SchemaAsync(string connectionString, string schemaBefore, string keptRows)
    {
        Assert.Equal(schemaBefore, await SchemaTestDatabase.FingerprintAsync(connectionString));

        await using var c = await OpenAsync(connectionString);
        Assert.Equal(keptRows, await TextAsync(c, KeptRowsSql));
        Assert.Equal(0L, await ScalarAsync(c,
            "SELECT count(*) FROM \"AppUserLinkshells\" WHERE \"JobLevels\" IS NOT NULL OR \"StrongJobs\" IS NOT NULL OR \"MeritJobs\" IS NOT NULL"));
        Assert.Equal(0L, await ScalarAsync(c, "SELECT count(*) FROM \"ChartMemberKeyItems\" WHERE \"MembershipId\" IS NOT NULL"));
    }

    // Every column the deploy 3 build maps (the target model of its last migration) is in the
    // database. EF names every mapped column when it loads a row, so a missing one would make that
    // build fail on every such query.
    private static async Task AssertDeploy3ModelColumnsExistAsync(string connectionString)
    {
        IModel deploy3Model;
        await using (var db = SchemaTestDatabase.CreateContext(connectionString))
        {
            var migrations = db.GetService<IMigrationsAssembly>();
            deploy3Model = migrations
                .CreateMigration(migrations.Migrations[Before], db.Database.ProviderName!)
                .TargetModel!;
        }

        var mapped = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entityType in deploy3Model.GetEntityTypes())
        {
            var table = entityType.GetTableName();
            if (table is null)
            {
                continue;
            }
            foreach (var property in entityType.GetProperties())
            {
                var column = property.GetColumnName();
                // xmin (the concurrency token) is a system column, not in information_schema.
                if (column != "xmin")
                {
                    mapped.Add(table + "." + column);
                }
            }
        }
        foreach (var (table, column) in DroppedColumns)
        {
            Assert.Contains(table + "." + column, mapped);
        }

        var existing = new HashSet<string>(StringComparer.Ordinal);
        await using var c = await OpenAsync(connectionString);
        await using var command = c.CreateCommand();
        command.CommandText =
            "SELECT table_name::text || '.' || column_name::text FROM information_schema.columns WHERE table_schema = 'public'";
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            existing.Add(reader.GetString(0));
        }
        Assert.Empty(mapped.Where(name => !existing.Contains(name)).OrderBy(name => name, StringComparer.Ordinal));
    }

    private static string TableOf(string index) => index.Split('_')[1];

    private static List<string> Lines(string fingerprint) =>
        fingerprint.Split('\n', StringSplitOptions.RemoveEmptyEntries).ToList();

    // Moves the schema of the given database to exactly this migration, forwards or backwards.
    private static async Task MigrateAsync(string connectionString, string targetMigration)
    {
        await using var db = SchemaTestDatabase.CreateContext(connectionString);
        await db.GetService<IMigrator>().MigrateAsync(targetMigration);
    }

    private static async Task<NpgsqlConnection> OpenAsync(string connectionString)
    {
        var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        return connection;
    }

    private static async Task<int> InsertAsync(NpgsqlConnection c, string insert) =>
        (int)(await ScalarAsync(c, insert))!;

    private static async Task<string> TextAsync(NpgsqlConnection c, string sql) =>
        (string)(await ScalarAsync(c, sql))!;

    private static async Task<object?> ScalarAsync(NpgsqlConnection c, string sql)
    {
        await using var command = c.CreateCommand();
        command.CommandText = sql;
        return await command.ExecuteScalarAsync();
    }
}
