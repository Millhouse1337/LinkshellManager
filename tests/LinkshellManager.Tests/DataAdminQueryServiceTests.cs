using LinkshellManagerDiscordApp.Data;
using LinkshellManagerDiscordApp.Models;
using LinkshellManagerDiscordApp.Services;
using LinkshellManagerDiscordApp.ViewModels;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Primitives;
using Xunit;

namespace LinkshellManager.Tests;

// The Data Admin list page: search, equality filters, sort, paging and the foreign-key label
// batch, all driven by hand-built expression trees over whatever table is asked for. These run on
// the EF InMemory provider, so the search exercises the ToLower().Contains branch; the production
// branch is EF.Functions.ILike (Npgsql only) and is checked by hand against Postgres, which is
// why EscapeLike has its own test on the pattern string.
public class DataAdminQueryServiceTests
{
    private static readonly IReadOnlySet<string> ShowRulesAndLinkshells = new HashSet<string> { "Rule", "Linkshell" };
    private static readonly IReadOnlySet<string> ShowRulesOnly = new HashSet<string> { "Rule" };

    private static ApplicationDbContext NewInMemoryContext() =>
        new(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static Rule NewRule(int id, int linkshellId, string title, string details, string? category) => new()
    {
        Id = id,
        LinkshellId = linkshellId,
        LinkshellName = linkshellId == 1 ? "Kraken LS" : "Titans",
        RuleTitle = title,
        RuleDetails = details,
        Category = category,
        CreatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddDays(id),
    };

    private static async Task<ApplicationDbContext> SeededAsync()
    {
        var db = NewInMemoryContext();
        db.Linkshells.Add(new Linkshell { Id = 1, LinkshellName = "Kraken LS", LootStructure = "Dkp" });
        db.Linkshells.Add(new Linkshell { Id = 2, LinkshellName = "Titans", LootStructure = "Dkp" });
        db.Rules.AddRange(
            NewRule(1, 1, "Loot rules", "No ninja looting", "Loot"),
            NewRule(2, 1, "attendance", "Be on time", "Conduct"),
            NewRule(3, 2, "Loot split", "Split evenly", "Loot"),
            NewRule(4, 1, "Discord etiquette", "Be nice", "Conduct"),
            NewRule(5, 2, "Bidding", "loot bids close at 21:00", null));
        await db.SaveChangesAsync();
        return db;
    }

    private static DataAdminModel Rules(ApplicationDbContext db) =>
        new DataAdminCatalog(db.Model, DataAdminPolicy.Default).FindByClrType(typeof(Rule))
        ?? throw new InvalidOperationException("Rule is not in the catalog.");

    private static DataAdminListQuery Query(params (string Key, string Value)[] pairs) =>
        DataAdminListQuery.Parse(new QueryCollection(pairs.ToDictionary(p => p.Key, p => new StringValues(p.Value))));

    private static async Task<int[]> IdsAsync(ApplicationDbContext db, DataAdminListQuery query)
    {
        var page = await new DataAdminQueryService(db).ListAsync(Rules(db), query, ShowRulesOnly, CancellationToken.None);
        return page.Rows.Select(row => int.Parse(row.Key, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
    }

    [Fact]
    public async Task NoQuery_ListsEveryRow_NewestFirst()
    {
        using var db = await SeededAsync();

        var model = await new DataAdminQueryService(db).ListAsync(Rules(db), Query(), ShowRulesOnly, CancellationToken.None);

        Assert.Equal(new[] { 5, 4, 3, 2, 1 }, await IdsAsync(db, Query()));
        Assert.Equal(5, model.Total);
        Assert.Equal(1, model.TotalPages);
        Assert.Equal(nameof(Rule.Id), model.SortColumn);
        Assert.True(model.Descending);
        Assert.Contains(model.Columns, c => c.Name == nameof(Rule.RuleTitle));
        Assert.Contains(model.Columns, c => c.IsKey);
        Assert.Equal("Loot rules", model.Rows.Single(r => r.Key == "1").Label);
    }

    [Fact]
    public async Task Search_IsCaseInsensitive_AcrossEveryTextColumn()
    {
        using var db = await SeededAsync();

        // "Loot rules" (title), "Loot split" (title), "loot bids..." (details)
        Assert.Equal(new[] { 1, 3, 5 }, (await IdsAsync(db, Query(("q", "LOOT")))).OrderBy(id => id));
        Assert.Equal(new[] { 2, 4 }, (await IdsAsync(db, Query(("q", "conduct")))).OrderBy(id => id)); // category column
        Assert.Empty(await IdsAsync(db, Query(("q", "zzz"))));
        Assert.Equal(5, (await IdsAsync(db, Query(("q", "   ")))).Length); // blank search is no search
    }

    [Fact]
    public void EscapeLike_EscapesTheLikeMetacharacters()
    {
        Assert.Equal("50\\%\\_a\\\\b", DataAdminValues.EscapeLike("50%_a\\b"));
        Assert.Equal("plain", DataAdminValues.EscapeLike("plain"));
    }

    [Fact]
    public async Task Filter_OnAForeignKey_NarrowsRows_AndBadFiltersAreIgnored()
    {
        using var db = await SeededAsync();

        Assert.Equal(new[] { 1, 2, 4 }, (await IdsAsync(db, Query(("f.LinkshellId", "1")))).OrderBy(id => id));
        Assert.Equal(new[] { 3, 5 }, (await IdsAsync(db, Query(("f.LinkshellId", "2")))).OrderBy(id => id));
        Assert.Equal(5, (await IdsAsync(db, Query(("f.NoSuchColumn", "1")))).Length);
        Assert.Equal(5, (await IdsAsync(db, Query(("f.LinkshellId", "abc")))).Length);
        // Only key and foreign-key columns are filterable; a text column is not.
        Assert.Equal(5, (await IdsAsync(db, Query(("f.Category", "Loot")))).Length);
    }

    [Fact]
    public async Task Sort_ByAnyVisibleColumn_BreaksTiesByKey()
    {
        using var db = await SeededAsync();

        var byTitle = await IdsAsync(db, Query(("sort", "RuleTitle")));
        Assert.Equal(new[] { 2, 5, 4, 1, 3 }, byTitle); // attendance, Bidding, Discord..., Loot rules, Loot split (ordinal)
        var byTitleDesc = await IdsAsync(db, Query(("sort", "RuleTitle"), ("desc", "true")));
        Assert.Equal(byTitle.Reverse(), byTitleDesc);

        // Category ties (Loot: 1 and 3; Conduct: 2 and 4) fall back to the key, ascending with the sort.
        var byCategory = await IdsAsync(db, Query(("sort", "Category")));
        Assert.True(Array.IndexOf(byCategory, 1) < Array.IndexOf(byCategory, 3));
        Assert.True(Array.IndexOf(byCategory, 2) < Array.IndexOf(byCategory, 4));
        var byCategoryDesc = await IdsAsync(db, Query(("sort", "Category"), ("desc", "true")));
        Assert.True(Array.IndexOf(byCategoryDesc, 3) < Array.IndexOf(byCategoryDesc, 1));

        // An unknown sort column falls back to the default order.
        Assert.Equal(new[] { 5, 4, 3, 2, 1 }, await IdsAsync(db, Query(("sort", "Nope"))));
    }

    [Fact]
    public async Task Paging_ClampsThePage_AndCountsTheWholeSet()
    {
        using var db = NewInMemoryContext();
        db.Linkshells.Add(new Linkshell { Id = 1, LinkshellName = "Kraken LS", LootStructure = "Dkp" });
        for (var id = 1; id <= 30; id++)
        {
            db.Rules.Add(NewRule(id, 1, $"Rule {id:00}", "...", null));
        }
        await db.SaveChangesAsync();
        var service = new DataAdminQueryService(db);
        var rules = Rules(db);

        var first = await service.ListAsync(rules, Query(), ShowRulesOnly, CancellationToken.None);
        Assert.Equal(30, first.Total);
        Assert.Equal(2, first.TotalPages);
        Assert.Equal(DataAdminDefaults.PageSize, first.Rows.Count);
        Assert.Equal(1, first.FirstRowNumber);
        Assert.Equal(DataAdminDefaults.PageSize, first.LastRowNumber);

        var second = await service.ListAsync(rules, Query(("page", "2")), ShowRulesOnly, CancellationToken.None);
        Assert.Equal(30 - DataAdminDefaults.PageSize, second.Rows.Count);
        Assert.Equal(2, second.Page);
        Assert.Equal(DataAdminDefaults.PageSize + 1, second.FirstRowNumber);
        Assert.Equal(30, second.LastRowNumber);

        Assert.Equal(2, (await service.ListAsync(rules, Query(("page", "99")), ShowRulesOnly, CancellationToken.None)).Page);
        Assert.Equal(1, (await service.ListAsync(rules, Query(("page", "0")), ShowRulesOnly, CancellationToken.None)).Page);
        Assert.Equal("2", second.PageRoute(2)[DataAdminListQuery.PageKey]);
        Assert.False(second.PageRoute(1).ContainsKey(DataAdminListQuery.PageKey));
    }

    [Fact]
    public async Task ForeignKeyCells_ShowThePrincipalLabel_AndLinkOnlyWhenThePrincipalIsShown()
    {
        using var db = await SeededAsync();
        db.Rules.Add(NewRule(9, 999, "Orphan", "linkshell gone", null));
        await db.SaveChangesAsync();
        var service = new DataAdminQueryService(db);
        var rules = Rules(db);
        var linkshellColumn = rules.ListColumns.ToList().FindIndex(c => c.Name == nameof(Rule.LinkshellId));
        Assert.True(linkshellColumn >= 0);

        var linked = await service.ListAsync(rules, Query(), ShowRulesAndLinkshells, CancellationToken.None);
        var cell = linked.Rows.Single(r => r.Key == "1").Cells[linkshellColumn];
        Assert.Equal("Kraken LS", cell.Text);
        Assert.Equal("linkshell", cell.LinkSlug);
        Assert.Equal("1", cell.LinkRoute?["f.Id"]);
        var orphan = linked.Rows.Single(r => r.Key == "9").Cells[linkshellColumn];
        Assert.Equal("999", orphan.Text);

        var unlinked = await service.ListAsync(rules, Query(), ShowRulesOnly, CancellationToken.None);
        var same = unlinked.Rows.Single(r => r.Key == "1").Cells[linkshellColumn];
        Assert.Equal("Kraken LS", same.Text);
        Assert.Null(same.LinkSlug);
    }

    [Fact]
    public async Task ActiveFilters_UseThePrincipalLabel()
    {
        using var db = await SeededAsync();
        var service = new DataAdminQueryService(db);

        var titans = await service.ListAsync(Rules(db), Query(("f.LinkshellId", "2")), ShowRulesOnly, CancellationToken.None);
        var chip = Assert.Single(titans.ActiveFilters);
        Assert.Equal("LinkshellId", chip.Column);
        Assert.Equal("Linkshell", chip.DisplayName);
        Assert.Equal("Titans", chip.Label);
        Assert.False(titans.RemoveFilterRoute("LinkshellId").ContainsKey("f.LinkshellId"));

        var gone = await service.ListAsync(Rules(db), Query(("f.LinkshellId", "999")), ShowRulesOnly, CancellationToken.None);
        Assert.Equal("999", Assert.Single(gone.ActiveFilters).Label);
        Assert.Empty(gone.Rows);
    }

    [Fact]
    public void Parse_RoundTrips_ThroughRouteValues()
    {
        var query = Query(("q", " loot "), ("sort", "RuleTitle"), ("desc", "true"), ("page", "3"), ("f.LinkshellId", "2"), ("f.", "ignored"), ("f.Empty", "   "));

        Assert.Equal("loot", query.Search);
        Assert.Equal("RuleTitle", query.Sort);
        Assert.True(query.Descending);
        Assert.Equal(3, query.Page);
        Assert.Equal(new Dictionary<string, string> { ["LinkshellId"] = "2" }, query.Filters);
        Assert.Equal(
            new Dictionary<string, string> { ["q"] = "loot", ["sort"] = "RuleTitle", ["desc"] = "true", ["page"] = "3", ["f.LinkshellId"] = "2" },
            query.ToRouteValues());

        Assert.Equal(1, Query(("page", "-4")).Page);
        Assert.Equal(1, Query(("page", "x")).Page);
        Assert.Empty(Query().ToRouteValues());
    }

    [Fact]
    public void SortLinks_FlipDirection_AndReturnToPageOne()
    {
        var query = Query(("sort", "RuleTitle"), ("page", "3"), ("f.LinkshellId", "2"));

        var flipped = query.WithSort("RuleTitle");
        Assert.True(flipped.Descending);
        Assert.Equal(1, flipped.Page);
        Assert.Equal("2", flipped.Filters["LinkshellId"]);

        var other = query.WithSort("Category");
        Assert.Equal("Category", other.Sort);
        Assert.False(other.Descending);

        Assert.Empty(query.WithoutFilter("LinkshellId").Filters);
        Assert.Equal(7, query.WithPage(7).Page);
    }
}
