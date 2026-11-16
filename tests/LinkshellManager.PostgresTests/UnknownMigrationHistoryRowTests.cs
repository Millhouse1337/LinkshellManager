using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LinkshellManagerDiscordApp.Data;
using LinkshellManagerDiscordApp.Models;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Xunit;

namespace LinkshellManager.PostgresTests;

// A history row this build does not know must not stop it. Written before the migration squash
// (Round 4, D11) to prove the rollback: the squash build ADDS a Baseline row and keeps every old row,
// so the build before it (commit 4b05ee2, which had this same test) sees one id it has never heard of.
// It passed there, so the D11 rollback is a plain swap, with no SQL. Since the squash it also covers
// the other side: this build's history on production holds the 173 old ids it no longer knows
// (MigrationBaselineAdoptionTests), and the next squash or rollback will add more. This proves
// Program.cs's start-up path (Database.Migrate, then the Characters re-copy) runs without error,
// sees nothing pending, changes nothing in the schema, and the app can read and write.
//
// Its OWN database inside the test container (SchemaTestDatabase), dropped at the end.
[Collection("postgres")]
public class UnknownMigrationHistoryRowTests
{
    // Sorts after every real migration id, like a Baseline made later would.
    private const string FutureMigrationId = "29991231000000_Future";

    private readonly PostgresFixture _pg;

    public UnknownMigrationHistoryRowTests(PostgresFixture pg) => _pg = pg;

    [Fact]
    public async Task TheBuildStarts_WithAnUnknownNewerRowInTheHistory()
    {
        var connectionString = await SchemaTestDatabase.CreateAsync(_pg, "future_row_");
        try
        {
            await SchemaTestDatabase.MigrateToLatestAsync(connectionString);
            var before = await SchemaTestDatabase.FingerprintAsync(connectionString);
            await SchemaTestDatabase.ExecuteAsync(connectionString,
                "INSERT INTO \"__EFMigrationsHistory\" (\"MigrationId\", \"ProductVersion\") " +
                $"SELECT '{FutureMigrationId}', max(\"ProductVersion\") FROM \"__EFMigrationsHistory\"");

            await using (var db = SchemaTestDatabase.CreateContext(connectionString))
            {
                // What Program.cs runs at start-up, in the same order. The synchronous Migrate is
                // the one Program.cs calls; it would throw here if EF refused the unknown row.
                db.Database.Migrate();

                Assert.Empty(db.Database.GetPendingMigrations());
                var applied = db.Database.GetAppliedMigrations().ToList();
                Assert.Contains(FutureMigrationId, applied);
                // Every migration this build knows is still seen as applied.
                Assert.Empty(db.Database.GetMigrations().Except(applied));
            }

            // Nothing in the schema moved, and the unknown row is left alone.
            Assert.Equal(before, await SchemaTestDatabase.FingerprintAsync(connectionString));
            await using (var c = new NpgsqlConnection(connectionString))
            {
                await c.OpenAsync();
                await using var command = c.CreateCommand();
                command.CommandText = $"SELECT count(*) FROM \"__EFMigrationsHistory\" WHERE \"MigrationId\" = '{FutureMigrationId}'";
                Assert.Equal(1L, (long)(await command.ExecuteScalarAsync())!);
            }

            // And the app works: a save (with its hooks, including the Characters copy) and a read.
            await using (var db = SchemaTestDatabase.CreateContext(connectionString))
            {
                db.Users.Add(new AppUser { Id = "after-rollback", UserName = "after-rollback", CharacterName = "Aeris" });
                await db.SaveChangesAsync();
            }
            await using (var db = SchemaTestDatabase.CreateContext(connectionString))
            {
                Assert.Equal("Aeris", (await db.Characters.AsNoTracking().SingleAsync(ch => ch.AppUserId == "after-rollback")).Name);
            }
        }
        finally
        {
            await SchemaTestDatabase.DropAsync(_pg, connectionString);
        }
    }
}
