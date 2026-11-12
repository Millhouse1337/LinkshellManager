using LinkshellManagerDiscordApp.Data;
using LinkshellManagerDiscordApp.Models;
using LinkshellManagerDiscordApp.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LinkshellManager.Tests;

// Deleting a past event reverses every DKP row filed under it. Loot History's refunds and edit
// charges used to be filed under no event, so the event delete reversed the close's loot charge
// but left the refund standing: the winner got the item's DKP back twice.
public class LootEditThenEventDeleteTests
{
    private const int Ls = 7;
    private const int HistoryId = 50;

    private static ApplicationDbContext NewDb() =>
        new(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options);

    private static (LootEditService Loot, EventHistoryEditService History) NewServices(ApplicationDbContext db)
    {
        var pools = new DkpPoolResolver(db, new DkpPoolProvisioner(db));
        var writer = new DkpLedgerWriter(db, pools, NullLogger<DkpLedgerWriter>.Instance);
        return (
            new LootEditService(db, writer, pools, NullLogger<LootEditService>.Instance),
            new EventHistoryEditService(db, writer, pools));
    }

    // Edicius had 100 and Bob 50. The event's close charged Edicius 30 for the Aketon.
    private static async Task<(AppUser Actor, EventLootDetail Loot)> SeedAsync(ApplicationDbContext db)
    {
        db.Linkshells.Add(new Linkshell { Id = Ls, LinkshellName = "LS", LootStructure = "Dkp" });
        var actor = new AppUser { Id = "u1", UserName = "edicius", CharacterName = "Edicius" };
        db.Users.Add(actor);
        db.Users.Add(new AppUser { Id = "u2", UserName = "bob", CharacterName = "Bob" });
        var edicius = new AppUserLinkshell
        {
            Id = 1, LinkshellId = Ls, AppUserId = "u1", CharacterName = "Edicius", LinkshellDkp = 100
        };
        db.AppUserLinkshells.Add(edicius);
        db.AppUserLinkshells.Add(new AppUserLinkshell
        {
            Id = 2, LinkshellId = Ls, AppUserId = "u2", CharacterName = "Bob", LinkshellDkp = 50
        });
        db.EventHistories.Add(new EventHistory
        {
            Id = HistoryId, LinkshellId = Ls, EventName = "Dynamis - Xarcabard", EventType = "Dynamis"
        });
        var loot = new EventLootDetail
        {
            Id = 60,
            EventHistoryId = HistoryId,
            LinkshellId = Ls,
            ItemName = "Ducal Aketon",
            ItemWinner = "Edicius",
            WinningDkpSpent = 30,
            ActualDeductedDkp = 30,
            DkpDebitedAt = DateTime.UtcNow,
        };
        db.EventLootDetails.Add(loot);
        await db.SaveChangesAsync();

        var pools = new DkpPoolResolver(db, new DkpPoolProvisioner(db));
        var writer = new DkpLedgerWriter(db, pools, NullLogger<DkpLedgerWriter>.Instance);
        await writer.AppendAsync(edicius, "LootSpent", -30, DateTime.UtcNow, DkpPoolRef.Derived("Dynamis"),
            new DkpEntryContext(CharacterName: "Edicius", EventHistoryId: HistoryId, SourceEventLootDetailId: 60),
            CancellationToken.None);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return (actor, loot);
    }

    private static double Dkp(ApplicationDbContext db, string appUserId) =>
        db.AppUserLinkshells.AsNoTracking().Single(m => m.AppUserId == appUserId).LinkshellDkp ?? 0;

    [Fact]
    public async Task DeleteLoot_ThenDeleteTheEvent_LeavesTheWinnerWhereHeStarted()
    {
        await using var db = NewDb();
        var (actor, loot) = await SeedAsync(db);
        var (lootService, history) = NewServices(db);

        Assert.True((await lootService.DeleteEventLootAsync(loot.Id, actor, "Wrong winner", DateTime.UtcNow)).Success);
        Assert.Equal(HistoryId, db.DkpLedgerEntries.Single(e => e.EntryType == "LootDeleteRefund").EventHistoryId);
        db.ChangeTracker.Clear();

        Assert.True(await history.DeleteEventAsync(HistoryId, CancellationToken.None));

        Assert.Equal(100, Dkp(db, "u1"));
    }

    [Fact]
    public async Task ChangeTheWinner_ThenDeleteTheEvent_PutsBothMembersBack()
    {
        await using var db = NewDb();
        var (actor, loot) = await SeedAsync(db);
        var (lootService, history) = NewServices(db);

        var edit = await lootService.EditEventLootAsync(
            loot.Id, new LootEditRequest("Ducal Aketon", "Bob", 30, "Bob won it"), actor, DateTime.UtcNow);
        Assert.True(edit.Success);
        db.ChangeTracker.Clear();
        Assert.Equal(100, Dkp(db, "u1"));
        Assert.Equal(20, Dkp(db, "u2"));

        Assert.True(await history.DeleteEventAsync(HistoryId, CancellationToken.None));

        Assert.Equal(100, Dkp(db, "u1"));
        Assert.Equal(50, Dkp(db, "u2"));
    }
}
