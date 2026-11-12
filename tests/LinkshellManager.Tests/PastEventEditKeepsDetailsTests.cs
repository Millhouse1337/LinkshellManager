using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LinkshellManagerDiscordApp.Data;
using LinkshellManagerDiscordApp.Models;
using LinkshellManagerDiscordApp.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LinkshellManager.Tests;

// The Past Events edit form has no Details box, so every Save sends details: null. That used to
// wipe the event's stored Details on any rename or retype. Null now means "not sent".
public class PastEventEditKeepsDetailsTests
{
    private const int LinkshellId = 42;
    private const int HistoryId = 7;

    private static ApplicationDbContext NewDb() =>
        new(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static EventHistoryEditService NewService(ApplicationDbContext db)
    {
        var pools = new DkpPoolResolver(db, new DkpPoolProvisioner(db));
        return new EventHistoryEditService(
            db, new DkpLedgerWriter(db, pools, NullLogger<DkpLedgerWriter>.Instance), pools);
    }

    private static async Task<ApplicationDbContext> SeededAsync()
    {
        var db = NewDb();
        db.Linkshells.Add(new Linkshell { Id = LinkshellId, LinkshellName = "Test", LootStructure = "Dkp" });
        db.EventHistories.Add(new EventHistory
        {
            Id = HistoryId,
            LinkshellId = LinkshellId,
            EventName = "Sky",
            EventType = "Sky",
            Details = "Kirin + 4 gods",
            Duration = 2,
            DkpPerHour = 10,
        });
        await db.SaveChangesAsync();
        return db;
    }

    private static Task<string?> StoredDetailsAsync(ApplicationDbContext db) =>
        db.EventHistories.AsNoTracking()
            .Where(h => h.Id == HistoryId)
            .Select(h => h.Details)
            .SingleAsync();

    [Fact]
    public async Task SaveWithoutDetails_KeepsTheStoredDetails()
    {
        await using var db = await SeededAsync();

        // What the form sends: a rename, the other fields as they were, details null.
        var outcome = await NewService(db).EditEventAsync(HistoryId,
            new EventHistoryEditInput("Sky (Kirin)", "Sky", null, null, 2, 10), CancellationToken.None);

        Assert.Equal(EventHistoryEditOutcome.Done, outcome);
        Assert.Equal("Kirin + 4 gods", await StoredDetailsAsync(db));
        Assert.Equal("Sky (Kirin)", (await db.EventHistories.AsNoTracking().SingleAsync()).EventName);
    }

    [Fact]
    public async Task SaveWithNewDetails_ReplacesThem()
    {
        await using var db = await SeededAsync();

        await NewService(db).EditEventAsync(HistoryId,
            new EventHistoryEditInput("Sky", "Sky", null, "  Kirin only  ", 2, 10), CancellationToken.None);

        Assert.Equal("Kirin only", await StoredDetailsAsync(db));
    }

    [Fact]
    public async Task SaveWithEmptyDetails_ClearsThem()
    {
        await using var db = await SeededAsync();

        await NewService(db).EditEventAsync(HistoryId,
            new EventHistoryEditInput("Sky", "Sky", null, "", 2, 10), CancellationToken.None);

        Assert.Null(await StoredDetailsAsync(db));
    }
}
