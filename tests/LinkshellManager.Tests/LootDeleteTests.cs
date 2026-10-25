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

    // A closed event whose 30-DKP item went to `winner`, already charged at close.
    private static (AppUser Actor, EventLootDetail Loot) Seed(ApplicationDbContext db, string winner)
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
            EventHistoryId = history.Id,
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
}
