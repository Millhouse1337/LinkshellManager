using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using LinkshellManagerDiscordApp.Migrations;
using LinkshellManagerDiscordApp.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using Xunit;

namespace LinkshellManager.PostgresTests;

// NormalizeNamesForSquash (Round 4 deploy 4) validates the seven NOT VALID checks and renames 13 NOT
// NULL constraints and 2 identity sequences left with their pre-rename names. On a database at
// Round4Contract (deploy 4's first migration) with rows in the renamed tables:
// - Up changes exactly those catalog lines and nothing else; running its SQL again changes nothing.
// - The renamed sequences still number new rows, from where they were.
// - Down renames back and keeps the checks validated; Up again works.
// - Rolling back to deploy 3 (Round4Contract.RollbackSql) and starting deploy 4 again works with this
//   migration's row still in the history.
// - An old row that breaks a check does not stop the migration: that one check stays NOT VALID with a
//   WARNING, and the owner's by-hand run validates it once the row is fixed.
//
// Its OWN databases inside the test container (SchemaTestDatabase), dropped at the end.
[Collection("postgres")]
public class NormalizeNamesForSquashRoundTripTests
{
    // Deploy 4's first migration. Named, not "latest", so a later migration never joins this test.
    private const string Before = Round4Contract.MigrationId;

    private const string Normalize = NormalizeNamesForSquash.MigrationId;

    private const string NotValidMark = " NOT VALID [NOT VALID]";

    private readonly PostgresFixture _pg;

    public NormalizeNamesForSquashRoundTripTests(PostgresFixture pg) => _pg = pg;

    [Fact]
    public async Task NormalizeNamesForSquash_GoesUpDownAndUpAgain_AndSurvivesTheDeploy3Rollback()
    {
        var connectionString = await SchemaTestDatabase.CreateAsync(_pg, "normalize_names_");
        try
        {
            await MigrateAsync(connectionString, Before);
            var schemaBefore = await SchemaTestDatabase.FingerprintAsync(connectionString);
            var (firstItemId, firstCreditId) = await AddPopItemWithCreditAsync(connectionString);

            // What the chain gives before this migration: all seven NOT VALID, the old names there.
            await using (var c = await OpenAsync(connectionString))
            {
                foreach (var (_, name) in NormalizeNamesForSquash.Checks)
                {
                    Assert.Equal(false, await ScalarAsync(c, $"SELECT convalidated FROM pg_constraint WHERE conname = '{name}'"));
                }
            }
            var before = Lines(schemaBefore);
            foreach (var (table, oldName, _) in NormalizeNamesForSquash.ConstraintRenames)
            {
                Assert.Contains(before, line => line.StartsWith($"constraint\t\"{table}\".{oldName}\t", StringComparison.Ordinal));
            }
            foreach (var (oldName, _) in NormalizeNamesForSquash.SequenceRenames)
            {
                Assert.Contains($"sequence\t{oldName}\t", before);
            }
            var rows = await AllPopRowsAsync(connectionString);

            // Up: exactly the expected lines change.
            await MigrateAsync(connectionString, Normalize);
            var schemaAfter = Expected(schemaBefore, validate: true, rename: true);
            Assert.Equal(schemaAfter, await SchemaTestDatabase.FingerprintAsync(connectionString));
            Assert.Equal(rows, await AllPopRowsAsync(connectionString));

            // Its SQL run again (as the owner may run it by hand) changes nothing.
            await SchemaTestDatabase.ExecuteAsync(connectionString, NormalizeNamesForSquash.ValidateChecksSql);
            await SchemaTestDatabase.ExecuteAsync(connectionString, NormalizeNamesForSquash.RenameSql);
            Assert.Equal(schemaAfter, await SchemaTestDatabase.FingerprintAsync(connectionString));

            // The renamed sequences are still the columns' own and keep counting from where they were.
            await using (var c = await OpenAsync(connectionString))
            {
                Assert.Equal("public.\"ChartPopItems_Id_seq\"",
                    await ScalarAsync(c, "SELECT pg_get_serial_sequence('\"ChartPopItems\"', 'Id')"));
                Assert.Equal("public.\"ChartPopItemCredits_Id_seq\"",
                    await ScalarAsync(c, "SELECT pg_get_serial_sequence('\"ChartPopItemCredits\"', 'Id')"));
            }
            var (secondItemId, secondCreditId) = await AddPopItemWithCreditAsync(connectionString);
            Assert.True(secondItemId > firstItemId);
            Assert.True(secondCreditId > firstCreditId);
            rows = await AllPopRowsAsync(connectionString);

            // Down: the names go back; the checks stay validated (Postgres cannot un-validate one).
            await MigrateAsync(connectionString, Before);
            var schemaDown = Expected(schemaBefore, validate: true, rename: false);
            Assert.Equal(schemaDown, await SchemaTestDatabase.FingerprintAsync(connectionString));
            await SchemaTestDatabase.ExecuteAsync(connectionString, NormalizeNamesForSquash.RenameBackSql);
            Assert.Equal(schemaDown, await SchemaTestDatabase.FingerprintAsync(connectionString));
            Assert.Equal(rows, await AllPopRowsAsync(connectionString));

            // Up again.
            await MigrateAsync(connectionString, Normalize);
            Assert.Equal(schemaAfter, await SchemaTestDatabase.FingerprintAsync(connectionString));

            // The rollback to deploy 3: the owner runs Round4Contract.RollbackSql, which forgets only
            // Round4Contract. This migration's row stays (the deploy 3 build ignores ids it does not
            // know) and so do the validated checks and new names, which that build never looks at.
            await SchemaTestDatabase.ExecuteAsync(connectionString, Round4Contract.RollbackSql);
            await using (var c = await OpenAsync(connectionString))
            {
                Assert.Equal(0L, await ScalarAsync(c, $"SELECT count(*) FROM \"__EFMigrationsHistory\" WHERE \"MigrationId\" = '{Before}'"));
                Assert.Equal(1L, await ScalarAsync(c, $"SELECT count(*) FROM \"__EFMigrationsHistory\" WHERE \"MigrationId\" = '{Normalize}'"));
            }

            // The next deploy 4 start runs Round4Contract again (although a newer id is applied) and
            // the schema is exactly this deploy's again.
            await using (var db = SchemaTestDatabase.CreateContext(connectionString))
            {
                Assert.Equal(new[] { Before }, (await db.Database.GetPendingMigrationsAsync()).ToArray());
            }
            await MigrateAsync(connectionString, Normalize);
            Assert.Equal(schemaAfter, await SchemaTestDatabase.FingerprintAsync(connectionString));
            Assert.Equal(rows, await AllPopRowsAsync(connectionString));
        }
        finally
        {
            await SchemaTestDatabase.DropAsync(_pg, connectionString);
        }
    }

    [Fact]
    public async Task AnOldRowThatBreaksACheck_LeavesOnlyThatCheckNotValid_UntilTheOwnerFixesIt()
    {
        var connectionString = await SchemaTestDatabase.CreateAsync(_pg, "normalize_bad_row_");
        try
        {
            await MigrateAsync(connectionString, Before);
            int badLinkshellId;
            await using (var db = SchemaTestDatabase.CreateContext(connectionString))
            {
                var linkshell = new Linkshell { LinkshellName = "LS" };
                db.Linkshells.Add(linkshell);
                await db.SaveChangesAsync();
                badLinkshellId = linkshell.Id;
            }

            // An old row with a value the check refuses, as a row saved before the check might hold:
            // take the check off, write the row, put the check back exactly as the chain made it.
            await using (var c = await OpenAsync(connectionString))
            {
                var definition = (string)(await ScalarAsync(c,
                    "SELECT pg_get_constraintdef(oid) FROM pg_constraint WHERE conname = 'CK_Linkshells_HnmAttendanceMode'"))!;
                Assert.EndsWith(" NOT VALID", definition);
                await ScalarAsync(c, "ALTER TABLE \"Linkshells\" DROP CONSTRAINT \"CK_Linkshells_HnmAttendanceMode\"");
                await ScalarAsync(c, $"UPDATE \"Linkshells\" SET \"HnmAttendanceMode\" = 'OldMode' WHERE \"Id\" = {badLinkshellId}");
                await ScalarAsync(c, $"ALTER TABLE \"Linkshells\" ADD CONSTRAINT \"CK_Linkshells_HnmAttendanceMode\" {definition}");
            }

            // Up: the migration still applies. Only the broken check stays NOT VALID, and the row is untouched.
            await MigrateAsync(connectionString, Normalize);
            await using (var c = await OpenAsync(connectionString))
            {
                foreach (var (_, name) in NormalizeNamesForSquash.Checks)
                {
                    Assert.Equal(name != "CK_Linkshells_HnmAttendanceMode",
                        await ScalarAsync(c, $"SELECT convalidated FROM pg_constraint WHERE conname = '{name}'"));
                }
                Assert.Equal("OldMode", await ScalarAsync(c,
                    $"SELECT \"HnmAttendanceMode\" FROM \"Linkshells\" WHERE \"Id\" = {badLinkshellId}"));
                Assert.Equal(1L, await ScalarAsync(c,
                    $"SELECT count(*) FROM \"__EFMigrationsHistory\" WHERE \"MigrationId\" = '{Normalize}'"));
            }

            // The owner's by-hand run says which check is still NOT VALID...
            var notices = await RunCollectingNoticesAsync(connectionString, NormalizeNamesForSquash.ValidateChecksSql);
            var warning = Assert.Single(notices);
            Assert.Equal("WARNING", warning.Severity);
            Assert.Contains("CK_Linkshells_HnmAttendanceMode", warning.MessageText);

            // ...and validates it once the row is fixed.
            await SchemaTestDatabase.ExecuteAsync(connectionString,
                $"UPDATE \"Linkshells\" SET \"HnmAttendanceMode\" = 'Standard' WHERE \"Id\" = {badLinkshellId}");
            notices = await RunCollectingNoticesAsync(connectionString, NormalizeNamesForSquash.ValidateChecksSql);
            var validated = Assert.Single(notices);
            Assert.Equal("NOTICE", validated.Severity);
            Assert.Contains("validated CK_Linkshells_HnmAttendanceMode", validated.MessageText);
            await using (var c = await OpenAsync(connectionString))
            {
                foreach (var (_, name) in NormalizeNamesForSquash.Checks)
                {
                    Assert.Equal(true, await ScalarAsync(c, $"SELECT convalidated FROM pg_constraint WHERE conname = '{name}'"));
                }
            }
        }
        finally
        {
            await SchemaTestDatabase.DropAsync(_pg, connectionString);
        }
    }

    // The fingerprint before the migration, with the seven checks marked validated and/or the old
    // names swapped for the new ones, sorted the way FingerprintAsync sorts.
    private static string Expected(string schemaBefore, bool validate, bool rename)
    {
        var checks = NormalizeNamesForSquash.Checks.Select(check => $"constraint\t\"{check.Table}\".{check.Name}\t").ToList();
        var renames = NormalizeNamesForSquash.ConstraintRenames
            .Select(r => ($"constraint\t\"{r.Table}\".{r.OldName}\t", $"constraint\t\"{r.Table}\".{r.NewName}\t"))
            .Concat(NormalizeNamesForSquash.SequenceRenames.Select(r => ($"sequence\t{r.OldName}\t", $"sequence\t{r.NewName}\t")))
            .ToList();

        var lines = new List<string>();
        var validatedCount = 0;
        var renamedCount = 0;
        foreach (var original in Lines(schemaBefore))
        {
            var line = original;
            if (validate && checks.Any(prefix => line.StartsWith(prefix, StringComparison.Ordinal)))
            {
                Assert.EndsWith(NotValidMark, line);
                line = line[..^NotValidMark.Length];
                validatedCount++;
            }
            if (rename)
            {
                foreach (var (from, to) in renames)
                {
                    if (line.StartsWith(from, StringComparison.Ordinal))
                    {
                        line = to + line[from.Length..];
                        renamedCount++;
                    }
                }
            }
            lines.Add(line);
        }
        Assert.Equal(validate ? 7 : 0, validatedCount);
        Assert.Equal(rename ? 15 : 0, renamedCount);
        lines.Sort(StringComparer.Ordinal);
        return string.Join("\n", lines) + "\n";
    }

    // A pop item with one credit, through the app's own model (its ids come from the two sequences).
    private static async Task<(int ItemId, int CreditId)> AddPopItemWithCreditAsync(string connectionString)
    {
        await using var db = SchemaTestDatabase.CreateContext(connectionString);
        var linkshell = await db.Linkshells.FirstOrDefaultAsync() ?? new Linkshell { LinkshellName = "Pop LS" };
        var item = new ChartPopItem { Linkshell = linkshell, Board = "Sky", Boss = "Kirin", ItemName = "Gem of the East" };
        var credit = new ChartPopItemCredit { ChartPopItem = item, CharacterName = "Aeris" };
        db.ChartPopItemCredits.Add(credit);
        await db.SaveChangesAsync();
        return (item.Id, credit.Id);
    }

    private static async Task<string> AllPopRowsAsync(string connectionString)
    {
        await using var c = await OpenAsync(connectionString);
        return (string)(await ScalarAsync(c, """
            SELECT concat_ws(E'\n',
                (SELECT string_agg(to_jsonb(t)::text, E'\n' ORDER BY t."Id") FROM "ChartPopItems" t),
                (SELECT string_agg(to_jsonb(t)::text, E'\n' ORDER BY t."Id") FROM "ChartPopItemCredits" t),
                (SELECT string_agg(to_jsonb(t)::text, E'\n' ORDER BY t."Id") FROM "Linkshells" t))
            """))!;
    }

    private static async Task<List<PostgresNotice>> RunCollectingNoticesAsync(string connectionString, string sql)
    {
        var notices = new List<PostgresNotice>();
        await using var c = await OpenAsync(connectionString);
        c.Notice += (_, e) => notices.Add(e.Notice);
        await ScalarAsync(c, sql);
        return notices;
    }

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

    private static async Task<object?> ScalarAsync(NpgsqlConnection c, string sql)
    {
        await using var command = c.CreateCommand();
        command.CommandText = sql;
        return await command.ExecuteScalarAsync();
    }
}
