using LinkshellManagerDiscordApp.Data;
using LinkshellManagerDiscordApp.Models;
using LinkshellManagerDiscordApp.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LinkshellManager.Tests;

// The delete page's promise: before a row goes, the super admin sees every table that loses rows
// with it, every table whose rows are unlinked, and every table that makes the database refuse
// the delete. Two things are easy to get wrong and are pinned here: a table reachable through two
// cascade paths (AppUserEventStatusLedger under Event AND under AppUserEvent) must be counted
// once, and an optional foreign key with no ON DELETE clause (Event.SourceTodId -> Tod) is a
// block, not a set-null. Runs on the EF InMemory provider: it only counts, so no cascade fires.
public class DataAdminCascadePreviewTests
{
    private static readonly AppUser Actor = new() { Id = "admin-1", UserName = "millhouse", IsSuperAdmin = true };

    private static ApplicationDbContext NewInMemoryContext() =>
        new(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static async Task<ApplicationDbContext> SeededAsync()
    {
        var db = NewInMemoryContext();
        db.Linkshells.Add(new Linkshell { Id = 1, LinkshellName = "Kraken LS", LootStructure = "Dkp" });
        db.Rules.Add(new Rule { Id = 1, LinkshellId = 1, LinkshellName = "Kraken LS", RuleTitle = "Loot rules", RuleDetails = "..." });
        db.Tods.Add(new Tod { Id = 1, LinkshellId = 1 });
        db.Events.Add(new Event { Id = 1, LinkshellId = 1, EventName = "Fafnir camp", SourceTodId = 1 });
        db.Events.Add(new Event { Id = 2, LinkshellId = 1, EventName = "Sky run" });
        db.AppUserEvents.Add(new AppUserEvent { Id = 1, EventId = 1 });
        db.AppUserEvents.Add(new AppUserEvent { Id = 2, EventId = 1 });
        db.AppUserEventStatusLedgers.Add(new AppUserEventStatusLedger { Id = 1, AppUserEventId = 1, EventId = 1 });
        db.AppUserEventStatusLedgers.Add(new AppUserEventStatusLedger { Id = 2, AppUserEventId = 1, EventId = 1 });
        db.AppUserEventStatusLedgers.Add(new AppUserEventStatusLedger { Id = 3, AppUserEventId = 2, EventId = 1 });
        db.EventLootDetails.Add(new EventLootDetail { Id = 1, LinkshellId = 1, EventId = 1 });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return db;
    }

    private static DataAdminCatalog Catalog(ApplicationDbContext db) => new(db.Model, DataAdminPolicy.Default);

    private static DataAdminModel Table(DataAdminCatalog catalog, Type entityType) =>
        catalog.FindByClrType(entityType) ?? throw new InvalidOperationException($"{entityType.Name} is not in the catalog.");

    private static DataAdminEditor Editor(ApplicationDbContext db) =>
        new(db, new GlobalSettingsService(db, new MemoryCache(new MemoryCacheOptions())), NullLogger<DataAdminEditor>.Instance);

    private static async Task<DataAdminCascadeImpact> ImpactAsync(ApplicationDbContext db, Type entityType, object key)
    {
        var table = Table(Catalog(db), entityType);
        var entity = await table.FindAsync(db, key, track: false, CancellationToken.None) ?? throw new InvalidOperationException("row missing");
        return await DataAdminCascadePreview.ComputeAsync(db, table, entity, CancellationToken.None);
    }

    [Fact]
    public async Task DeletingAnEvent_CountsEachDependentRowOnce_AndReportsUnlinkedRows()
    {
        using var db = await SeededAsync();

        var impact = await ImpactAsync(db, typeof(Event), 1);

        Assert.False(impact.IsBlocked);
        Assert.Equal("Fafnir camp", impact.Label);
        var totals = impact.DeletedTotals.ToDictionary(pair => pair.Key.ClrType, pair => pair.Value);
        Assert.Equal(2, totals[typeof(AppUserEvent)]);
        Assert.Equal(3, totals[typeof(AppUserEventStatusLedger)]);
        Assert.Equal(5, impact.DeletedRows);

        // The ledger rows are reached under App User Event first (alphabetical walk); the direct
        // Event -> ledger edge then finds them already counted.
        var ledgerNodes = impact.Flatten().Where(node => node.Table.ClrType == typeof(AppUserEventStatusLedger)).ToList();
        Assert.Equal(2, ledgerNodes.Count);
        Assert.Equal(3, ledgerNodes.Sum(node => node.Count));
        Assert.Contains(ledgerNodes, node => node.Count == 0 && node.AlreadyCounted == 3);
        Assert.All(ledgerNodes, node => Assert.Equal(DataAdminCascadeImpact.Effect.Deleted, node.Effect));

        var loot = Assert.Single(impact.Flatten(), node => node.Table.ClrType == typeof(EventLootDetail));
        Assert.Equal(DataAdminCascadeImpact.Effect.Unlinked, loot.Effect);
        Assert.Equal(1, loot.Count);
        Assert.False(impact.IsTruncated);
    }

    [Fact]
    public async Task DeletingAnEventNobodyPointsAt_TouchesNothingElse()
    {
        using var db = await SeededAsync();

        var impact = await ImpactAsync(db, typeof(Event), 2);

        Assert.Empty(impact.Children);
        Assert.Equal(0, impact.DeletedRows);
        Assert.False(impact.IsBlocked);
    }

    // Event.SourceTodId is an optional foreign key with no ON DELETE clause, so Postgres refuses
    // to delete a Tod an event still points at; the preview must say so and the editor must not try.
    [Fact]
    public async Task DeletingATod_IsBlockedByTheEventThatPointsAtIt()
    {
        using var db = await SeededAsync();

        var impact = await ImpactAsync(db, typeof(Tod), 1);
        var block = Assert.Single(impact.Children);
        Assert.Equal(typeof(Event), block.Table.ClrType);
        Assert.Equal(nameof(Event.SourceTodId), block.ForeignKeyColumn);
        Assert.Equal(DataAdminCascadeImpact.Effect.Blocked, block.Effect);
        Assert.Equal(1, block.Count);
        Assert.True(impact.IsBlocked);

        var result = await Editor(db).DeleteAsync(Table(Catalog(db), typeof(Tod)), 1, Actor, CancellationToken.None);
        Assert.False(result.Succeeded);
        Assert.Contains("reference", Assert.Single(result.Errors).Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(await db.Tods.AnyAsync(t => t.Id == 1));
    }

    [Fact]
    public async Task DeleteAsync_RemovesTheRow_AndHonoursThePolicy()
    {
        using var db = await SeededAsync();
        var catalog = Catalog(db);
        var editor = Editor(db);

        var deleted = await editor.DeleteAsync(Table(catalog, typeof(Rule)), 1, Actor, CancellationToken.None);
        Assert.True(deleted.Succeeded, string.Join("; ", deleted.Errors.Select(e => e.Message)));
        Assert.False(await db.Rules.AnyAsync(r => r.Id == 1));

        Assert.True((await editor.DeleteAsync(Table(catalog, typeof(Linkshell)), 1, Actor, CancellationToken.None)).Forbidden);
        Assert.True((await editor.DeleteAsync(Table(catalog, typeof(Rule)), 999, Actor, CancellationToken.None)).NotFound);
        Assert.True(await db.Linkshells.AnyAsync(l => l.Id == 1));
    }

    // More dependents than one key chunk: the counts and the fetched keys are summed across chunks.
    [Fact]
    public async Task Counts_SpanKeyChunks()
    {
        using var db = NewInMemoryContext();
        db.Linkshells.Add(new Linkshell { Id = 1, LinkshellName = "Kraken LS", LootStructure = "Dkp" });
        for (var id = 1; id <= DataAdminDefaults.KeyChunkSize + 1; id++)
        {
            db.Rules.Add(new Rule { Id = id, LinkshellId = 1, RuleTitle = $"Rule {id}", RuleDetails = "..." });
        }
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var impact = await ImpactAsync(db, typeof(Linkshell), 1);

        var rules = Assert.Single(impact.Flatten(), node => node.Table.ClrType == typeof(Rule));
        Assert.Equal(DataAdminDefaults.KeyChunkSize + 1, rules.Count);
        Assert.Equal(DataAdminDefaults.KeyChunkSize + 1, impact.DeletedTotals.Single(pair => pair.Key.ClrType == typeof(Rule)).Value);
    }

    [Fact]
    public void Classify_UsesPostgresSemantics()
    {
        Assert.Equal(DataAdminCascadeImpact.Effect.Deleted, DataAdminCascadePreview.Classify(DeleteBehavior.Cascade));
        Assert.Equal(DataAdminCascadeImpact.Effect.Unlinked, DataAdminCascadePreview.Classify(DeleteBehavior.SetNull));
        Assert.Equal(DataAdminCascadeImpact.Effect.Blocked, DataAdminCascadePreview.Classify(DeleteBehavior.Restrict));
        Assert.Equal(DataAdminCascadeImpact.Effect.Blocked, DataAdminCascadePreview.Classify(DeleteBehavior.NoAction));
        Assert.Equal(DataAdminCascadeImpact.Effect.Blocked, DataAdminCascadePreview.Classify(DeleteBehavior.ClientSetNull));
        Assert.Equal(DataAdminCascadeImpact.Effect.Blocked, DataAdminCascadePreview.Classify(DeleteBehavior.ClientCascade));
    }
}
