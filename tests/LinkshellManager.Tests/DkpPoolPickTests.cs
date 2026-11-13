using LinkshellManagerDiscordApp.Controllers;
using LinkshellManagerDiscordApp.Data;
using LinkshellManagerDiscordApp.Models;
using LinkshellManagerDiscordApp.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LinkshellManager.Tests;

// The DKP pool picker on the Misc DKP, auction and Add Loot forms (contracts-03).
//
// A pool id from a client either belongs to the linkshell or is refused. It used to be swapped
// for the default pool without a word, and an auction edit that sent no pool moved the auction
// into the default pool. No pick now means "the fallback": the default pool, or on an auction
// edit the auction's own pool.
public class DkpPoolPickTests
{
    private const int Ls = 1;

    private static DkpPoolMap TwoPoolMap() => new(
        Ls,
        DefaultPoolId: 10,
        PoolIdByNormalizedEventType: new Dictionary<string, int>(),
        Pools: new[]
        {
            new DkpPool { Id = 10, LinkshellId = Ls, Name = "General", IsDefault = true, SortOrder = 0 },
            new DkpPool { Id = 11, LinkshellId = Ls, Name = "Sky", SortOrder = 1 },
        });

    [Fact]
    public void NoPick_UsesTheFallback()
    {
        // An auction edit passes the auction's CURRENT pool (11) as the fallback, so leaving the
        // pool out keeps it there instead of moving it to the default (10).
        Assert.True(TwoPoolMap().TryPick(null, 11, out var poolId));
        Assert.Equal(11, poolId);
    }

    [Fact]
    public void PickOfOwnPool_IsKept()
    {
        Assert.True(TwoPoolMap().TryPick(11, 10, out var poolId));
        Assert.Equal(11, poolId);
    }

    [Fact]
    public void PickOfAnotherLinkshellsPool_IsRefused()
    {
        Assert.False(TwoPoolMap().TryPick(99, 10, out _));
    }

    [Fact]
    public void PickerOptions_AreEmptyForASinglePool()
    {
        var single = new DkpPoolMap(Ls, 10, new Dictionary<string, int>(), new[]
        {
            new DkpPool { Id = 10, LinkshellId = Ls, Name = "General", IsDefault = true },
        });

        Assert.Empty(ActivityOverviewController.PrimaryDkpPoolOptions(single));
        Assert.Empty(ActivityOverviewController.PrimaryDkpPoolOptions(null));
    }

    [Fact]
    public void PickerOptions_ListEveryPoolAndMarkTheDefault()
    {
        var options = ActivityOverviewController.PrimaryDkpPoolOptions(TwoPoolMap());

        Assert.Equal(new[] { 10, 11 }, options.Select(option => option.Id));
        Assert.True(options[0].IsDefault);
        Assert.False(options[1].IsDefault);
        Assert.Equal("Sky", options[1].Name);
    }

    [Fact]
    public async Task ManualLoot_RefusesAPoolFromAnotherLinkshell()
    {
        using var db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);
        db.Linkshells.Add(new Linkshell { Id = Ls, LinkshellName = "LS", LootStructure = "Dkp" });
        db.Linkshells.Add(new Linkshell { Id = 2, LinkshellName = "Other", LootStructure = "Dkp" });
        db.DkpPools.Add(new DkpPool { Id = 50, LinkshellId = 2, Name = "Theirs", IsDefault = true });
        db.Users.Add(new AppUser { Id = "u1", UserName = "alice" });
        db.AppUserLinkshells.Add(new AppUserLinkshell
        {
            Id = 1, LinkshellId = Ls, AppUserId = "u1", CharacterName = "Alice", LinkshellDkp = 100,
        });
        await db.SaveChangesAsync();

        var pools = new DkpPoolResolver(db, new DkpPoolProvisioner(db));
        var service = new ManualLootService(
            db,
            new DkpLedgerWriter(db, pools, NullLogger<DkpLedgerWriter>.Instance),
            pools,
            new DkpPoolBalanceService(db, pools));

        var result = await service.AddAsync(
            Ls, ManualLootTarget.None, "Ridill", "Alice", 30, dkpPoolId: 50, recordedByAppUserId: null,
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(DkpPoolMap.UnknownPoolError, result.Error);
        Assert.Empty(await db.EventLootDetails.ToListAsync());
        Assert.Empty(await db.DkpLedgerEntries.ToListAsync());
    }
}
