using LinkshellManagerDiscordApp.Models;
using LinkshellManagerDiscordApp.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace LinkshellManager.PostgresTests;

// Round 5 (CampSpawnKeySwitch): the camp lookups compare Events.SpawnKey on real Postgres. A
// "Nidhogg" ToD finds a camp saved under the combined label or under the other half, and never
// another spawn's camp or a camp with no monster (no key).
[Collection("postgres")]
public class CampSpawnKeyLookupPostgresTests : IAsyncLifetime
{
    private static readonly DateTime Repop = new(2026, 11, 20, 20, 0, 0, DateTimeKind.Utc);

    private readonly PostgresFixture _pg;

    public CampSpawnKeyLookupPostgresTests(PostgresFixture pg) => _pg = pg;

    public Task InitializeAsync() => _pg.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [Theory]
    [InlineData("Fafnir/Nidhogg")]
    [InlineData("Fafnir")]
    [InlineData(" fafnir ")]
    public async Task ANidhoggToD_FindsTheFafnirCamp_AndNoOther(string campMonster)
    {
        int linkshellId;
        await using (var db = _pg.CreateContext())
        {
            var linkshell = new Linkshell { LinkshellName = "LS" };
            db.Linkshells.Add(linkshell);
            await db.SaveChangesAsync();
            linkshellId = linkshell.Id;

            db.HnmRecurringBoards.Add(new HnmRecurringBoard { LinkshellId = linkshellId, MonsterName = "Fafnir/Nidhogg", Enabled = true, LeadHours = 1 });
            db.Tods.Add(new Tod { LinkshellId = linkshellId, MonsterName = "Nidhogg", Time = Repop.AddHours(-84), RepopTime = Repop });
            // Live camps: the Fafnir one, another spawn's (a later day, so it would win if it matched)
            // and one with no monster.
            db.Events.AddRange(
                new Event
                {
                    LinkshellId = linkshellId, EventName = "Tuesday camp", EventType = "HNM", AssignedMonsterName = campMonster,
                    DayNumber = 4, StartTime = Repop.AddHours(-3), CommencementStartTime = Repop.AddHours(-3),
                },
                new Event
                {
                    LinkshellId = linkshellId, EventName = "Behe camp", EventType = "HNM", AssignedMonsterName = "Behemoth/King Behemoth",
                    DayNumber = 6, StartTime = Repop.AddHours(-2), CommencementStartTime = Repop.AddHours(-2),
                },
                new Event
                {
                    LinkshellId = linkshellId, EventName = "No monster", EventType = "HNM",
                    DayNumber = 7, StartTime = Repop.AddHours(-1), CommencementStartTime = Repop.AddHours(-1),
                },
                // Parked boards: the Fafnir one and another spawn's.
                new Event
                {
                    LinkshellId = linkshellId, EventName = "Parked faf", EventType = "HNM", AssignedMonsterName = campMonster,
                    StartTime = Repop.AddHours(-90), HnmDefeatedAt = Repop.AddHours(-84),
                },
                new Event
                {
                    LinkshellId = linkshellId, EventName = "Parked behe", EventType = "HNM", AssignedMonsterName = "Behemoth",
                    StartTime = Repop.AddHours(-90), HnmDefeatedAt = Repop.AddHours(-84),
                });
            await db.SaveChangesAsync();
        }

        await using (var db = _pg.CreateContext())
        {
            Assert.Equal(4, await HnmDayCycle.DayForAddonTodAsync(db, linkshellId, "Nidhogg", 2, CancellationToken.None));
            Assert.Equal(2, await HnmDayCycle.DayForAddonTodAsync(db, linkshellId, "Aspidochelone", 2, CancellationToken.None));

            await HnmRecurringBoardService.SyncParkedBoardsForTodAsync(db, linkshellId, "Nidhogg", CancellationToken.None, Repop.AddHours(-10));
        }

        await using (var db = _pg.CreateContext())
        {
            var parked = await db.Events.AsNoTracking()
                .Where(e => e.HnmDefeatedAt != null)
                .ToDictionaryAsync(e => e.EventName!, e => e.StartTime);
            Assert.Equal(Repop, parked["Parked faf"]);
            Assert.Equal(Repop.AddHours(-90), parked["Parked behe"]);
        }
    }
}
