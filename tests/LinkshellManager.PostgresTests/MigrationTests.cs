using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace LinkshellManager.PostgresTests;

// The fixture already ran every migration on an empty Postgres. Getting here at all means none of
// them failed (raw SQL included), and this checks that EF agrees nothing is left to apply. A typo in
// migration SQL now fails CI instead of the production deploy.
[Collection("postgres")]
public class MigrationTests
{
    private readonly PostgresFixture _pg;

    public MigrationTests(PostgresFixture pg) => _pg = pg;

    [Fact]
    public async Task AllMigrationsApplyToAnEmptyDatabase()
    {
        await using var db = _pg.CreateContext();

        var applied = (await db.Database.GetAppliedMigrationsAsync()).ToList();
        var pending = (await db.Database.GetPendingMigrationsAsync()).ToList();

        Assert.NotEmpty(applied);
        Assert.Empty(pending);
        Assert.Equal(db.Database.GetMigrations().Count(), applied.Count);
    }
}
