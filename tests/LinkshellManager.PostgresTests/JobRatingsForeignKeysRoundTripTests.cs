using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using LinkshellManagerDiscordApp.Data;
using LinkshellManagerDiscordApp.Migrations;
using LinkshellManagerDiscordApp.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using Xunit;

namespace LinkshellManager.PostgresTests;

// JobRatingsForeignKeys gives JobRatings real foreign keys to the linkshell, the rated account and
// the rater (all cascade), after deleting ratings that already point at something missing.
//
// Seeded at the migration before with good ratings and one orphan of each kind. Up keeps only the
// good ones and then refuses a new orphan; deleting an account or a linkshell takes its ratings
// with it. Down lets orphans in again, and Up again cleans them up and adds the keys back.
//
// Its OWN database inside the test container, like DropRedundantJobRatingsIndexRoundTripTests.
// Deleted at the end.
[Collection("postgres")]
public class JobRatingsForeignKeysRoundTripTests
{
    private const string Before = "20261114170434_FillJournalLineHolderAccounts";

    // Named, not "latest", so a later migration's Down never joins this test.
    private const string AddKeys = "20261114234838_JobRatingsForeignKeys";

    private static readonly string[] ForeignKeys =
    {
        "FK_JobRatings_Linkshells_LinkshellId",
        "FK_JobRatings_AspNetUsers_TargetAppUserId",
        "FK_JobRatings_AspNetUsers_RaterAppUserId",
    };

    private static readonly string[] Indexes =
    {
        "IX_JobRatings_TargetAppUserId",
        "IX_JobRatings_RaterAppUserId",
    };

    private const string Target = "target";
    private const string Rater = "rater";
    private const string Leaver = "leaver";
    private const string Ghost = "ghost-account";
    private const int MissingLinkshell = 999_999;

    private readonly PostgresFixture _pg;

    public JobRatingsForeignKeysRoundTripTests(PostgresFixture pg) => _pg = pg;

    [Fact]
    public async Task JobRatingsForeignKeys_GoesUpDownAndUpAgain()
    {
        var databaseName = "jobratings_fk_roundtrip_" + Guid.NewGuid().ToString("N")[..8];
        await ExecuteAsync(_pg.ConnectionString, $"CREATE DATABASE \"{databaseName}\"");

        var connectionString = new NpgsqlConnectionStringBuilder(_pg.ConnectionString)
        {
            Database = databaseName,
            // No pooled connections left behind, so DROP DATABASE at the end is not blocked.
            Pooling = false,
        }.ConnectionString;

        try
        {
            await MigrateAsync(connectionString, Before);
            var (main, other) = await SeedAsync(connectionString);

            // Up: the three orphans go, the four good ratings stay, and the keys are in place.
            await MigrateAsync(connectionString, AddKeys);
            await using (var c = await OpenAsync(connectionString))
            {
                await AssertKeysAsync(c, present: true);
                Assert.Equal(4L, await CountAsync(c));
                Assert.Equal(0L, await OrphanCountAsync(c));

                // A new orphan of each kind is refused.
                foreach (var orphan in Orphans(main))
                {
                    var refused = await Assert.ThrowsAsync<PostgresException>(() => ScalarAsync(c, orphan));
                    Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, refused.SqlState);
                }

                // Deleting an account takes the ratings OF it and BY it; deleting a linkshell takes
                // its ratings. Only the rating between the two staying accounts in the main
                // linkshell is left.
                await ScalarAsync(c, $"DELETE FROM \"AspNetUsers\" WHERE \"Id\" = '{Leaver}'");
                Assert.Equal(2L, await CountAsync(c));
                await ScalarAsync(c, $"DELETE FROM \"Linkshells\" WHERE \"Id\" = {other}");
                Assert.Equal(1L, await CountAsync(c));
                Assert.Equal(1L, await ScalarAsync(c,
                    $"SELECT count(*) FROM \"JobRatings\" WHERE \"LinkshellId\" = {main} " +
                    $"AND \"TargetAppUserId\" = '{Target}' AND \"RaterAppUserId\" = '{Rater}'"));
            }

            // Down: the keys and indexes go, so an orphan can be saved again; the rating stays.
            await MigrateAsync(connectionString, Before);
            await using (var c = await OpenAsync(connectionString))
            {
                await AssertKeysAsync(c, present: false);
                await ScalarAsync(c, Orphans(main)[0]);
                Assert.Equal(2L, await CountAsync(c));

                // The delete is safe to run again, and says in the log how many it removed.
                var notices = new List<string>();
                c.Notice += (_, e) => notices.Add(e.Notice.MessageText);
                await ScalarAsync(c, JobRatingsForeignKeys.DeleteOrphansSql);
                Assert.Contains("JobRatingsForeignKeys: deleted 1 orphan job rating(s).", notices);
                await ScalarAsync(c, JobRatingsForeignKeys.DeleteOrphansSql);
                Assert.Contains("JobRatingsForeignKeys: deleted 0 orphan job rating(s).", notices);

                // Leave orphans behind for Up again, as an old build running after a rollback could.
                foreach (var orphan in Orphans(main))
                {
                    await ScalarAsync(c, orphan);
                }
                Assert.Equal(4L, await CountAsync(c));
            }

            // Up again: those orphans go too, and the keys come back.
            await MigrateAsync(connectionString, AddKeys);
            await using (var c = await OpenAsync(connectionString))
            {
                await AssertKeysAsync(c, present: true);
                Assert.Equal(1L, await CountAsync(c));
                Assert.Equal(0L, await OrphanCountAsync(c));
            }
        }
        finally
        {
            await ExecuteAsync(_pg.ConnectionString, $"DROP DATABASE IF EXISTS \"{databaseName}\" WITH (FORCE)");
        }
    }

    // Two linkshells through EF, three accounts by SQL (see InsertAccountSql).
    // Ratings by SQL: four good ones and three orphans the old schema let in.
    private static async Task<(int Main, int Other)> SeedAsync(string connectionString)
    {
        int main, other;
        // Accounts by SQL: the EF model has account columns later migrations add.
        foreach (var id in new[] { Target, Rater, Leaver })
        {
            await ExecuteAsync(connectionString, PostgresFixture.InsertAccountSql(id));
        }
        await using (var db = NewContext(connectionString))
        {
            var mainLinkshell = new Linkshell { LinkshellName = "Main LS" };
            var otherLinkshell = new Linkshell { LinkshellName = "Other LS" };
            db.Linkshells.AddRange(mainLinkshell, otherLinkshell);
            await db.SaveChangesAsync();
            main = mainLinkshell.Id;
            other = otherLinkshell.Id;
        }

        await using var c = await OpenAsync(connectionString);
        // Good: a peer rating, a self rating and a player-comment row, one of each leaver's
        // sides, and one in the other linkshell.
        await ScalarAsync(c, Rating(main, Target, Rater, jobIndex: 3));
        await ScalarAsync(c, Rating(main, Leaver, Rater, jobIndex: JobRating.PlayerCommentJobIndex));
        await ScalarAsync(c, Rating(main, Target, Leaver, jobIndex: 3));
        await ScalarAsync(c, Rating(other, Target, Target, jobIndex: 0));
        foreach (var orphan in Orphans(main))
        {
            await ScalarAsync(c, orphan);
        }
        Assert.Equal(7L, await CountAsync(c));
        Assert.Equal(3L, await OrphanCountAsync(c));
        return (main, other);
    }

    // One rating per missing thing: the linkshell, the rated account, the rater.
    private static string[] Orphans(int main) => new[]
    {
        Rating(MissingLinkshell, Target, Rater, jobIndex: 5),
        Rating(main, Ghost, Rater, jobIndex: 5),
        Rating(main, Target, Ghost, jobIndex: 5),
    };

    private static string Rating(int linkshellId, string target, string rater, int jobIndex) =>
        "INSERT INTO \"JobRatings\" (\"LinkshellId\", \"CharacterSlot\", \"TargetAppUserId\", \"RaterAppUserId\", " +
        "\"JobIndex\", \"Gear\", \"Skill\", \"HasRelic\", \"UpdatedAt\") " +
        $"VALUES ({linkshellId}, 0, '{target}', '{rater}', {jobIndex}, 4, 5, false, now())";

    private static async Task AssertKeysAsync(NpgsqlConnection c, bool present)
    {
        foreach (var name in ForeignKeys)
        {
            var count = await ScalarAsync(c,
                $"SELECT count(*) FROM pg_constraint WHERE conname = '{name}' AND contype = 'f' AND confdeltype = 'c'");
            Assert.True((long)count! == (present ? 1L : 0L), $"{name} present={present}");
        }
        foreach (var name in Indexes)
        {
            var exists = (bool)(await ScalarAsync(c, $"SELECT to_regclass('public.\"{name}\"') IS NOT NULL"))!;
            Assert.True(exists == present, $"{name} present={present}");
        }
    }

    private static async Task<long> CountAsync(NpgsqlConnection c) =>
        (long)(await ScalarAsync(c, "SELECT count(*) FROM \"JobRatings\""))!;

    private static async Task<long> OrphanCountAsync(NpgsqlConnection c) =>
        (long)(await ScalarAsync(c,
            "SELECT count(*) FROM \"JobRatings\" j " +
            "WHERE NOT EXISTS (SELECT 1 FROM \"Linkshells\" l WHERE l.\"Id\" = j.\"LinkshellId\") " +
            "OR NOT EXISTS (SELECT 1 FROM \"AspNetUsers\" u WHERE u.\"Id\" = j.\"TargetAppUserId\") " +
            "OR NOT EXISTS (SELECT 1 FROM \"AspNetUsers\" u WHERE u.\"Id\" = j.\"RaterAppUserId\")"))!;

    private static ApplicationDbContext NewContext(string connectionString)
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseNpgsql(connectionString)
            .UseApplicationServiceProvider(PostgresFixture.IdentityServices)
            .Options;
        return new ApplicationDbContext(options);
    }

    // Moves the schema of the given database to exactly this migration, forwards or backwards.
    private static async Task MigrateAsync(string connectionString, string targetMigration)
    {
        await using var db = NewContext(connectionString);
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

    private static async Task ExecuteAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }
}
