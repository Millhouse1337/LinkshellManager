using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LinkshellManagerDiscordApp.Data;
using LinkshellManagerDiscordApp.Migrations;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Logging;
using Npgsql;
using Xunit;

namespace LinkshellManager.PostgresTests;

// Round 5, R5.7: Round5Contract drops the 14 old AspNetUsers alt, job and craft columns and the
// roster name (AppUserLinkshells.CharacterName).
//
// From CharactersBecomeSource (the last migration of the update B build, 1adf6d6), on accounts with
// mains, alts, jobs and crafts, and old copies that R5.6 left out of date:
// - Up drops exactly the 15 columns and changes no other row (Characters, the accounts' main names,
//   the memberships and their DKP stay as they were);
// - Down brings them back filled from Characters (ReAddSql), and Up again drops them again;
// - RollbackSql (what the owner runs in psql before swapping back to update B) brings them back,
//   filled, and forgets the migration. Run three times (the second after drift made by hand), it
//   fixes what differs and then writes nothing. After it, every column the update B model maps
//   exists with its type, update B's own start-up check (AccountNameMirrorCounts) finds 0
//   differences, and the build before update B's copy into Characters would change nothing;
// - the next update C start (Migrate) drops them again.
//
// Its OWN database inside the test container, dropped at the end.
[Collection("postgres")]
public class Round5ContractRoundTripTests
{
    // Named, not "latest", so a later migration's Down never joins this test. Before is the last
    // migration the update B build knows.
    private const string Before = "20261116175636_CharactersBecomeSource";

    private static readonly string[] DroppedAccountColumns =
    {
        "AltCharacterName1", "AltCharacterName2",
        "JobLevels", "StrongJobs", "MeritJobs", "CraftLevels",
        "Alt1JobLevels", "Alt1StrongJobs", "Alt1MeritJobs", "Alt1CraftLevels",
        "Alt2JobLevels", "Alt2StrongJobs", "Alt2MeritJobs", "Alt2CraftLevels",
    };

    private readonly PostgresFixture _pg;

    public Round5ContractRoundTripTests(PostgresFixture pg) => _pg = pg;

    [Fact]
    public void TheMigrationIdConstant_IsThisMigrationsId()
    {
        var attribute = typeof(Round5Contract).GetCustomAttributes(typeof(MigrationAttribute), false)
            .Cast<MigrationAttribute>().Single();
        Assert.Equal(Round5Contract.MigrationId, attribute.Id);
        Assert.EndsWith(
            $"END $$;\nDELETE FROM \"__EFMigrationsHistory\" WHERE \"MigrationId\" = '{Round5Contract.MigrationId}';",
            Round5Contract.RollbackSql.Replace("\r\n", "\n"));
        Assert.StartsWith(Round5Contract.ReAddSql, Round5Contract.RollbackSql);
    }

    [Fact]
    public async Task Round5Contract_GoesUpDownAndUp_AndTheRollbackSqlBringsUpdateBsColumnsBack()
    {
        var connectionString = await SchemaTestDatabase.CreateAsync(_pg, "round5_contract_");
        try
        {
            await SchemaTestDatabase.MigrateToAsync(connectionString, Before);
            await SeedAsync(connectionString);
            var characters = await CharactersAsync(connectionString);
            var accounts = await AccountsAsync(connectionString);
            var memberships = await MembershipsAsync(connectionString);

            // Up: the 15 columns go, nothing else changes.
            await MigrateAsync(connectionString, Round5Contract.MigrationId);
            await AssertDroppedAsync(connectionString);
            Assert.Equal(characters, await CharactersAsync(connectionString));
            Assert.Equal(accounts, await AccountsAsync(connectionString));
            Assert.Equal(memberships, await MembershipsAsync(connectionString));

            // Down: the columns come back, filled from Characters (a1 and a3 have more than a main
            // name; a1, a2 and a5 have a main for their roster name).
            var log = await MigrateAsync(connectionString, Before);
            Assert.Contains(
                "Postgres NOTICE during migration: Round5Contract re-add: 2 account(s) refilled from Characters; 3 roster name(s) set to the main.",
                log.Entries.Select(e => e.Message));
            Assert.DoesNotContain(log.Entries, e => e.Level >= LogLevel.Warning);
            await AssertLikeUpdateBLeftItAsync(connectionString);
            Assert.Equal(characters, await CharactersAsync(connectionString));
            Assert.Equal(accounts, await AccountsAsync(connectionString));
            Assert.Equal(memberships, await MembershipsAsync(connectionString));

            // Up again.
            await MigrateAsync(connectionString, Round5Contract.MigrationId);
            await AssertDroppedAsync(connectionString);

            // The rollback SQL, as the owner runs it in psql.
            Assert.Equal(
                new[] { "Round5Contract re-add: 2 account(s) refilled from Characters; 3 roster name(s) set to the main." },
                await RunRollbackSqlAsync(connectionString));
            Assert.DoesNotContain(Round5Contract.MigrationId, await HistoryAsync(connectionString));
            Assert.Equal(Before, (await HistoryAsync(connectionString)).Last());
            await AssertLikeUpdateBLeftItAsync(connectionString);
            await AssertEveryColumnUpdateBMapsExistsAsync(connectionString);

            // Drift made by hand afterwards (or by update B, had a copy gone wrong): run again, it is
            // fixed; run once more, nothing is left to write.
            await SchemaTestDatabase.ExecuteAsync(connectionString,
                "UPDATE \"AspNetUsers\" SET \"Alt1JobLevels\" = '[1]' WHERE \"Id\" = 'a2'");
            await SchemaTestDatabase.ExecuteAsync(connectionString,
                "UPDATE \"AppUserLinkshells\" SET \"CharacterName\" = 'Wrong' WHERE \"AppUserId\" = 'a1'");
            Assert.Equal(
                new[] { "Round5Contract re-add: 1 account(s) refilled from Characters; 1 roster name(s) set to the main." },
                await RunRollbackSqlAsync(connectionString));
            Assert.Equal(
                new[] { "Round5Contract re-add: 0 account(s) refilled from Characters; 0 roster name(s) set to the main." },
                await RunRollbackSqlAsync(connectionString));
            await AssertLikeUpdateBLeftItAsync(connectionString);
            await AssertEveryColumnUpdateBMapsExistsAsync(connectionString);
            Assert.Equal(characters, await CharactersAsync(connectionString));
            Assert.Equal(accounts, await AccountsAsync(connectionString));
            Assert.Equal(memberships, await MembershipsAsync(connectionString));

            // The next update C start drops them again, and the database is healthy.
            await SchemaTestDatabase.MigrateToLatestAsync(connectionString);
            await AssertDroppedAsync(connectionString);
            Assert.Equal(characters, await CharactersAsync(connectionString));
            await using (var db = SchemaTestDatabase.CreateContext(connectionString))
            {
                SchemaTestDatabase.AssertBuiltFromBaseline(db);
                var health = new MigrationNoticeLoggingTests.CapturingLogger();
                Assert.Equal(0, await MigrationHealth.LogProblemsAsync(db, health, CancellationToken.None));
            }
        }
        finally
        {
            await SchemaTestDatabase.DropAsync(_pg, connectionString);
        }
    }

    // Five accounts, as update C finds them: Characters is the source; CharacterName is the main's
    // name; the old columns and roster names hold what update B last copied, now out of date where
    // R5.6 changed a character (a1 renamed alt 1 and changed its main's jobs) or made a membership
    // (a2's has no roster name).
    // - a1: a main, two alts, jobs, strong flags, merits and crafts;
    // - a2: a main only;
    // - a3: alt 1 only (no main, so no CharacterName);
    // - a4: no characters, no membership;
    // - a5: a placeholder sharing a1's main name.
    // Accounts and characters are written with raw SQL (the save hook would tidy them); linkshells and
    // memberships through EF, then the roster names with raw SQL (the current model has none).
    private static async Task SeedAsync(string connectionString)
    {
        foreach (var id in new[] { "a1", "a2", "a3", "a4", "a5" })
        {
            await SchemaTestDatabase.ExecuteAsync(connectionString, PostgresFixture.InsertAccountSql(id));
        }
        foreach (var statement in new[]
        {
            "UPDATE \"AspNetUsers\" SET \"IsPlaceholder\" = true WHERE \"Id\" = 'a5'",
            "INSERT INTO \"Characters\" (\"AppUserId\", \"Slot\", \"Name\", \"NormalizedName\", \"JobLevels\", \"StrongJobs\", \"MeritJobs\", \"CraftLevels\", \"UpdatedAt\") VALUES " +
                "('a1', 0, 'Bob', 'bob', '[0, 75]', '[0, 1]', '[\"\", \"Axe\"]', '[9]', '2026-11-01'), " +
                "('a1', 1, 'Bobalt', 'bobalt', '[0, 50]', NULL, NULL, '[3]', '2026-11-01'), " +
                "('a1', 2, 'Bobtwo', 'bobtwo', NULL, NULL, NULL, NULL, '2026-11-01'), " +
                "('a2', 0, 'Ann', 'ann', NULL, NULL, NULL, NULL, '2026-11-01'), " +
                "('a3', 1, 'Onlyalt', 'onlyalt', '[0, 0, 30]', NULL, NULL, NULL, '2026-11-01'), " +
                "('a5', 0, 'Bob', 'bob', NULL, NULL, NULL, NULL, '2026-11-01')",
            "UPDATE \"AspNetUsers\" u SET \"CharacterName\" = c.\"Name\" FROM \"Characters\" c WHERE c.\"AppUserId\" = u.\"Id\" AND c.\"Slot\" = 0",
            // What update B last copied for a1: the old alt 1 name and the old main jobs.
            "UPDATE \"AspNetUsers\" SET \"JobLevels\" = '[0, 70]', \"StrongJobs\" = '[0, 1]', \"MeritJobs\" = '[\"\", \"Axe\"]', \"CraftLevels\" = '[9]', " +
                "\"AltCharacterName1\" = 'Oldalt', \"Alt1JobLevels\" = '[0, 50]', \"Alt1CraftLevels\" = '[3]', \"AltCharacterName2\" = 'Bobtwo' WHERE \"Id\" = 'a1'",
            "UPDATE \"AspNetUsers\" SET \"AltCharacterName1\" = 'Onlyalt', \"Alt1JobLevels\" = '[0, 0, 30]' WHERE \"Id\" = 'a3'",
        })
        {
            await SchemaTestDatabase.ExecuteAsync(connectionString, statement);
        }

        await using (var db = SchemaTestDatabase.CreateContext(connectionString))
        {
            var linkshell = new LinkshellManagerDiscordApp.Models.Linkshell { LinkshellName = "LS" };
            db.Linkshells.Add(linkshell);
            await db.SaveChangesAsync();
            var dkp = 10.5;
            foreach (var account in new[] { "a1", "a2", "a3", "a5" })
            {
                db.AppUserLinkshells.Add(new LinkshellManagerDiscordApp.Models.AppUserLinkshell
                {
                    AppUserId = account, LinkshellId = linkshell.Id, LinkshellDkp = dkp, Status = "Active",
                });
                dkp += 10;
            }
            await db.SaveChangesAsync();
        }

        foreach (var (account, roster) in new[] { ("a1", "'Bob'"), ("a2", "NULL"), ("a3", "NULL"), ("a5", "'Bob'") })
        {
            await SchemaTestDatabase.ExecuteAsync(connectionString,
                $"UPDATE \"AppUserLinkshells\" SET \"CharacterName\" = {roster} WHERE \"AppUserId\" = '{account}'");
        }
    }

    // The 15 columns are gone; AspNetUsers.CharacterName is not.
    private static async Task AssertDroppedAsync(string connectionString)
    {
        var accountColumns = await ColumnsAsync(connectionString, "AspNetUsers");
        Assert.Empty(accountColumns.Intersect(DroppedAccountColumns));
        Assert.Contains("CharacterName", accountColumns);
        Assert.DoesNotContain("CharacterName", await ColumnsAsync(connectionString, "AppUserLinkshells"));
    }

    // The old columns are an exact copy of Characters and every roster name is its account's main:
    // update B's start-up check counts 0 everywhere, and the build before update B (which copies the
    // old columns INTO Characters) would change no Characters row.
    private static async Task AssertLikeUpdateBLeftItAsync(string connectionString)
    {
        await using var db = SchemaTestDatabase.CreateContext(connectionString);
        Assert.Equal(new AccountNameMirrorCounts.Result(0, 0, 0), await AccountNameMirrorCounts.CountAsync(db, CancellationToken.None));
        Assert.Equal(
            new[]
            {
                "a1|Bob|[0, 75]|[0, 1]|[\"\", \"Axe\"]|[9]|Bobalt|[0, 50]|-|-|[3]|Bobtwo|-|-|-|-",
                "a2|Ann|-|-|-|-|-|-|-|-|-|-|-|-|-|-",
                "a3|-|-|-|-|-|Onlyalt|[0, 0, 30]|-|-|-|-|-|-|-|-",
                "a4|-|-|-|-|-|-|-|-|-|-|-|-|-|-|-",
                "a5|Bob|-|-|-|-|-|-|-|-|-|-|-|-|-|-",
            },
            await QueryAsync(connectionString, """
                SELECT concat_ws('|', "Id", coalesce("CharacterName", '-'),
                    coalesce("JobLevels"::text, '-'), coalesce("StrongJobs"::text, '-'), coalesce("MeritJobs"::text, '-'), coalesce("CraftLevels"::text, '-'),
                    coalesce("AltCharacterName1", '-'), coalesce("Alt1JobLevels"::text, '-'), coalesce("Alt1StrongJobs"::text, '-'),
                    coalesce("Alt1MeritJobs"::text, '-'), coalesce("Alt1CraftLevels"::text, '-'),
                    coalesce("AltCharacterName2", '-'), coalesce("Alt2JobLevels"::text, '-'), coalesce("Alt2StrongJobs"::text, '-'),
                    coalesce("Alt2MeritJobs"::text, '-'), coalesce("Alt2CraftLevels"::text, '-'))
                FROM "AspNetUsers" ORDER BY "Id"
                """));
        Assert.Equal(new[] { "a1|Bob", "a2|Ann", "a3|-", "a5|Bob" }, await QueryAsync(connectionString,
            "SELECT concat_ws('|', \"AppUserId\", coalesce(\"CharacterName\", '-')) FROM \"AppUserLinkshells\" ORDER BY \"AppUserId\""));

        var before = await CharactersAsync(connectionString);
        await PreviousBuildCharacterCopy.RunForAllAccountsAsync(db);
        Assert.Equal(before, await CharactersAsync(connectionString));
    }

    // Every column of every table the update B build maps (the model its last migration,
    // CharactersBecomeSource, describes) exists, with the type it maps.
    private static async Task AssertEveryColumnUpdateBMapsExistsAsync(string connectionString)
    {
        await using var db = SchemaTestDatabase.CreateContext(connectionString);
        var assembly = db.GetService<IMigrationsAssembly>();
        var migration = assembly.CreateMigration(assembly.Migrations[Before], db.Database.ProviderName!);
        var updateB = migration.TargetModel!;

        var expected = new List<string>();
        foreach (var entity in updateB.GetEntityTypes())
        {
            var table = entity.GetTableName();
            if (table is null || entity.GetViewName() is not null)
            {
                continue;
            }
            var store = StoreObjectIdentifier.Table(table, entity.GetSchema());
            foreach (var property in entity.GetProperties())
            {
                var column = property.GetColumnName(store);
                if (column is not null)
                {
                    expected.Add($"{table}.{column} {property.GetColumnType(store)}");
                }
            }
        }
        Assert.Contains("AspNetUsers.Alt2CraftLevels jsonb", expected);
        Assert.Contains("AspNetUsers.AltCharacterName1 character varying(64)", expected);
        Assert.Contains("AppUserLinkshells.CharacterName text", expected);

        var actual = (await QueryAsync(connectionString, """
                SELECT c.relname || '.' || a.attname || ' ' || format_type(a.atttypid, a.atttypmod)
                FROM pg_attribute a
                JOIN pg_class c ON c.oid = a.attrelid
                JOIN pg_namespace n ON n.oid = c.relnamespace
                WHERE n.nspname = 'public' AND c.relkind = 'r' AND NOT a.attisdropped
                """)).ToHashSet(System.StringComparer.Ordinal);
        Assert.DoesNotContain(expected.Distinct(), column => !actual.Contains(column));
    }

    private static async Task<List<string>> RunRollbackSqlAsync(string connectionString)
    {
        await using var c = new NpgsqlConnection(connectionString);
        await c.OpenAsync();
        var notices = new List<string>();
        c.Notice += (_, e) => notices.Add(e.Notice.MessageText);
        await using var command = c.CreateCommand();
        command.CommandText = Round5Contract.RollbackSql;
        await command.ExecuteNonQueryAsync();
        // A second run also prints Postgres's own "column ... already exists, skipping" notices.
        return notices.Where(notice => notice.StartsWith("Round5Contract", System.StringComparison.Ordinal)).ToList();
    }

    private static Task<List<string>> HistoryAsync(string connectionString) => QueryAsync(connectionString,
        "SELECT \"MigrationId\" FROM \"__EFMigrationsHistory\" ORDER BY \"MigrationId\"");

    private static Task<List<string>> ColumnsAsync(string connectionString, string table) => QueryAsync(connectionString,
        $"SELECT column_name::text FROM information_schema.columns WHERE table_schema = 'public' AND table_name = '{table}'");

    private static Task<List<string>> CharactersAsync(string connectionString) => QueryAsync(connectionString,
        "SELECT concat_ws('|', \"Id\", \"AppUserId\", \"Slot\", \"Name\", \"NormalizedName\", \"JobLevels\"::text, " +
        "\"StrongJobs\"::text, \"MeritJobs\"::text, \"CraftLevels\"::text, \"UpdatedAt\"::text) FROM \"Characters\" ORDER BY \"Id\"");

    private static Task<List<string>> AccountsAsync(string connectionString) => QueryAsync(connectionString,
        "SELECT concat_ws('|', \"Id\", coalesce(\"CharacterName\", '-'), \"UserName\", \"IsPlaceholder\"::text) FROM \"AspNetUsers\" ORDER BY \"Id\"");

    // Every membership column that stays, DKP included.
    private static Task<List<string>> MembershipsAsync(string connectionString) => QueryAsync(connectionString,
        "SELECT concat_ws('|', \"Id\", \"AppUserId\", \"LinkshellId\", \"LinkshellRoleId\", \"Rank\", \"Status\", " +
        "\"LinkshellDkp\"::text, \"SeededDkpEarned\"::text, \"SeededDkpSpent\"::text, \"DkpSeedLedgerId\", \"DkpPoolLedgerFromId\") " +
        "FROM \"AppUserLinkshells\" ORDER BY \"Id\"");

    private static async Task<List<string>> QueryAsync(string connectionString, string sql)
    {
        await using var c = new NpgsqlConnection(connectionString);
        await c.OpenAsync();
        await using var command = c.CreateCommand();
        command.CommandText = sql;
        var rows = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            rows.Add(reader.GetString(0));
        }
        return rows;
    }

    private static async Task<MigrationNoticeLoggingTests.CapturingLogger> MigrateAsync(string connectionString, string target)
    {
        var log = new MigrationNoticeLoggingTests.CapturingLogger();
        await using var db = SchemaTestDatabase.CreateContext(connectionString);
        await MigrationNotices.RunWithNoticesLoggedAsync(db, log, async () =>
        {
            await db.GetService<IMigrator>().MigrateAsync(target);
            return 0;
        });
        return log;
    }
}
