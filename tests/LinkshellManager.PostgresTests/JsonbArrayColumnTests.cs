using System.Threading.Tasks;
using LinkshellManagerDiscordApp.Models;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace LinkshellManager.PostgresTests;

// The profile job levels, merits and relic names are int[] / string[] kept in jsonb columns (since
// Round 5 on the Characters table: the old account copies were dropped by Round5Contract).
// EF Core 10 reads and writes them a new way (as a "primitive collection" turned into JSON text)
// where EF 8 handed the array to Npgsql. Production already holds rows written the old way, so
// these pin that both directions still agree: what we write is the same JSON array as before, and
// an old row still reads back as the same array.
[Collection("postgres")]
public class JsonbArrayColumnTests : IAsyncLifetime
{
    private readonly PostgresFixture _pg;

    public JsonbArrayColumnTests(PostgresFixture pg) => _pg = pg;

    public Task InitializeAsync() => _pg.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task SavedArraysAreStoredAsPlainJsonArrays()
    {
        await using (var db = _pg.CreateContext())
        {
            db.Users.Add(new AppUser { Id = "user-1", UserName = "user-1", CharacterName = "Main" });
            db.Characters.Add(TestCharacters.Row("user-1", Character.Alt1Slot, "Altone",
                jobLevels: new[] { 75, 0, 37 }, meritJobs: new[] { "WAR", "MNK" }));
            await db.SaveChangesAsync();
        }

        await using (var db = _pg.CreateContext())
        {
            var levels = await db.Database
                .SqlQueryRaw<string>("SELECT \"JobLevels\"::text AS \"Value\" FROM \"Characters\" WHERE \"AppUserId\" = 'user-1' AND \"Slot\" = 1")
                .SingleAsync();
            var merits = await db.Database
                .SqlQueryRaw<string>("SELECT \"MeritJobs\"::text AS \"Value\" FROM \"Characters\" WHERE \"AppUserId\" = 'user-1' AND \"Slot\" = 1")
                .SingleAsync();

            // jsonb prints arrays with a space after each comma.
            Assert.Equal("[75, 0, 37]", levels);
            Assert.Equal("[\"WAR\", \"MNK\"]", merits);
        }
    }

    [Fact]
    public async Task RowsWrittenTheOldWayStillReadBack()
    {
        await using (var db = _pg.CreateContext())
        {
            db.Users.Add(new AppUser { Id = "user-1", UserName = "user-1", CharacterName = "Main" });
            await db.SaveChangesAsync();

            // What the EF 8 build left behind in production (copied into Characters by AddCharacters).
            await db.Database.ExecuteSqlRawAsync(
                "UPDATE \"Characters\" SET \"JobLevels\" = '[1,2,3]'::jsonb, " +
                "\"MeritJobs\" = '[\"WAR\",\"MNK\"]'::jsonb WHERE \"AppUserId\" = 'user-1'");
        }

        await using (var db = _pg.CreateContext())
        {
            var main = await db.Characters.SingleAsync(c => c.AppUserId == "user-1");
            Assert.Equal(new[] { 1, 2, 3 }, main.JobLevels);
            Assert.Equal(new[] { "WAR", "MNK" }, main.MeritJobs);

            // Never set: still a database NULL, not the JSON text "null".
            Assert.Null(main.CraftLevels);
            var isSqlNull = await db.Database
                .SqlQueryRaw<bool>("SELECT \"CraftLevels\" IS NULL AS \"Value\" FROM \"Characters\" WHERE \"AppUserId\" = 'user-1'")
                .SingleAsync();
            Assert.True(isSqlNull);
        }
    }

    // The main character's jobs: written to its Characters row (CharacterWriter, Round 5) as JSON the
    // app reads back as the same arrays. (The old membership copies were dropped by Round4Contract, the
    // old account copies by Round5Contract.)
    [Fact]
    public async Task MainJobs_AreWrittenAsJsonOnTheCharactersRow()
    {
        await using (var db = _pg.CreateContext())
        {
            var linkshell = new Linkshell { LinkshellName = "LS" };
            var user = new AppUser { Id = "user-1", UserName = "user-1" };
            db.Linkshells.Add(linkshell);
            db.Users.Add(user);
            LinkshellManagerDiscordApp.Services.CharacterWriter.ForNewAccount(db, user).SetName(Character.MainSlot, "Main");
            await db.SaveChangesAsync();
            var membership = new AppUserLinkshell { LinkshellId = linkshell.Id, AppUserId = user.Id };
            db.AppUserLinkshells.Add(membership);
            await db.SaveChangesAsync();

            var characters = await LinkshellManagerDiscordApp.Services.CharacterWriter.LoadAsync(db, user.Id, default);
            characters.SetJobs(Character.MainSlot, new[] { 0, 75, 37 }, new[] { 0, 1, 0 }, new[] { "", "Axe x5" });
            await db.SaveChangesAsync();
        }

        await using (var db = _pg.CreateContext())
        {
            var row = await db.Database
                .SqlQueryRaw<string>(
                    "SELECT concat_ws('|', \"JobLevels\"::text, \"StrongJobs\"::text, \"MeritJobs\"::text) AS \"Value\" " +
                    "FROM \"Characters\" WHERE \"AppUserId\" = 'user-1' AND \"Slot\" = 0")
                .SingleAsync();
            Assert.Equal("[0, 75, 37]|[0, 1, 0]|[\"\", \"Axe x5\"]", row);
            var main = await db.Characters.SingleAsync(c => c.AppUserId == "user-1" && c.Slot == Character.MainSlot);
            Assert.Equal(new[] { 0, 75, 37 }, main.JobLevels);
            Assert.Equal(new[] { "", "Axe x5" }, main.MeritJobs);
        }
    }
}
