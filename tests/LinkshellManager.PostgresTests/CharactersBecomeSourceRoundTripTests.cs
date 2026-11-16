using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LinkshellManagerDiscordApp.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Logging;
using Npgsql;
using Xunit;

namespace LinkshellManager.PostgresTests;

// Round 5, R5.5: CharactersBecomeSource (data only).
//
// Up, Down and Up again from CampSpawnKeySwitch (built from Baseline) on rows the previous build
// could leave: Characters behind the account columns, a main with spaces around it, an alt repeating
// the main, a blank main with jobs, a main too long to copy, and roster names that are not the
// account's main. After Up, Characters matches the account columns by the previous build's rules,
// the account columns are an exact copy of Characters, every roster name is its account's main, and
// the NOTICE counts each step. Down changes nothing; the previous build writing during a rollback is
// repaired by Up again; a third run writes nothing. No membership row is lost.
//
// Its OWN database inside the test container, dropped at the end.
[Collection("postgres")]
public class CharactersBecomeSourceRoundTripTests
{
    // Named, not "latest", so a later migration's Down never joins this test.
    private const string Before = "20261116032606_CampSpawnKeySwitch";
    private const string Fill = "20261116175636_CharactersBecomeSource";

    private readonly PostgresFixture _pg;

    public CharactersBecomeSourceRoundTripTests(PostgresFixture pg) => _pg = pg;

    [Fact]
    public async Task CharactersBecomeSource_GoesUpDownAndUpAgain()
    {
        var connectionString = await SchemaTestDatabase.CreateAsync(_pg, "chars_source_");
        try
        {
            await SchemaTestDatabase.MigrateToAsync(connectionString, Before);
            await SeedAsync(connectionString);
            var untouched = await CharacterRowAsync(connectionString, "a4", 0);

            // Up.
            var log = await MigrateAsync(connectionString, Fill);
            Assert.Contains(
                "Postgres NOTICE during migration: CharactersBecomeSource: Characters: 1 row(s) removed, 4 row(s) added or updated; " +
                "CharacterName: 1 set to the main, 2 cleared (no main); 3 account(s) mirrored; 3 roster name(s) set to the main.",
                log.Entries.Select(e => e.Message));
            Assert.DoesNotContain(log.Entries, e => e.Level >= LogLevel.Warning);

            var expected = new[]
            {
                "a1|0|Bob|bob|[1]",
                "a1|1|Bobalt|bobalt|",
                "a2|0|Ann|ann|",
                "a2|2|Annalt|annalt|[5]",
                "a3|1|Onlyalt|onlyalt|",
                "a4|0|Dee|dee|",
            };
            Assert.Equal(expected, await CharactersAsync(connectionString));
            Assert.Equal(
                new[]
                {
                    "a1|Bob|[1]|Bobalt||",
                    "a2|Ann|||Annalt|[5]",
                    "a3|||Onlyalt||",
                    "a4|Dee||||",
                    "a5|||||",
                },
                await AccountsAsync(connectionString));
            Assert.Equal(new[] { "a1|Bob", "a2|Ann", "a3|", "a4|Dee" }, await RosterNamesAsync(connectionString));
            // A row that was already right is not written again.
            Assert.Equal(untouched, await CharacterRowAsync(connectionString, "a4", 0));
            await AssertMirrorExactAsync(connectionString);

            // Down: nothing changes.
            await SchemaTestDatabase.MigrateToAsync(connectionString, Before);
            Assert.Equal(expected, await CharactersAsync(connectionString));
            Assert.Equal(4, await MembershipCountAsync(connectionString));

            // The previous build saves while rolled back: it writes the account columns (its copy into
            // Characters failed here, say), and a new membership with its login name as roster name.
            await SchemaTestDatabase.ExecuteAsync(connectionString,
                "UPDATE \"AspNetUsers\" SET \"AltCharacterName1\" = ' Deealt ' WHERE \"Id\" = 'a4'");
            await AddMembershipAsTheOldBuildWouldAsync(connectionString);

            // Up again: Characters takes the new alt (trimmed), the mirror holds it trimmed, and the new
            // roster name becomes the account's main (none).
            log = await MigrateAsync(connectionString, Fill);
            Assert.Contains(
                "Postgres NOTICE during migration: CharactersBecomeSource: Characters: 0 row(s) removed, 1 row(s) added or updated; " +
                "CharacterName: 0 set to the main, 0 cleared (no main); 1 account(s) mirrored; 1 roster name(s) set to the main.",
                log.Entries.Select(e => e.Message));
            Assert.Contains("a4|1|Deealt|deealt|", await CharactersAsync(connectionString));
            Assert.Contains("a4|Dee||Deealt||", await AccountsAsync(connectionString));
            Assert.Equal(5, await MembershipCountAsync(connectionString));
            await AssertMirrorExactAsync(connectionString);

            // Once more by hand: nothing left to write.
            await using (var c = new NpgsqlConnection(connectionString))
            {
                await c.OpenAsync();
                var notices = new List<string>();
                c.Notice += (_, e) => notices.Add(e.Notice.MessageText);
                await using var command = c.CreateCommand();
                command.CommandText = LinkshellManagerDiscordApp.Migrations.CharactersBecomeSource.FillSql;
                await command.ExecuteNonQueryAsync();
                Assert.Contains(
                    "CharactersBecomeSource: Characters: 0 row(s) removed, 0 row(s) added or updated; " +
                    "CharacterName: 0 set to the main, 0 cleared (no main); 0 account(s) mirrored; 0 roster name(s) set to the main.",
                    notices);
            }

            // Built from Baseline to here, the database is healthy.
            await using (var db = SchemaTestDatabase.CreateContext(connectionString))
            {
                var health = new MigrationNoticeLoggingTests.CapturingLogger();
                Assert.Equal(0, await MigrationHealth.LogProblemsAsync(db, health, CancellationToken.None));
            }
        }
        finally
        {
            await SchemaTestDatabase.DropAsync(_pg, connectionString);
        }
    }

    // Names with letters outside ASCII (production has one, pre-check A10). The writer lower-cases in
    // C# (ToLowerInvariant), the previous build's copy and this migration in SQL (lower()); they must
    // agree, or the previous build would rewrite such a row after a rollback.
    public static readonly string[] NonAsciiNames =
    {
        "Émile", "Ærith", "Ñandú", "Ølaf", "Straße", "Maïwenn", "Åsa", "Zoë",
    };

    // Step 1 is the previous build's copy, frozen: for any account it gives the rows
    // CharacterSync.DesiredRows (the same rules in C#) gives.
    [Fact]
    public async Task TheFrozenCopy_GivesTheRowsCharacterSyncDesiredRowsGives()
    {
        var connectionString = await SchemaTestDatabase.CreateAsync(_pg, "chars_source_parity_");
        try
        {
            await SchemaTestDatabase.MigrateToAsync(connectionString, Before);
            var accounts = new (string Id, string? Main, string? Alt1, string? Alt2)[]
            {
                ("p1", " Twin ", "TWIN", "Other\t"),
                ("p2", "   ", "Alt", new string('y', 257)),
                ("p3", new string('z', 256), null, "z"),
                ("p4", NonAsciiNames[0], NonAsciiNames[1], NonAsciiNames[2]),
                ("p5", NonAsciiNames[3], NonAsciiNames[4], NonAsciiNames[5]),
                ("p6", NonAsciiNames[6], NonAsciiNames[7], NonAsciiNames[6].ToLowerInvariant()),
                ("p7", null, null, null),
            };
            foreach (var (id, main, alt1, alt2) in accounts)
            {
                await SchemaTestDatabase.ExecuteAsync(connectionString, PostgresFixture.InsertAccountSql(id));
                await SchemaTestDatabase.ExecuteAsync(connectionString,
                    $"UPDATE \"AspNetUsers\" SET \"CharacterName\" = {Literal(main)}, \"JobLevels\" = '[1]', " +
                    $"\"AltCharacterName1\" = {Literal(alt1)}, \"AltCharacterName2\" = {Literal(alt2 is { Length: > 64 } ? null : alt2)}, " +
                    $"\"Alt2CraftLevels\" = '[3]' WHERE \"Id\" = '{id}'");
            }
            // alt 2 is varchar(64): the 257-character name goes into the main of another account.
            await SchemaTestDatabase.ExecuteAsync(connectionString, PostgresFixture.InsertAccountSql("p8"));
            await SchemaTestDatabase.ExecuteAsync(connectionString,
                $"UPDATE \"AspNetUsers\" SET \"CharacterName\" = '{new string('y', 257)}', \"AltCharacterName1\" = 'Keep' WHERE \"Id\" = 'p8'");

            var expected = new List<string>();
            await using (var db = SchemaTestDatabase.CreateContext(connectionString))
            {
                foreach (var user in (await db.Users.AsNoTracking().ToListAsync()).OrderBy(u => u.Id, System.StringComparer.Ordinal))
                {
                    expected.AddRange(CharacterSync.DesiredRows(user).Select(row =>
                        $"{user.Id}|{row.Slot}|{row.Name}|{row.NormalizedName}|{Json(row.JobLevels)}|{Json(row.CraftLevels)}"));
                }
            }

            await MigrateAsync(connectionString, Fill);

            var actual = await QueryAsync(connectionString,
                "SELECT concat_ws('|', \"AppUserId\", \"Slot\", \"Name\", \"NormalizedName\", coalesce(\"JobLevels\"::text, ''), coalesce(\"CraftLevels\"::text, '')) " +
                "FROM \"Characters\" ORDER BY \"AppUserId\", \"Slot\"");
            Assert.Equal(expected, actual);
            await AssertMirrorExactAsync(connectionString);

            // And SQL lower() is C#'s ToLowerInvariant for each name.
            foreach (var name in NonAsciiNames)
            {
                var lowered = (await QueryAsync(connectionString, $"SELECT lower({Literal(name)})")).Single();
                Assert.Equal(name.ToLowerInvariant(), lowered);
            }
        }
        finally
        {
            await SchemaTestDatabase.DropAsync(_pg, connectionString);
        }
    }

    private static string Literal(string? value) =>
        value is null ? "NULL" : "'" + value.Replace("'", "''") + "'";

    // jsonb prints arrays with a space after each comma.
    private static string Json(int[]? values) => values is null ? "" : "[" + string.Join(", ", values) + "]";

    // Five accounts as the previous build could leave them (see the class comment), with memberships
    // in linkshell "LS". Accounts and Characters rows are written with raw SQL (the save hook would
    // tidy them); linkshells and memberships through EF (the schema at Before is the current model,
    // this migration being data only), then the roster names are made messy with raw SQL.
    private static async Task SeedAsync(string connectionString)
    {
        foreach (var id in new[] { "a1", "a2", "a3", "a4", "a5" })
        {
            await SchemaTestDatabase.ExecuteAsync(connectionString, PostgresFixture.InsertAccountSql(id));
        }
        foreach (var statement in new[]
        {
            // a1: a main with spaces around it and jobs, an alt; no Characters rows at all.
            "UPDATE \"AspNetUsers\" SET \"CharacterName\" = ' Bob ', \"JobLevels\" = '[1]', \"AltCharacterName1\" = 'Bobalt' WHERE \"Id\" = 'a1'",
            // a2: alt 1 repeats the main; alt 2 has jobs and a no-break and an ideographic space around
            // it (.NET trims both); Characters has a stale alt 2.
            $"UPDATE \"AspNetUsers\" SET \"CharacterName\" = 'Ann', \"AltCharacterName1\" = 'ann ', \"AltCharacterName2\" = '{(char)0x00A0}Annalt{(char)0x3000}', \"Alt2JobLevels\" = '[5]' WHERE \"Id\" = 'a2'",
            "INSERT INTO \"Characters\" (\"AppUserId\", \"Slot\", \"Name\", \"NormalizedName\", \"UpdatedAt\") VALUES " +
                "('a2', 0, 'Ann', 'ann', '2026-01-01'), ('a2', 2, 'Oldalt', 'oldalt', '2026-01-01')",
            // a3: a blank main that still has jobs, and an alt.
            "UPDATE \"AspNetUsers\" SET \"CharacterName\" = '   ', \"JobLevels\" = '[9]', \"AltCharacterName1\" = 'Onlyalt' WHERE \"Id\" = 'a3'",
            // a4: already right.
            "UPDATE \"AspNetUsers\" SET \"CharacterName\" = 'Dee' WHERE \"Id\" = 'a4'",
            "INSERT INTO \"Characters\" (\"AppUserId\", \"Slot\", \"Name\", \"NormalizedName\", \"UpdatedAt\") VALUES ('a4', 0, 'Dee', 'dee', '2026-01-01')",
            // a5: a main too long to copy, and jobs and crafts on every slot but no other name (step 3
            // must clear every one of them: the mirror is an exact copy of Characters).
            $"UPDATE \"AspNetUsers\" SET \"CharacterName\" = '{new string('x', 300)}' WHERE \"Id\" = 'a5'",
            "UPDATE \"AspNetUsers\" SET " +
                "\"StrongJobs\" = '[1]', \"MeritJobs\" = '[\"m\"]', \"CraftLevels\" = '[2]', " +
                "\"Alt1JobLevels\" = '[3]', \"Alt1StrongJobs\" = '[4]', \"Alt1MeritJobs\" = '[\"m\"]', \"Alt1CraftLevels\" = '[5]', " +
                "\"Alt2JobLevels\" = '[6]', \"Alt2StrongJobs\" = '[7]', \"Alt2MeritJobs\" = '[\"m\"]', \"Alt2CraftLevels\" = '[8]' " +
                "WHERE \"Id\" = 'a5'",
        })
        {
            await SchemaTestDatabase.ExecuteAsync(connectionString, statement);
        }

        await using (var db = SchemaTestDatabase.CreateContext(connectionString))
        {
            var linkshell = new LinkshellManagerDiscordApp.Models.Linkshell { LinkshellName = "LS" };
            db.Linkshells.Add(linkshell);
            db.Linkshells.Add(new LinkshellManagerDiscordApp.Models.Linkshell { LinkshellName = "Other" });
            await db.SaveChangesAsync();
            foreach (var account in new[] { "a1", "a2", "a3", "a4" })
            {
                db.AppUserLinkshells.Add(new LinkshellManagerDiscordApp.Models.AppUserLinkshell
                {
                    AppUserId = account, LinkshellId = linkshell.Id, LinkshellDkp = 10, Status = "Active",
                });
            }
            await db.SaveChangesAsync();
        }

        // Roster names: a1 padded, a2 right, a3 something, a4 wrong.
        foreach (var (account, roster) in new[] { ("a1", " Bob "), ("a2", "Ann"), ("a3", "Blank"), ("a4", "Wrong") })
        {
            await SchemaTestDatabase.ExecuteAsync(connectionString,
                $"UPDATE \"AppUserLinkshells\" SET \"CharacterName\" = '{roster}' WHERE \"AppUserId\" = '{account}'");
        }
    }

    // A membership the previous build adds during a rollback, with the login name as roster name.
    private static async Task AddMembershipAsTheOldBuildWouldAsync(string connectionString)
    {
        await using (var db = SchemaTestDatabase.CreateContext(connectionString))
        {
            var other = await db.Linkshells.SingleAsync(l => l.LinkshellName == "Other");
            db.AppUserLinkshells.Add(new LinkshellManagerDiscordApp.Models.AppUserLinkshell
            {
                AppUserId = "a5", LinkshellId = other.Id, LinkshellDkp = 0, Status = "Active",
            });
            await db.SaveChangesAsync();
        }
        await SchemaTestDatabase.ExecuteAsync(connectionString,
            "UPDATE \"AppUserLinkshells\" SET \"CharacterName\" = 'a5' WHERE \"AppUserId\" = 'a5'");
    }

    private static async Task AssertMirrorExactAsync(string connectionString)
    {
        await using var db = SchemaTestDatabase.CreateContext(connectionString);
        Assert.Equal(new AccountNameMirrorCheck.Result(0, 0, 0), await AccountNameMirrorCheck.CountAsync(db, CancellationToken.None));

        // Every one of the 15 old columns is an EXACT copy of its slot's row (null with no row).
        Assert.Equal(new[] { "0" }, await QueryAsync(connectionString, """
            SELECT count(*)::text
            FROM "AspNetUsers" u
            LEFT JOIN "Characters" m  ON m."AppUserId"  = u."Id" AND m."Slot"  = 0
            LEFT JOIN "Characters" a1 ON a1."AppUserId" = u."Id" AND a1."Slot" = 1
            LEFT JOIN "Characters" a2 ON a2."AppUserId" = u."Id" AND a2."Slot" = 2
            WHERE (u."CharacterName", u."JobLevels", u."StrongJobs", u."MeritJobs", u."CraftLevels",
                   u."AltCharacterName1"::text, u."Alt1JobLevels", u."Alt1StrongJobs", u."Alt1MeritJobs", u."Alt1CraftLevels",
                   u."AltCharacterName2"::text, u."Alt2JobLevels", u."Alt2StrongJobs", u."Alt2MeritJobs", u."Alt2CraftLevels")
                IS DISTINCT FROM
                  (m."Name"::text, m."JobLevels", m."StrongJobs", m."MeritJobs", m."CraftLevels",
                   a1."Name"::text, a1."JobLevels", a1."StrongJobs", a1."MeritJobs", a1."CraftLevels",
                   a2."Name"::text, a2."JobLevels", a2."StrongJobs", a2."MeritJobs", a2."CraftLevels")
            """));
    }

    private static Task<List<string>> CharactersAsync(string connectionString) => QueryAsync(connectionString,
        "SELECT concat_ws('|', \"AppUserId\", \"Slot\", \"Name\", \"NormalizedName\", coalesce(\"JobLevels\"::text, '')) " +
        "FROM \"Characters\" ORDER BY \"AppUserId\", \"Slot\"");

    // Id | CharacterName | JobLevels | AltCharacterName1 | AltCharacterName2 | Alt2JobLevels
    private static Task<List<string>> AccountsAsync(string connectionString) => QueryAsync(connectionString,
        "SELECT concat_ws('|', \"Id\", coalesce(\"CharacterName\", ''), coalesce(\"JobLevels\"::text, ''), " +
        "coalesce(\"AltCharacterName1\", ''), coalesce(\"AltCharacterName2\", ''), coalesce(\"Alt2JobLevels\"::text, '')) " +
        "FROM \"AspNetUsers\" ORDER BY \"Id\"");

    private static Task<List<string>> RosterNamesAsync(string connectionString) => QueryAsync(connectionString,
        "SELECT concat_ws('|', \"AppUserId\", coalesce(\"CharacterName\", '')) FROM \"AppUserLinkshells\" ORDER BY \"AppUserId\"");

    private static async Task<string> CharacterRowAsync(string connectionString, string account, int slot) =>
        (await QueryAsync(connectionString,
            $"SELECT concat_ws('|', \"Id\", \"Name\", \"UpdatedAt\"::text) FROM \"Characters\" WHERE \"AppUserId\" = '{account}' AND \"Slot\" = {slot}"))
        .Single();

    private static async Task<long> MembershipCountAsync(string connectionString) =>
        long.Parse((await QueryAsync(connectionString, "SELECT count(*)::text FROM \"AppUserLinkshells\"")).Single());

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
