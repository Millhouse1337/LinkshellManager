using LinkshellManagerDiscordApp.Data;
using LinkshellManagerDiscordApp.Models;
using LinkshellManagerDiscordApp.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Xunit;

namespace LinkshellManager.Tests;

// The runtime half of "choose the tables": which catalog tables are shown, stored as one
// AppSettings row per table. The invariants that matter are that it FAILS CLOSED (no row, no
// table), that a write is visible at once on the same instance despite the cache, and that the
// selection can never reach past the policy: unknown names and Hidden tables are dropped even
// when a row for them exists.
public class DataAdminSelectionServiceTests
{
    private static ApplicationDbContext NewInMemoryContext() =>
        new(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static DataAdminCatalog NewCatalog(ApplicationDbContext db, DataAdminPolicy? policy = null) =>
        new(db.Model, policy ?? DataAdminPolicy.Default);

    private static DataAdminModel Table(DataAdminCatalog catalog, Type entityType) =>
        catalog.FindByClrType(entityType)
        ?? throw new InvalidOperationException($"{entityType.Name} is not in the catalog.");

    [Fact]
    public async Task FreshDatabase_ShowsNothing()
    {
        using var db = NewInMemoryContext();
        var catalog = NewCatalog(db);
        var service = new DataAdminSelectionService(db, new MemoryCache(new MemoryCacheOptions()), catalog);

        Assert.Empty(await service.GetShownClrNamesAsync());
        Assert.Empty(await service.GetShownAsync());
        Assert.False(await service.IsShownAsync(Table(catalog, typeof(Rule))));
    }

    [Fact]
    public async Task SetShown_PersistsOneRowPerTable_AndIsVisibleAtOnce()
    {
        using var db = NewInMemoryContext();
        var catalog = NewCatalog(db);
        var service = new DataAdminSelectionService(db, new MemoryCache(new MemoryCacheOptions()), catalog);
        Assert.Empty(await service.GetShownClrNamesAsync()); // primes the cache

        await service.SetShownAsync(new[] { "Rule", "Linkshell" });

        var rows = await db.AppSettings.OrderBy(s => s.Key).ToListAsync();
        Assert.Equal(new[] { "dataadmin.show.Linkshell", "dataadmin.show.Rule" }, rows.Select(r => r.Key));
        Assert.All(rows, row => Assert.Equal("true", row.Value));
        Assert.True(await service.IsShownAsync(Table(catalog, typeof(Rule))));
        Assert.False(await service.IsShownAsync(Table(catalog, typeof(Tod))));
        Assert.Equal(new[] { typeof(Linkshell), typeof(Rule) }, (await service.GetShownAsync()).Select(m => m.ClrType));
    }

    [Fact]
    public async Task SetShown_WithFewerTables_RemovesTheOthersRows()
    {
        using var db = NewInMemoryContext();
        var catalog = NewCatalog(db);
        var service = new DataAdminSelectionService(db, new MemoryCache(new MemoryCacheOptions()), catalog);
        await service.SetShownAsync(new[] { "Rule", "Linkshell", "Tod" });

        await service.SetShownAsync(new[] { "Tod" });

        Assert.Equal(new[] { "dataadmin.show.Tod" }, (await db.AppSettings.Select(s => s.Key).ToListAsync()));
        await service.SetShownAsync(Array.Empty<string>());
        Assert.Empty(await db.AppSettings.ToListAsync());
        Assert.Empty(await service.GetShownClrNamesAsync());
    }

    [Fact]
    public async Task StaleFalseRow_ReadsAsNotShown_AndIsRepairedOnWrite()
    {
        using var db = NewInMemoryContext();
        db.AppSettings.Add(new AppSetting { Key = "dataadmin.show.Rule", Value = "false" });
        await db.SaveChangesAsync();
        var catalog = NewCatalog(db);
        var service = new DataAdminSelectionService(db, new MemoryCache(new MemoryCacheOptions()), catalog);

        Assert.False(await service.IsShownAsync(Table(catalog, typeof(Rule))));

        await service.SetShownAsync(new[] { "Rule" });
        Assert.Equal("true", (await db.AppSettings.SingleAsync()).Value);
        Assert.True(await service.IsShownAsync(Table(catalog, typeof(Rule))));
    }

    [Fact]
    public async Task UnknownNames_AreDropped()
    {
        using var db = NewInMemoryContext();
        var catalog = NewCatalog(db);
        var service = new DataAdminSelectionService(db, new MemoryCache(new MemoryCacheOptions()), catalog);

        await service.SetShownAsync(new[] { "Rule", "NoSuchTable", "IdentityUserClaim`1" });

        Assert.Equal(new[] { "dataadmin.show.Rule" }, await db.AppSettings.Select(s => s.Key).ToListAsync());
    }

    // The policy wins over the selection: a Hidden table is not shown even with a planted row,
    // and cannot be written.
    [Fact]
    public async Task HiddenTables_CannotBeShown_EvenWithAPlantedRow()
    {
        using var db = NewInMemoryContext();
        db.AppSettings.Add(new AppSetting { Key = "dataadmin.show.Rule", Value = "true" });
        await db.SaveChangesAsync();
        var policy = new DataAdminPolicy(new Dictionary<Type, DataAdminEntityPolicy>
        {
            [typeof(Rule)] = new() { Flags = DataAdminPolicyFlags.Hidden },
        });
        var catalog = NewCatalog(db, policy);
        var service = new DataAdminSelectionService(db, new MemoryCache(new MemoryCacheOptions()), catalog);

        Assert.False(await service.IsShownAsync(Table(catalog, typeof(Rule))));
        Assert.Empty(await service.GetShownClrNamesAsync());

        await service.SetShownAsync(new[] { "Rule", "Tod" });
        Assert.Equal(new[] { "dataadmin.show.Tod" }, await db.AppSettings.Select(s => s.Key).ToListAsync());
    }

    [Fact]
    public async Task Cache_IsSharedAcrossInstances_AndInvalidatedByAWrite()
    {
        using var db = NewInMemoryContext();
        var catalog = NewCatalog(db);
        var cache = new MemoryCache(new MemoryCacheOptions());
        var reader = new DataAdminSelectionService(db, cache, catalog);
        var writer = new DataAdminSelectionService(db, cache, catalog);
        Assert.Empty(await reader.GetShownClrNamesAsync());

        await writer.SetShownAsync(new[] { "Rule" });

        Assert.Equal(new[] { "Rule" }, await reader.GetShownClrNamesAsync());
    }
}
