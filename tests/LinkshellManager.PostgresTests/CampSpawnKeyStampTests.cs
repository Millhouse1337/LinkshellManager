using LinkshellManagerDiscordApp.Data;
using LinkshellManagerDiscordApp.Models;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Xunit;

namespace LinkshellManager.PostgresTests;

// Round 5 (AddCampSpawnKeys): on real Postgres the save hook stamps SpawnKey on the camp tables,
// and every key it wrote is what the migration's SQL fill would have written for that row. Since
// CampSpawnKeySwitch a linkshell keeps one Monster Setups row per spawn, so the timings get their
// own list of names (one per spawn) and a second row for a spawn is refused.
[Collection("postgres")]
public class CampSpawnKeyStampTests : IAsyncLifetime
{
    private static readonly DateTime At = new(2026, 11, 15, 12, 0, 0, DateTimeKind.Utc);

    private readonly PostgresFixture _pg;

    public CampSpawnKeyStampTests(PostgresFixture pg) => _pg = pg;

    public Task InitializeAsync() => _pg.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task EverySave_StampsWhatTheMigrationFillWould()
    {
        int linkshellId;
        await using (var db = _pg.CreateContext())
        {
            var linkshell = new Linkshell { LinkshellName = "LS" };
            db.Linkshells.Add(linkshell);
            await db.SaveChangesAsync();
            linkshellId = linkshell.Id;

            var names = new[] { "Nidhogg", " Fafnir/Nidhogg ", "King Behemoth", "Foo / Bar", "Tiamat", "", null };
            var timingNames = new[] { " Fafnir/Nidhogg ", "Cerberus", "King Behemoth", "Foo / Bar", "Tiamat", "", null };
            for (var i = 0; i < names.Length; i++)
            {
                db.Events.Add(new Event { LinkshellId = linkshellId, EventName = $"Camp {i}", AssignedMonsterName = names[i] });
                db.PartySetups.Add(new PartySetup
                {
                    LinkshellId = linkshellId, Name = $"Setup {i}", AssignedMonsterName = names[i], CreatedAt = At, UpdatedAt = At,
                });
                db.ClaimShieldCaptures.Add(new ClaimShieldCapture
                {
                    LinkshellId = linkshellId, MonsterName = names[i] ?? string.Empty, CapturedAtUtc = At, CreatedAtUtc = At,
                });
                db.LinkshellMonsterTimings.Add(new LinkshellMonsterTiming
                {
                    LinkshellId = linkshellId, MonsterName = timingNames[i] ?? string.Empty, NormalizedMonsterName = $"n{i}",
                    CooldownMinutes = 1320, CreatedAtUtc = At, UpdatedAtUtc = At, SortOrder = i,
                });
            }
            await db.SaveChangesAsync();
        }

        // An edit to the name moves the key with it.
        await using (var db = _pg.CreateContext())
        {
            var tiamat = await db.Events.SingleAsync(e => e.AssignedMonsterName == "Tiamat");
            tiamat.AssignedMonsterName = "Aspidochelone";
            var timing = await db.LinkshellMonsterTimings.SingleAsync(t => t.MonsterName == "Tiamat");
            timing.MonsterName = "Aspidochelone";
            await db.SaveChangesAsync();
        }

        // A second Monster Setups row for a spawn the linkshell already has is refused.
        await using (var db = _pg.CreateContext())
        {
            db.LinkshellMonsterTimings.Add(new LinkshellMonsterTiming
            {
                LinkshellId = linkshellId, MonsterName = "Nidhogg", NormalizedMonsterName = "nidhogg",
                CooldownMinutes = 1320, CreatedAtUtc = At, UpdatedAtUtc = At,
            });
            var refused = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
            var inner = Assert.IsType<PostgresException>(refused.InnerException);
            Assert.Equal(PostgresErrorCodes.UniqueViolation, inner.SqlState);
            Assert.Equal("IX_LinkshellMonsterTimings_LinkshellId_SpawnKey", inner.ConstraintName);
        }

        await using var c = new NpgsqlConnection(_pg.ConnectionString);
        await c.OpenAsync();
        Assert.Equal(
            "fafnir/nidhogg|fafnir/nidhogg|behemoth/king behemoth|foo/bar|adamantoise/aspidochelone",
            await ScalarAsync(c, "SELECT string_agg(\"SpawnKey\", '|' ORDER BY \"Id\") FROM \"Events\""));
        Assert.Equal(
            "fafnir/nidhogg|cerberus|behemoth/king behemoth|foo/bar|adamantoise/aspidochelone",
            await ScalarAsync(c, "SELECT string_agg(\"SpawnKey\", '|' ORDER BY \"Id\") FROM \"LinkshellMonsterTimings\""));

        // The migration's SQL agrees with every key the hook wrote, blanks included.
        var checks = new[]
        {
            ("Events", "AssignedMonsterName"),
            ("PartySetups", "AssignedMonsterName"),
            ("ClaimShieldCaptures", "MonsterName"),
            ("LinkshellMonsterTimings", "MonsterName"),
        };
        foreach (var (table, nameColumn) in checks)
        {
            Assert.Equal(7L, await ScalarAsync(c, $"SELECT count(*) FROM \"{table}\""));
            Assert.Equal(0L, await ScalarAsync(c,
                $"SELECT count(*) FROM \"{table}\" t WHERE t.\"SpawnKey\" IS DISTINCT FROM " +
                LinkshellManagerDiscordApp.Migrations.AddCampSpawnKeys.SpawnKeySql($"t.\"{nameColumn}\"")));
        }
    }

    private static async Task<object?> ScalarAsync(NpgsqlConnection c, string sql)
    {
        await using var command = c.CreateCommand();
        command.CommandText = sql;
        return await command.ExecuteScalarAsync();
    }
}
