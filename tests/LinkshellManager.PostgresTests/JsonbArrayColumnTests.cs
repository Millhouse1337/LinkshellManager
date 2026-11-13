using System.Threading.Tasks;
using LinkshellManagerDiscordApp.Models;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace LinkshellManager.PostgresTests;

// The profile job levels, merits and relic names are int[] / string[] kept in jsonb columns.
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
            db.Users.Add(new AppUser
            {
                Id = "user-1",
                UserName = "user-1",
                Alt1JobLevels = new[] { 75, 0, 37 },
                Alt1MeritJobs = new[] { "WAR", "MNK" },
            });
            await db.SaveChangesAsync();
        }

        await using (var db = _pg.CreateContext())
        {
            var levels = await db.Database
                .SqlQueryRaw<string>("SELECT \"Alt1JobLevels\"::text AS \"Value\" FROM \"AspNetUsers\" WHERE \"Id\" = 'user-1'")
                .SingleAsync();
            var merits = await db.Database
                .SqlQueryRaw<string>("SELECT \"Alt1MeritJobs\"::text AS \"Value\" FROM \"AspNetUsers\" WHERE \"Id\" = 'user-1'")
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
            db.Users.Add(new AppUser { Id = "user-1", UserName = "user-1" });
            await db.SaveChangesAsync();

            // What the EF 8 build left behind in production.
            await db.Database.ExecuteSqlRawAsync(
                "UPDATE \"AspNetUsers\" SET \"Alt1JobLevels\" = '[1,2,3]'::jsonb, " +
                "\"Alt1MeritJobs\" = '[\"WAR\",\"MNK\"]'::jsonb WHERE \"Id\" = 'user-1'");
        }

        await using (var db = _pg.CreateContext())
        {
            var user = await db.Users.SingleAsync(u => u.Id == "user-1");
            Assert.Equal(new[] { 1, 2, 3 }, user.Alt1JobLevels);
            Assert.Equal(new[] { "WAR", "MNK" }, user.Alt1MeritJobs);

            // Never set: still a database NULL, not the JSON text "null".
            Assert.Null(user.Alt2JobLevels);
            var isSqlNull = await db.Database
                .SqlQueryRaw<bool>("SELECT \"Alt2JobLevels\" IS NULL AS \"Value\" FROM \"AspNetUsers\" WHERE \"Id\" = 'user-1'")
                .SingleAsync();
            Assert.True(isSqlNull);
        }
    }
}
