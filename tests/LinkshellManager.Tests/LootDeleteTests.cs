using LinkshellManagerDiscordApp.Controllers;
using LinkshellManagerDiscordApp.Data;
using LinkshellManagerDiscordApp.Models;
using LinkshellManagerDiscordApp.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LinkshellManager.Tests;

// Deleting a recorded loot row (Loot tab → Edit → Delete record) removes it and gives the winner
// back the DKP it cost. Pinned here because the delete used to live only on the old website, with
// no test, and a refund that silently finds nobody leaves a member permanently short.
public class LootDeleteTests
{
    private const int Ls = 7;

    private static ApplicationDbContext NewDb() =>
        new(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options);

    private static LootEditService NewService(ApplicationDbContext db)
    {
        var pools = new DkpPoolResolver(db, new DkpPoolProvisioner(db));
        return new LootEditService(
            db,
            new DkpLedgerWriter(db, pools, NullLogger<DkpLedgerWriter>.Instance),
            pools,
            NullLogger<LootEditService>.Instance);
    }

    // A closed event whose 30-DKP item went to `winner`, already charged at close. `noEvent` files
    // it the way Add loot's "No event" option does instead: no event, just the row's own
    // linkshell, paid from `poolId` (null = the default pool).
    private static (AppUser Actor, EventLootDetail Loot) Seed(
        ApplicationDbContext db, string winner, bool noEvent = false, int? poolId = null)
    {
        db.Linkshells.Add(new Linkshell { Id = Ls, LinkshellName = "LS", LootStructure = "Dkp" });
        var actor = new AppUser
        {
            Id = "u1", UserName = "edicius", CharacterName = "Edicius", AltCharacterName1 = "Athmilk"
        };
        db.Users.Add(actor);
        db.AppUserLinkshells.Add(new AppUserLinkshell
        {
            Id = 1, LinkshellId = Ls, AppUserId = "u1", CharacterName = "Edicius", LinkshellDkp = 70
        });
        var history = new EventHistory
        {
            Id = 50, LinkshellId = Ls, EventName = "Dynamis - Xarcabard", EventType = "Dynamis"
        };
        db.EventHistories.Add(history);
        var loot = new EventLootDetail
        {
            Id = 60,
            EventHistoryId = noEvent ? null : history.Id,
            LinkshellId = Ls,
            DkpPoolId = poolId,
            ItemName = "Ducal Aketon",
            ItemWinner = winner,
            WinningDkpSpent = 30,
            ActualDeductedDkp = 30
        };
        db.EventLootDetails.Add(loot);
        db.SaveChanges();
        return (actor, loot);
    }

    [Fact]
    public async Task DeletingEventLoot_RemovesTheRow_AndRefundsTheWinner()
    {
        await using var db = NewDb();
        var (actor, loot) = Seed(db, winner: "Edicius");

        var result = await NewService(db).DeleteEventLootAsync(
            loot.Id, actor, "Wrong winner", DateTime.UtcNow);

        Assert.True(result.Success);
        Assert.Empty(db.EventLootDetails);
        var refund = Assert.Single(db.DkpLedgerEntries);
        Assert.Equal("LootDeleteRefund", refund.EntryType);
        Assert.Equal(30, refund.Amount);
        Assert.Equal("u1", refund.AppUserId);
        Assert.Equal(100, db.AppUserLinkshells.Single().LinkshellDkp);
    }

    [Fact]
    public async Task DeletingEventLoot_WonOnAnAlt_RefundsTheMainAccount()
    {
        await using var db = NewDb();
        var (actor, loot) = Seed(db, winner: "Athmilk");

        await NewService(db).DeleteEventLootAsync(loot.Id, actor, "Wrong winner", DateTime.UtcNow);

        var refund = Assert.Single(db.DkpLedgerEntries);
        Assert.Equal("u1", refund.AppUserId);
        Assert.Equal(30, refund.Amount);
    }

    // The bug a "No event" row hit: the Edit/Delete permission check looked for the linkshell
    // only through the row's event, found none, and answered "Loot record not found."
    [Fact]
    public async Task ANoEventRow_ResolvesToItsOwnLinkshell()
    {
        await using var db = NewDb();
        var (_, loot) = Seed(db, winner: "Edicius", noEvent: true);

        Assert.Equal(Ls, await ActivityLootController.LootRowLinkshellIdAsync(
            db, loot.Id, CancellationToken.None));
    }

    [Fact]
    public async Task DeletingNoEventLoot_RefundsIntoThePoolItWasPaidFrom()
    {
        await using var db = NewDb();
        db.DkpPools.Add(new DkpPool { Id = 1, LinkshellId = Ls, Name = "Main", IsDefault = true });
        db.DkpPools.Add(new DkpPool { Id = 2, LinkshellId = Ls, Name = "Sky" });
        var (actor, loot) = Seed(db, winner: "Edicius", noEvent: true, poolId: 2);

        var result = await NewService(db).DeleteEventLootAsync(loot.Id, actor, "Wrong item", DateTime.UtcNow);

        Assert.True(result.Success);
        Assert.Empty(db.EventLootDetails);
        var refund = Assert.Single(db.DkpLedgerEntries);
        Assert.Equal(30, refund.Amount);
        Assert.Equal(2, refund.DkpPoolId);
    }
}
