using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LinkshellManagerDiscordApp.Models;
using LinkshellManagerDiscordApp.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace LinkshellManager.PostgresTests;

// Round 4 deploy 3: a new key-item tick no longer writes ChartMemberKeyItems.MembershipId
// (ROUND4-DROP). On the real database that column is still there with its old unique index
// (LinkshellId, Board, KeyItemName, MembershipId). Postgres never treats two NULLs as equal, so the
// old index cannot refuse a NULL tick; the per-account index is what still stops a second tick.
[Collection("postgres")]
public class KeyItemTickWithoutMembershipTests : IAsyncLifetime
{
    private const string Lantern = "Hydra Corps Lantern";

    private readonly PostgresFixture _pg;

    public KeyItemTickWithoutMembershipTests(PostgresFixture pg) => _pg = pg;

    public Task InitializeAsync() => _pg.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task TwoMembersTickTheSameKeyItem_BothWithNoMembershipId_AndATickTwiceStaysOne()
    {
        int linkshellId, aerisId, bobId;
        await using (var db = _pg.CreateContext())
        {
            var linkshell = new Linkshell { LinkshellName = "LS" };
            db.Linkshells.Add(linkshell);
            db.Users.AddRange(
                new AppUser { Id = "aeris", UserName = "aeris", CharacterName = "Aeris" },
                new AppUser { Id = "bob", UserName = "bob", CharacterName = "Bob" });
            var aeris = new AppUserLinkshell { Linkshell = linkshell, AppUserId = "aeris", CharacterName = "Aeris" };
            var bob = new AppUserLinkshell { Linkshell = linkshell, AppUserId = "bob", CharacterName = "Bob" };
            db.AppUserLinkshells.AddRange(aeris, bob);
            await db.SaveChangesAsync();
            linkshellId = linkshell.Id;
            aerisId = aeris.Id;
            bobId = bob.Id;
        }

        var officer = new ChartBoardActor("aeris", "Aeris");
        foreach (var membershipId in new[] { aerisId, bobId, bobId })
        {
            await using var db = _pg.CreateContext();
            Assert.Null(await new ChartKeyItemService(db).SetAsync(
                linkshellId, "Dynamis", Lantern, membershipId, has: true, officer, CancellationToken.None));
            await db.SaveChangesAsync();
        }

        await using (var check = _pg.CreateContext())
        {
            var ticks = await check.ChartMemberKeyItems.AsNoTracking().OrderBy(k => k.AppUserId).ToListAsync();
            Assert.Equal(new[] { "aeris", "bob" }, ticks.Select(k => k.AppUserId));
            Assert.All(ticks, k => Assert.Null(k.MembershipId));
        }

        // A second row for the same account is still refused by the per-account index.
        await using (var db = _pg.CreateContext())
        {
            db.ChartMemberKeyItems.Add(new ChartMemberKeyItem
            {
                LinkshellId = linkshellId, Board = "Dynamis", KeyItemName = Lantern, AppUserId = "bob",
            });
            await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        }
    }
}
