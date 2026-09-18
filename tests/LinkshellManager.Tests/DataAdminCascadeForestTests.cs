using LinkshellManagerDiscordApp.Data;
using LinkshellManagerDiscordApp.Models;
using LinkshellManagerDiscordApp.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace LinkshellManager.Tests;

// The "Choose tables" page groups tables by cascade delete so a super admin can see that ticking
// Event also concerns the rows an Event delete takes with it. The grouping is a forest computed
// from the real Cascade foreign keys; these tests pin its shape over the real model (so a new
// foreign key that re-parents a table is noticed) and the rules that decide a parent when a table
// has several cascade owners, plus the cycle guard the real model never exercises.
public class DataAdminCascadeForestTests
{
    private static ApplicationDbContext NewInMemoryContext() =>
        new(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static DataAdminCatalog NewCatalog()
    {
        using var db = NewInMemoryContext();
        return new DataAdminCatalog(db.Model, DataAdminPolicy.Default);
    }

    private static DataAdminCascadeForest<DataAdminModel>.Node NodeOf(DataAdminCatalog catalog, Type entityType)
    {
        var table = catalog.FindByClrType(entityType) ?? throw new InvalidOperationException($"{entityType.Name} is not in the catalog.");
        return catalog.Forest.ByItem[table];
    }

    private static Type? ParentOf(DataAdminCatalog catalog, Type entityType) => NodeOf(catalog, entityType).Parent?.Item.ClrType;

    private static IEnumerable<Type> AlsoOf(DataAdminCatalog catalog, Type entityType) => NodeOf(catalog, entityType).AlsoDeletedWith.Select(m => m.ClrType);

    [Fact]
    public void RealModel_Roots()
    {
        var catalog = NewCatalog();

        Assert.Null(ParentOf(catalog, typeof(Linkshell)));
        Assert.Null(ParentOf(catalog, typeof(AppUser)));
        Assert.Null(ParentOf(catalog, typeof(AppSetting)));
        Assert.Null(ParentOf(catalog, typeof(DiscordActivityUser)));
        Assert.Contains(catalog.Forest.Roots, node => node.Item.ClrType == typeof(Linkshell));
    }

    [Fact]
    public void RealModel_SingleOwnerChains()
    {
        var catalog = NewCatalog();

        Assert.Equal(typeof(Linkshell), ParentOf(catalog, typeof(Event)));
        Assert.Equal(typeof(Event), ParentOf(catalog, typeof(AppUserEvent)));
        Assert.Equal(typeof(Linkshell), ParentOf(catalog, typeof(Rule)));
        // EventComment hangs off the past event, not the live one: it has no FK to Event at all.
        Assert.Equal(typeof(EventHistory), ParentOf(catalog, typeof(EventComment)));
    }

    // Deepest owner wins; Linkshell only wins a tie between roots; names break the rest.
    [Fact]
    public void RealModel_MultiOwnerTables_NestUnderTheMostSpecificOwner()
    {
        var catalog = NewCatalog();

        Assert.Equal(typeof(DkpPool), ParentOf(catalog, typeof(DkpPoolEventType)));
        Assert.Equal(new[] { typeof(Linkshell) }, AlsoOf(catalog, typeof(DkpPoolEventType)));

        Assert.Equal(typeof(AppUserEvent), ParentOf(catalog, typeof(AppUserEventStatusLedger)));
        Assert.Contains(typeof(Event), AlsoOf(catalog, typeof(AppUserEventStatusLedger)));

        Assert.Equal(typeof(Linkshell), ParentOf(catalog, typeof(Invite)));
        Assert.Equal(new[] { typeof(AppUser) }, AlsoOf(catalog, typeof(Invite)));

        Assert.Equal(typeof(Event), ParentOf(catalog, typeof(PartySetup)));
        Assert.Equal(new[] { typeof(Linkshell) }, AlsoOf(catalog, typeof(PartySetup)));

        Assert.Equal(typeof(PartySetupSlot), ParentOf(catalog, typeof(EventPartySlotSignup)));
        Assert.Equal(typeof(Auction), ParentOf(catalog, typeof(AuctionItem)));
        Assert.Equal(new[] { typeof(AuctionHistory) }, AlsoOf(catalog, typeof(AuctionItem)));
        Assert.Equal(typeof(Event), ParentOf(catalog, typeof(EventAttendanceWindow)));
        Assert.Equal(new[] { typeof(EventHistory) }, AlsoOf(catalog, typeof(EventAttendanceWindow)));
    }

    [Fact]
    public void RealModel_LinkshellIsTheTenantWipe()
    {
        var catalog = NewCatalog();
        var descendants = NodeOf(catalog, typeof(Linkshell)).CascadeDescendants.Select(m => m.ClrType).ToList();

        // 61 of the 67 tables at the time of writing; do not pin the exact number.
        Assert.True(descendants.Count > 50, $"Linkshell reaches only {descendants.Count} tables.");
        Assert.Contains(typeof(Rule), descendants);
        Assert.Contains(typeof(Event), descendants);
        Assert.Contains(typeof(AppUserEvent), descendants);
        Assert.Contains(typeof(JournalEntryLine), descendants);
        Assert.DoesNotContain(typeof(AppUser), descendants);
        Assert.DoesNotContain(typeof(AppSetting), descendants);
    }

    [Fact]
    public void RealModel_AppUserReachesInviteOnly_AndNeverIdentityTables()
    {
        var catalog = NewCatalog();
        var node = NodeOf(catalog, typeof(AppUser));

        Assert.Equal(new[] { typeof(Invite) }, node.CascadeDescendants.Select(m => m.ClrType));
        Assert.Empty(node.Children); // Invite files under Linkshell by the tie rule
    }

    [Fact]
    public void RealModel_EveryTableAppearsExactlyOnce()
    {
        var catalog = NewCatalog();
        var seen = new List<DataAdminModel>();
        void Walk(DataAdminCascadeForest<DataAdminModel>.Node node, int depth)
        {
            Assert.Equal(depth, node.Depth);
            seen.Add(node.Item);
            foreach (var child in node.Children)
            {
                Assert.Same(node, child.Parent);
                Walk(child, depth + 1);
            }
        }
        foreach (var root in catalog.Forest.Roots)
        {
            Walk(root, 0);
        }

        Assert.Equal(catalog.Models.Count, seen.Count);
        Assert.Equal(seen.Count, seen.Distinct().Count());
    }

    [Fact]
    public void Synthetic_DeepestOwnerWins_ThenTieGoesToThePreferredItem_ThenName()
    {
        var forest = DataAdminCascadeForest<string>.Build(
            new[] { "Linkshell", "Pool", "Type", "Invite", "User", "Item", "Auction", "Archive" },
            new (string, string)[]
            {
                ("Pool", "Linkshell"), ("Type", "Linkshell"), ("Type", "Pool"),
                ("Invite", "Linkshell"), ("Invite", "User"),
                ("Auction", "Linkshell"), ("Archive", "Linkshell"), ("Item", "Auction"), ("Item", "Archive"),
            },
            name => name,
            name => name == "Linkshell");

        Assert.Equal("Pool", forest.ByItem["Type"].Parent?.Item);
        Assert.Equal(new[] { "Linkshell" }, forest.ByItem["Type"].AlsoDeletedWith);
        Assert.Equal("Linkshell", forest.ByItem["Invite"].Parent?.Item);
        Assert.Equal(new[] { "User" }, forest.ByItem["Invite"].AlsoDeletedWith);
        Assert.Equal("Archive", forest.ByItem["Item"].Parent?.Item); // same depth, no preference: name order
        Assert.Equal(new[] { "Auction" }, forest.ByItem["Item"].AlsoDeletedWith);
        Assert.Equal(new[] { "Item", "Pool", "Type" }, forest.ByItem["Linkshell"].CascadeDescendants.Where(n => n != "Invite" && n != "Auction" && n != "Archive"));
        Assert.Equal(2, forest.ByItem["Type"].Depth);
    }

    // A mutual cascade would recurse forever in a naive walker; the guard turns it into two roots.
    [Fact]
    public void Synthetic_MutualCascade_DoesNotHang_AndEveryItemIsRendered()
    {
        var forest = DataAdminCascadeForest<string>.Build(
            new[] { "A", "B", "C" },
            new (string, string)[] { ("A", "B"), ("B", "A"), ("C", "A") },
            name => name,
            _ => false);

        var rendered = new List<string>();
        void Walk(DataAdminCascadeForest<string>.Node node)
        {
            rendered.Add(node.Item);
            foreach (var child in node.Children)
            {
                Walk(child);
            }
        }
        foreach (var root in forest.Roots)
        {
            Walk(root);
        }

        Assert.Equal(new[] { "A", "B", "C" }, rendered.OrderBy(n => n, StringComparer.Ordinal));
        Assert.Equal("A", forest.ByItem["C"].Parent?.Item);
        Assert.Contains("C", forest.ByItem["A"].CascadeDescendants);
    }

    [Fact]
    public void Synthetic_EdgesToUnknownItems_AreIgnored()
    {
        var forest = DataAdminCascadeForest<string>.Build(
            new[] { "A" },
            new (string, string)[] { ("A", "Ghost"), ("Ghost", "A"), ("A", "A") },
            name => name,
            _ => false);

        Assert.Single(forest.Roots);
        Assert.Empty(forest.ByItem["A"].CascadeDescendants);
    }
}
