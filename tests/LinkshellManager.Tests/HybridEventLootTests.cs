using LinkshellManagerDiscordApp.Data;
using LinkshellManagerDiscordApp.Models;
using LinkshellManagerDiscordApp.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LinkshellManager.Tests;

// In a Hybrid linkshell an event item's "DKP" is a PERCENT of the winner's pool balance -- that is
// how the close, End Camp and hand-entered loot all charge it. Loot History's Edit/Delete and the
// live card's affordability check used to read that number as flat DKP: fixing just the item name
// on a 50% item that cost 100 refunded 100 and re-charged 50, and 50% was refused for a member
// with 40 DKP.
public class HybridEventLootTests
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

    // A closed event whose 50% item went to Edicius. He had 200, the close took 100, he has 100.
    // `actualDeducted` null is a legacy row that never stored what was taken.
    private static (AppUser Actor, EventLootDetail Loot) Seed(
        ApplicationDbContext db, string structure, double dkp, double? actualDeducted)
    {
        db.Linkshells.Add(new Linkshell { Id = Ls, LinkshellName = "LS", LootStructure = structure });
        var actor = new AppUser { Id = "u1", UserName = "edicius", CharacterName = "Edicius" };
        db.Users.Add(actor);
        db.AppUserLinkshells.Add(new AppUserLinkshell
        {
            Id = 1, LinkshellId = Ls, AppUserId = "u1", CharacterName = "Edicius", LinkshellDkp = dkp
        });
        db.EventHistories.Add(new EventHistory
        {
            Id = 50, LinkshellId = Ls, EventName = "Dynamis - Xarcabard", EventType = "Dynamis"
        });
        var loot = new EventLootDetail
        {
            Id = 60,
            EventHistoryId = 50,
            LinkshellId = Ls,
            ItemName = "Ducal Aketn",
            ItemWinner = "Edicius",
            WinningDkpSpent = 50,
            ActualDeductedDkp = actualDeducted,
            DkpDebitedAt = DateTime.UtcNow,
        };
        db.EventLootDetails.Add(loot);
        db.SaveChanges();
        return (actor, loot);
    }

    [Fact]
    public async Task FixingOnlyTheItemName_ChargesTheSamePercentAgain_NotTheFlatNumber()
    {
        await using var db = NewDb();
        var (actor, loot) = Seed(db, "Hybrid", dkp: 100, actualDeducted: 100);

        var result = await NewService(db).EditEventLootAsync(
            loot.Id, new LootEditRequest("Ducal Aketon", "Edicius", 50, "Typo"), actor, DateTime.UtcNow);

        Assert.True(result.Success);
        // Refund 100 back to 200, then 50% of 200 again: he is exactly where he was.
        Assert.Equal(100, db.AppUserLinkshells.Single().LinkshellDkp);
        Assert.Equal(100, db.EventLootDetails.Single().ActualDeductedDkp);
    }

    [Fact]
    public async Task DeletingALegacyHybridRow_RefundsWhatThePercentTook()
    {
        await using var db = NewDb();
        var (actor, loot) = Seed(db, "Hybrid", dkp: 100, actualDeducted: null);

        var result = await NewService(db).DeleteEventLootAsync(loot.Id, actor, "Wrong winner", DateTime.UtcNow);

        Assert.True(result.Success);
        // 50% left him with 100, so 100 was taken (not 50).
        Assert.Equal(100, Assert.Single(db.DkpLedgerEntries).Amount);
        Assert.Equal(200, db.AppUserLinkshells.Single().LinkshellDkp);
    }

    [Theory]
    [InlineData("Hybrid", true)]  // 50% of 40 = 20, affordable
    [InlineData("Dkp", false)]    // 50 flat DKP, he only has 40
    public async Task LiveCardGuard_ReadsAHybridNumberAsAPercent(string structure, bool allowed)
    {
        await using var db = NewDb();
        db.Linkshells.Add(new Linkshell { Id = Ls, LinkshellName = "LS", LootStructure = structure });
        db.Users.Add(new AppUser { Id = "u1", UserName = "edicius", CharacterName = "Edicius" });
        db.AppUserLinkshells.Add(new AppUserLinkshell
        {
            Id = 1, LinkshellId = Ls, AppUserId = "u1", CharacterName = "Edicius", LinkshellDkp = 40
        });
        db.Events.Add(new Event { Id = 9, LinkshellId = Ls, EventName = "Sky", EventType = "Sky" });
        db.SaveChanges();
        var pools = new DkpPoolResolver(db, new DkpPoolProvisioner(db));

        var error = await LootDkpGuard.CheckEventLootAsync(
            db, pools, new DkpPoolBalanceService(db, pools), 9, Ls, "Edicius", 50, CancellationToken.None);

        Assert.Equal(allowed, error is null);
    }
}
