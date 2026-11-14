using System.Threading.Tasks;
using LinkshellManagerDiscordApp.Migrations;
using LinkshellManagerDiscordApp.Models;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace LinkshellManager.PostgresTests;

// P9: loot added from a live event's card was saved without a LinkshellId. The
// BackfillLiveCardLootLinkshell migration fills it in from the row's event or past event.
[Collection("postgres")]
public class LootLinkshellBackfillTests : IAsyncLifetime
{
    private readonly PostgresFixture _pg;

    public LootLinkshellBackfillTests(PostgresFixture pg) => _pg = pg;

    public Task InitializeAsync() => _pg.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task BackfillSql_FillsOnlyMissingLinkshells_FromTheRowsEvent()
    {
        int linkshellId, otherLinkshellId, onLiveEvent, onPastEvent, noEvent, alreadySet;
        await using (var db = _pg.CreateContext())
        {
            var linkshell = new Linkshell { LinkshellName = "LS" };
            var other = new Linkshell { LinkshellName = "Other" };
            db.Linkshells.AddRange(linkshell, other);
            await db.SaveChangesAsync();
            linkshellId = linkshell.Id;
            otherLinkshellId = other.Id;

            var ev = new Event { LinkshellId = linkshell.Id, EventName = "Sky", EventType = "Sky" };
            var history = new EventHistory { LinkshellId = linkshell.Id, EventName = "Dynamis" };
            db.Events.Add(ev);
            db.EventHistories.Add(history);
            await db.SaveChangesAsync();

            var live = new EventLootDetail { EventId = ev.Id, ItemName = "Kirin's Osode" };
            var past = new EventLootDetail { EventHistoryId = history.Id, ItemName = "Aketon" };
            var none = new EventLootDetail { ItemName = "Orphan" };
            var set = new EventLootDetail { EventId = ev.Id, LinkshellId = other.Id, ItemName = "Left alone" };
            db.EventLootDetails.AddRange(live, past, none, set);
            await db.SaveChangesAsync();
            onLiveEvent = live.Id;
            onPastEvent = past.Id;
            noEvent = none.Id;
            alreadySet = set.Id;
        }

        await using (var db = _pg.CreateContext())
        {
            await db.Database.ExecuteSqlRawAsync(BackfillLiveCardLootLinkshell.BackfillSql);
            // Twice: the second run changes nothing.
            await db.Database.ExecuteSqlRawAsync(BackfillLiveCardLootLinkshell.BackfillSql);

            var rows = await db.EventLootDetails.AsNoTracking().ToDictionaryAsync(d => d.Id);
            Assert.Equal(linkshellId, rows[onLiveEvent].LinkshellId);
            Assert.Equal(linkshellId, rows[onPastEvent].LinkshellId);
            Assert.Null(rows[noEvent].LinkshellId);
            Assert.Equal(otherLinkshellId, rows[alreadySet].LinkshellId);
        }
    }
}
