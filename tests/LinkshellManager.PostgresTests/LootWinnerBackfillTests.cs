using System.Linq;
using System.Threading.Tasks;
using LinkshellManagerDiscordApp.Migrations;
using LinkshellManagerDiscordApp.Models;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace LinkshellManager.PostgresTests;

// P2.10: the RecordLootWinnerAccount migration fills EventLootDetails.ItemWinnerAppUserId from the
// typed winner name, but only when exactly one member of that row's linkshell has the name.
[Collection("postgres")]
public class LootWinnerBackfillTests : IAsyncLifetime
{
    private readonly PostgresFixture _pg;

    public LootWinnerBackfillTests(PostgresFixture pg) => _pg = pg;

    public Task InitializeAsync() => _pg.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task BackfillSql_MatchesOnlyUnambiguousMembersOfTheRowsOwnLinkshell()
    {
        int main, alt, viaHistory, twoAccounts, unknown, otherShell, alreadySet;
        await using (var db = _pg.CreateContext())
        {
            var ls = new Linkshell { LinkshellName = "LS" };
            var other = new Linkshell { LinkshellName = "Other" };
            db.Linkshells.AddRange(ls, other);
            db.Users.Add(new AppUser { Id = "alice", UserName = "alice", CharacterName = "Alice", AltCharacterName1 = "Alicealt" });
            db.Users.Add(new AppUser { Id = "bob", UserName = "bob", CharacterName = "Bob", AltCharacterName1 = "Twin" });
            db.Users.Add(new AppUser { Id = "carol", UserName = "carol", CharacterName = "Carol", AltCharacterName1 = "Twin" });
            db.Users.Add(new AppUser { Id = "dave", UserName = "dave", CharacterName = "Dave" });
            await db.SaveChangesAsync();
            db.AppUserLinkshells.AddRange(
                new AppUserLinkshell { LinkshellId = ls.Id, AppUserId = "alice", CharacterName = "Alice" },
                new AppUserLinkshell { LinkshellId = ls.Id, AppUserId = "bob", CharacterName = "Bob" },
                new AppUserLinkshell { LinkshellId = ls.Id, AppUserId = "carol", CharacterName = "Carol" },
                // Dave is only in the other linkshell.
                new AppUserLinkshell { LinkshellId = other.Id, AppUserId = "dave", CharacterName = "Dave" });
            var history = new EventHistory { LinkshellId = ls.Id, EventName = "Sky" };
            db.EventHistories.Add(history);
            await db.SaveChangesAsync();

            EventLootDetail Loot(string winner, int? linkshellId, int? historyId = null, string? recorded = null)
            {
                var row = new EventLootDetail
                {
                    LinkshellId = linkshellId, EventHistoryId = historyId, ItemName = "Item",
                    ItemWinner = winner, ItemWinnerAppUserId = recorded,
                };
                db.EventLootDetails.Add(row);
                return row;
            }

            var r1 = Loot(" alice ", ls.Id);
            var r2 = Loot("ALICEALT", ls.Id);
            // No LinkshellId of its own: found through its past event.
            var r3 = Loot("Bob", null, history.Id);
            // Bob's and Carol's alts share a name: ambiguous, left alone.
            var r4 = Loot("Twin", ls.Id);
            var r5 = Loot("Nobody", ls.Id);
            // Dave exists, but not in this row's linkshell.
            var r6 = Loot("Dave", ls.Id);
            // Already recorded: not overwritten.
            var r7 = Loot("Alice", ls.Id, recorded: "carol");
            await db.SaveChangesAsync();
            (main, alt, viaHistory, twoAccounts, unknown, otherShell, alreadySet) =
                (r1.Id, r2.Id, r3.Id, r4.Id, r5.Id, r6.Id, r7.Id);
        }

        await using (var db = _pg.CreateContext())
        {
            await db.Database.ExecuteSqlRawAsync(RecordLootWinnerAccount.BackfillSql);

            var rows = await db.EventLootDetails.AsNoTracking().ToDictionaryAsync(r => r.Id, r => r.ItemWinnerAppUserId);
            Assert.Equal("alice", rows[main]);
            Assert.Equal("alice", rows[alt]);
            Assert.Equal("bob", rows[viaHistory]);
            Assert.Null(rows[twoAccounts]);
            Assert.Null(rows[unknown]);
            Assert.Null(rows[otherShell]);
            Assert.Equal("carol", rows[alreadySet]);
        }
    }
}
