using LinkshellManagerDiscordApp.Migrations;
using LinkshellManagerDiscordApp.Models;
using LinkshellManagerDiscordApp.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Xunit;

namespace LinkshellManager.PostgresTests;

// The save hook stamps SpawnKey / CooldownMinutes / IntervalMinutes (AddTodDurationsAndSpawnKeys)
// on real Postgres, through a real writer (TodRecorder, the path the addon and the ToD forms use),
// a plain edit, the approval queue's table and a standing board. Afterwards the migration's own fill
// finds nothing to change: the hook and the SQL wrote the same values.
[Collection("postgres")]
public class TodDerivedColumnsStampTests : IAsyncLifetime
{
    private static readonly DateTime KilledAt = new(2026, 11, 1, 12, 0, 0, DateTimeKind.Utc);

    private readonly PostgresFixture _pg;

    public TodDerivedColumnsStampTests(PostgresFixture pg) => _pg = pg;

    public Task InitializeAsync() => _pg.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task EveryWriter_StampsWhatTheMigrationFillWould()
    {
        int linkshellId;
        await using (var db = _pg.CreateContext())
        {
            var linkshell = new Linkshell { LinkshellName = "LS" };
            db.Linkshells.Add(linkshell);
            await db.SaveChangesAsync();
            linkshellId = linkshell.Id;
        }

        // A ToD recorded the way the addon records one, with the typed cooldown and interval.
        await using (var db = _pg.CreateContext())
        {
            var recorder = new TodRecorder(db, new MonsterTimingResolver(db), NullLogger<TodRecorder>.Instance);
            await recorder.RecordAsync(
                new TodRecordInput(linkshellId, "Nidhogg", Hq: null, KilledAt, AdditionalSeconds: null, PopWindow: null,
                    Claim: true, Killed: null, DayNumber: null, TodSources.Addon, ActorAppUserId: null)
                {
                    Cooldown = "22 Hour",
                    Interval = "10 Min",
                },
                CancellationToken.None);
        }

        await using (var db = _pg.CreateContext())
        {
            var tod = await db.Tods.SingleAsync();
            Assert.Equal("fafnir/nidhogg", tod.SpawnKey);
            Assert.Equal(1320, tod.CooldownMinutes);
            Assert.Equal(10, tod.IntervalMinutes);

            // An edit to the text moves the derived values with it.
            tod.MonsterName = "Tiamat";
            tod.Cooldown = "84 Hour";
            tod.Interval = "1 Hour";

            db.PendingTodSubmissions.Add(new PendingTodSubmission
            {
                LinkshellId = linkshellId, SubmittedAtUtc = KilledAt, MonsterName = " King Behemoth ",
                Cooldown = "22", Interval = "10",
            });
            db.HnmRecurringBoards.Add(new HnmRecurringBoard
            {
                LinkshellId = linkshellId, MonsterName = "Adamantoise/Aspidochelone", CreatedAt = KilledAt, UpdatedAt = KilledAt,
            });
            await db.SaveChangesAsync();
        }

        await using (var c = new NpgsqlConnection(_pg.ConnectionString))
        {
            await c.OpenAsync();
            Assert.Equal("tiamat|5040|60", await ScalarAsync(c,
                "SELECT concat_ws('|', \"SpawnKey\", \"CooldownMinutes\", \"IntervalMinutes\") FROM \"Tods\""));
            Assert.Equal("behemoth/king behemoth|1320|10", await ScalarAsync(c,
                "SELECT concat_ws('|', \"SpawnKey\", \"CooldownMinutes\", \"IntervalMinutes\") FROM \"PendingTodSubmissions\""));
            Assert.Equal("adamantoise/aspidochelone", await ScalarAsync(c, "SELECT \"SpawnKey\" FROM \"HnmRecurringBoards\""));

            // The migration's fill agrees with every value the hook wrote.
            var notices = new List<string>();
            c.Notice += (_, e) => notices.Add(e.Notice.MessageText);
            await ScalarAsync(c, AddTodDurationsAndSpawnKeys.FillSql);
            Assert.Contains("AddTodDurationsAndSpawnKeys: filled 0 ToD(s), 0 pending ToD(s), 0 standing board(s).", notices);
        }
    }

    private static async Task<object?> ScalarAsync(NpgsqlConnection c, string sql)
    {
        await using var command = c.CreateCommand();
        command.CommandText = sql;
        return await command.ExecuteScalarAsync();
    }
}
