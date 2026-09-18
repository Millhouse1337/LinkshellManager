using LinkshellManagerDiscordApp.Data;
using LinkshellManagerDiscordApp.Models;
using LinkshellManagerDiscordApp.Services;
using LinkshellManagerDiscordApp.ViewModels;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace LinkshellManager.Tests;

// "Follow relationships between tables" on the details page: a foreign key shows the principal
// row's label and links to its details, and the page lists every table whose rows point at this
// row with a count and a link to that table's list narrowed to this key. Links exist only for
// tables that are currently shown, and columns without a real foreign key (the soft int pointers
// the app keeps on purpose) are never links. Runs on the EF InMemory provider.
public class DataAdminRelationsTests
{
    private static readonly IReadOnlySet<string> RulesAndLinkshells = new HashSet<string> { "Rule", "Linkshell" };
    private static readonly IReadOnlySet<string> RulesOnly = new HashSet<string> { "Rule" };

    private static ApplicationDbContext NewInMemoryContext() =>
        new(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static async Task<ApplicationDbContext> SeededAsync()
    {
        var db = NewInMemoryContext();
        db.Linkshells.Add(new Linkshell { Id = 1, LinkshellName = "Kraken LS", LootStructure = "Dkp" });
        db.Linkshells.Add(new Linkshell { Id = 2, LinkshellName = "Titans", LootStructure = "Dkp" });
        db.Rules.Add(new Rule { Id = 1, LinkshellId = 1, LinkshellName = "Kraken LS", RuleTitle = "Loot rules", RuleDetails = "Line one\nLine two" });
        db.Rules.Add(new Rule { Id = 2, LinkshellId = 1, LinkshellName = "Kraken LS", RuleTitle = "Attendance", RuleDetails = "Be on time" });
        db.Rules.Add(new Rule { Id = 3, LinkshellId = 2, LinkshellName = "Titans", RuleTitle = "Bidding", RuleDetails = "..." });
        db.LinkshellRoles.Add(new LinkshellRole { Id = 1, LinkshellId = 1, Name = "Leader", IsSystem = true });
        db.Users.Add(new AppUser { Id = "u1", UserName = "millhouse", CharacterName = "Millhouse", PrimaryLinkshellId = 1, PasswordHash = "hash-that-must-never-show" });
        await db.SaveChangesAsync();
        return db;
    }

    private static DataAdminModel Table(ApplicationDbContext db, Type entityType) =>
        new DataAdminCatalog(db.Model, DataAdminPolicy.Default).FindByClrType(entityType)
        ?? throw new InvalidOperationException($"{entityType.Name} is not in the catalog.");

    private static async Task<DataAdminDetailsViewModel> DetailsAsync(ApplicationDbContext db, Type entityType, object key, IReadOnlySet<string> shown) =>
        await new DataAdminQueryService(db).DetailsAsync(Table(db, entityType), key, shown, CancellationToken.None)
        ?? throw new InvalidOperationException("row not found");

    [Fact]
    public async Task ReverseRelations_CountTheRowsThatPointHere_AndLinkOnlyShownTables()
    {
        using var db = await SeededAsync();

        var kraken = await DetailsAsync(db, typeof(Linkshell), 1, RulesAndLinkshells);

        var rules = Assert.Single(kraken.Related, r => r.Slug == "rule");
        Assert.Equal(2, rules.Count);
        Assert.Equal("LinkshellId", rules.ForeignKeyColumn);
        Assert.True(rules.IsShown);
        Assert.Equal("1", rules.FilterRoute["f.LinkshellId"]);
        Assert.Equal(DataAdminQueryService.DescribeDeleteBehavior(DeleteBehavior.Cascade), rules.OnDelete);

        var roles = Assert.Single(kraken.Related, r => r.Slug == "linkshell-role");
        Assert.Equal(1, roles.Count);
        Assert.False(roles.IsShown);

        Assert.DoesNotContain(kraken.Related, r => r.DisplayName.Contains("Identity", StringComparison.Ordinal));
        Assert.Equal("Kraken LS", kraken.Label);
        Assert.Equal("1", kraken.Key);
    }

    [Fact]
    public async Task ForwardRelation_ShowsThePrincipalLabel_AndLinksOnlyWhenShown()
    {
        using var db = await SeededAsync();

        var linked = await DetailsAsync(db, typeof(Rule), 1, RulesAndLinkshells);
        var field = Assert.Single(linked.Fields, f => f.Name == nameof(Rule.LinkshellId));
        Assert.Equal("Kraken LS", field.Text);
        Assert.Equal("Linkshell #1", field.Note);
        Assert.Equal("linkshell", field.LinkSlug);
        Assert.Equal("1", field.LinkKey);

        var unlinked = await DetailsAsync(db, typeof(Rule), 1, RulesOnly);
        var same = Assert.Single(unlinked.Fields, f => f.Name == nameof(Rule.LinkshellId));
        Assert.Equal("Kraken LS", same.Text);
        Assert.Null(same.LinkSlug);
    }

    [Fact]
    public async Task Details_ShowEveryVisibleColumn_AndNeverAProtectedOne()
    {
        using var db = await SeededAsync();

        var user = await DetailsAsync(db, typeof(AppUser), "u1", RulesAndLinkshells);

        Assert.Contains(user.Fields, f => f.Name == nameof(AppUser.CharacterName) && f.Text == "Millhouse");
        Assert.Contains(user.Fields, f => f.Name == nameof(AppUser.IsSuperAdmin) && f.Text == "No");
        Assert.DoesNotContain(user.Fields, f => f.Name == nameof(AppUser.PasswordHash));
        Assert.DoesNotContain(user.Fields, f => f.Name == nameof(AppUser.ProfileImage));
        Assert.Equal("Millhouse", user.Label);
    }

    // AppUser.PrimaryLinkshellId is a plain int with no foreign key, so it is never a link even
    // when the Linkshell table is shown.
    [Fact]
    public async Task SoftReferences_AreNeverLinks()
    {
        using var db = await SeededAsync();

        var user = await DetailsAsync(db, typeof(AppUser), "u1", RulesAndLinkshells);

        var pointer = Assert.Single(user.Fields, f => f.Name == nameof(AppUser.PrimaryLinkshellId));
        Assert.Equal("1", pointer.Text);
        Assert.Null(pointer.LinkSlug);
        Assert.Null(pointer.Note);
    }

    [Fact]
    public async Task LongText_IsFlaggedForPreWrap_AndNullsReadAsEmpty()
    {
        using var db = await SeededAsync();

        var rule = await DetailsAsync(db, typeof(Rule), 1, RulesOnly);

        Assert.True(Assert.Single(rule.Fields, f => f.Name == nameof(Rule.RuleDetails)).IsLongText);
        Assert.False(Assert.Single(rule.Fields, f => f.Name == nameof(Rule.RuleTitle)).IsLongText);
        var category = Assert.Single(rule.Fields, f => f.Name == nameof(Rule.Category));
        Assert.True(category.IsEmpty);
        Assert.Equal(DataAdminFormat.Empty, category.Text);
    }

    [Fact]
    public async Task MissingRow_IsNull()
    {
        using var db = await SeededAsync();

        Assert.Null(await new DataAdminQueryService(db).DetailsAsync(Table(db, typeof(Rule)), 999, RulesOnly, CancellationToken.None));
    }
}
