using LinkshellManagerDiscordApp.Data;
using LinkshellManagerDiscordApp.Models;
using LinkshellManagerDiscordApp.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LinkshellManager.Tests;

// WHEN hand-entered loot is charged.
//
// Loot posted to a LIVE event (the addon's Post Loot, the live card's Add loot) waits for the event
// to end: recorded with no ledger row and DkpDebitedAt null, which is what the close -- or End Camp
// -- reads as "still owed". It used to be charged on the spot, so a camp's loot came off balances
// before the camp was over. Loot with no live event to ride has no close coming, so it is charged
// at once and stamped, which every close path skips.
public class ManualLootServiceTests
{
    private const int Ls = 1;
    private const int LiveEventId = 10;

    private static ApplicationDbContext NewDb() =>
        new(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static ManualLootService NewService(ApplicationDbContext db)
    {
        var pools = new DkpPoolResolver(db, new DkpPoolProvisioner(db));
        return new ManualLootService(
            db,
            new DkpLedgerWriter(db, pools, NullLogger<DkpLedgerWriter>.Instance),
            pools,
            new DkpPoolBalanceService(db, pools));
    }

    private static async Task<ApplicationDbContext> SeededAsync()
    {
        var db = NewDb();
        db.Linkshells.Add(new Linkshell { Id = Ls, LinkshellName = "LS", LootStructure = "Dkp" });
        db.Users.Add(new AppUser { Id = "u1", UserName = "alice" });
        db.AppUserLinkshells.Add(new AppUserLinkshell
        {
            Id = 1, LinkshellId = Ls, AppUserId = "u1", CharacterName = "Alice", LinkshellDkp = 100,
        });
        db.Events.Add(new Event { Id = LiveEventId, LinkshellId = Ls, EventName = "Fafnir", EventType = "HNM" });
        await db.SaveChangesAsync();
        return db;
    }

    [Fact]
    public async Task LootOnALiveEvent_IsRecordedButNotCharged()
    {
        using var db = await SeededAsync();

        var result = await NewService(db).AddAsync(
            Ls, ManualLootTarget.Parse("live", LiveEventId, null), "Ridill", "Alice", 30, null, CancellationToken.None);

        Assert.True(result.Success, result.Error);
        var loot = await db.EventLootDetails.SingleAsync();
        Assert.Equal(LiveEventId, loot.EventId);
        Assert.Null(loot.DkpDebitedAt);
        Assert.Null(loot.ActualDeductedDkp);
        Assert.Empty(await db.DkpLedgerEntries.ToListAsync());
    }

    [Fact]
    public async Task LootOnALiveEvent_StillHasToBeAffordable()
    {
        using var db = await SeededAsync();

        var result = await NewService(db).AddAsync(
            Ls, ManualLootTarget.Parse("live", LiveEventId, null), "Ridill", "Alice", 500, null, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Empty(await db.EventLootDetails.ToListAsync());
    }

    [Fact]
    public async Task LootWithNoEvent_IsChargedAtOnce()
    {
        using var db = await SeededAsync();

        var result = await NewService(db).AddAsync(
            Ls, ManualLootTarget.None, "Ridill", "Alice", 30, null, CancellationToken.None);

        Assert.True(result.Success, result.Error);
        var loot = await db.EventLootDetails.SingleAsync();
        Assert.NotNull(loot.DkpDebitedAt);
        Assert.Equal(30, loot.ActualDeductedDkp);
        Assert.Equal(-30, Assert.Single(await db.DkpLedgerEntries.ToListAsync()).Amount);
    }
}
