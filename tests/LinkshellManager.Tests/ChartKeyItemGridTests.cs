using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LinkshellManagerDiscordApp.Data;
using LinkshellManagerDiscordApp.Models;
using LinkshellManagerDiscordApp.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace LinkshellManager.Tests;

// Presence is the fact: a row means "has it" and no row means "needs it". Everything the grid and
// the card drawers show is derived from that one asymmetry, so these pin the derivation rather than
// any stored total.
public class ChartKeyItemGridTests
{
    private static readonly ChartBoard Dynamis = ChartBoardCatalog.Find(ChartBoardCatalog.Dynamis)!;

    private static ApplicationDbContext NewInMemoryContext() =>
        new(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static ChartRosterEntry Member(int id, string name) =>
        new(id, $"user-{id}", name, "Member", Array.Empty<string>());

    private static ChartMemberKeyItem Has(int membershipId, string name, string keyItem) => new()
    {
        LinkshellId = 1,
        Board = ChartBoardCatalog.Dynamis,
        KeyItemName = keyItem,
        MembershipId = membershipId,
        CharacterName = name,
    };

    /// <summary>
    /// Columns come from the CATALOG, in catalog order - never from the data. A key item nobody has
    /// yet still gets a column reading "0 of N", which is the whole point of the grid: deriving
    /// columns from what people already hold would hide exactly the rows that matter.
    /// </summary>
    [Fact]
    public void BuildGrid_DrawsEveryCatalogColumn_EvenOnesNobodyHas()
    {
        var grid = ChartKeyItemService.BuildGrid(
            Dynamis, Array.Empty<ChartMemberKeyItem>(), new[] { Member(1, "Aeris"), Member(2, "Bob") });

        Assert.Equal(
            Dynamis.KeyItems.Select(item => item.Name).ToArray(),
            grid.Columns.Select(column => column.Name).ToArray());
        Assert.All(grid.Columns, column =>
        {
            Assert.Equal(0, column.HaveCount);
            Assert.Equal(2, column.TotalMembers);
            Assert.Equal(new[] { "Aeris", "Bob" }, column.MissingCharacterNames);
        });
    }

    // The stored fact is "has it"; the useful question is the other one, so the drawer's list is the
    // inverse against the CURRENT roster.
    [Fact]
    public void BuildGrid_ListsWhoIsStillMissingEachKeyItem()
    {
        var rows = new[] { Has(1, "Aeris", "Hydra Corps Lantern") };
        var grid = ChartKeyItemService.BuildGrid(
            Dynamis, rows, new[] { Member(1, "Aeris"), Member(2, "Bob"), Member(3, "Carol") });

        var lantern = grid.Columns.Single(column => column.Name == "Hydra Corps Lantern");
        Assert.Equal(1, lantern.HaveCount);
        Assert.Equal(3, lantern.TotalMembers);
        Assert.Equal(new[] { "Bob", "Carol" }, lantern.MissingCharacterNames);
    }

    // A row stored in another case still lands in its column: the name is canonicalised on the way
    // in, but a row written before a catalog fix must not fall out of the grid.
    [Fact]
    public void BuildGrid_MatchesAStoredNameCaseInsensitively()
    {
        var rows = new[] { Has(1, "Aeris", "  hydra corps lantern ") };
        var grid = ChartKeyItemService.BuildGrid(Dynamis, rows, new[] { Member(1, "Aeris") });

        Assert.Equal(1, grid.Columns.Single(column => column.Name == "Hydra Corps Lantern").HaveCount);
    }

    // A name the catalog does not have belongs to no column, so it is ignored rather than inventing
    // one. NormalizeKeyItemName keeps these out on the way in; this is the read-side backstop.
    [Fact]
    public void BuildGrid_IgnoresAStoredNameTheCatalogDoesNotHave()
    {
        var rows = new[] { Has(1, "Aeris", "Hydra Corps Monocle") };
        var grid = ChartKeyItemService.BuildGrid(Dynamis, rows, new[] { Member(1, "Aeris") });

        Assert.All(grid.Columns, column => Assert.Equal(0, column.HaveCount));
        Assert.DoesNotContain(grid.Columns, column => column.Name == "Hydra Corps Monocle");
    }

    [Fact]
    public void BuildGrid_AlignsEachRowToTheColumnOrder()
    {
        var rows = new[]
        {
            Has(1, "Aeris", "Vial of Shrouded Sand"),
            Has(1, "Aeris", "Hydra Corps Eyeglass"),
        };
        var grid = ChartKeyItemService.BuildGrid(Dynamis, rows, new[] { Member(1, "Aeris"), Member(2, "Bob") });

        var aeris = grid.Rows.Single(row => row.CharacterName == "Aeris");
        Assert.Equal(Dynamis.KeyItems.Count, aeris.Has.Count);
        Assert.True(aeris.Has[0]);                                   // Vial of Shrouded Sand, column 0
        Assert.True(aeris.Has[Dynamis.KeyItems.ToList().FindIndex(item => item.Name == "Hydra Corps Eyeglass")]);
        Assert.Equal(2, aeris.HaveCount);
        Assert.Equal(11, aeris.TotalColumns);

        var bob = grid.Rows.Single(row => row.CharacterName == "Bob");
        Assert.All(bob.Has, Assert.False);
        Assert.Equal(0, bob.HaveCount);
        Assert.Equal(0, bob.HavePercent);
    }

    /// <summary>
    /// Deliberately unlike ChartBoardService.BuildLedger, which keeps departed farmers.
    ///
    /// Farming credit is a historical fact worth preserving; "does this person have the key item" is
    /// only a question about people who are here. The orphan row is harmless - there is no FK to
    /// delete it, by the second-cascade-path design - and re-adding the same person restores it.
    /// </summary>
    [Fact]
    public void BuildGrid_IgnoresRowsForAMembershipNoLongerOnTheRoster()
    {
        var rows = new[]
        {
            Has(1, "Aeris", "Hydra Corps Lantern"),
            Has(404, "Departed", "Hydra Corps Lantern"),
        };
        var grid = ChartKeyItemService.BuildGrid(Dynamis, rows, new[] { Member(1, "Aeris") });

        Assert.Single(grid.Rows);
        var lantern = grid.Columns.Single(column => column.Name == "Hydra Corps Lantern");
        Assert.Equal(1, lantern.HaveCount);
        Assert.Equal(1, lantern.TotalMembers);
        Assert.Empty(lantern.MissingCharacterNames);
    }

    // Twin of ChartLedgerRow.CreditedPercent, including the never-divide-by-zero rule. Limbus tracks
    // no key items, so it is the real board with no columns at all.
    [Fact]
    public void BuildGrid_ReadsZeroPercentOnABoardWithNoColumns()
    {
        var limbus = ChartBoardCatalog.Find(ChartBoardCatalog.Limbus)!;
        var grid = ChartKeyItemService.BuildGrid(
            limbus, Array.Empty<ChartMemberKeyItem>(), new[] { Member(1, "Aeris") });

        Assert.Empty(grid.Columns);
        Assert.Equal(0, grid.Rows.Single().HavePercent);
    }

    [Fact]
    public void BuildGrid_RoundsThePercentageTheWayTheLedgerDoes()
    {
        var rows = Dynamis.KeyItems.Take(3).Select(item => Has(1, "Aeris", item.Name)).ToArray();
        var grid = ChartKeyItemService.BuildGrid(Dynamis, rows, new[] { Member(1, "Aeris") });

        // 3 of 11.
        Assert.Equal(27, grid.Rows.Single().HavePercent);
    }

    // ---- CanSetKeyItemFor: the second ownership rule ----------------------------

    [Fact]
    public void CanSetKeyItemFor_LetsAMemberTickTheirOwn() =>
        Assert.True(ChartKeyItemService.CanSetKeyItemFor(7, viewerMembershipId: 7, canManage: false));

    [Fact]
    public void CanSetKeyItemFor_RefusesAMemberTickingSomebodyElses() =>
        Assert.False(ChartKeyItemService.CanSetKeyItemFor(7, viewerMembershipId: 8, canManage: false));

    [Fact]
    public void CanSetKeyItemFor_LetsAnOfficerTickAnybodys() =>
        Assert.True(ChartKeyItemService.CanSetKeyItemFor(7, viewerMembershipId: 8, canManage: true));

    // A viewer with no membership never matches, including against membership 0.
    [Fact]
    public void CanSetKeyItemFor_RefusesAViewerWithNoMembership()
    {
        Assert.False(ChartKeyItemService.CanSetKeyItemFor(7, viewerMembershipId: null, canManage: false));
        Assert.False(ChartKeyItemService.CanSetKeyItemFor(0, viewerMembershipId: null, canManage: false));
    }

    // ---- SetAsync: the write, and the two boundaries it draws --------------------

    private static ApplicationDbContext WithRoster()
    {
        var db = NewInMemoryContext();
        db.AppUserLinkshells.Add(new AppUserLinkshell
        {
            Id = 1, LinkshellId = 1, AppUserId = "user-1", CharacterName = "Aeris", Rank = "Member",
        });
        db.AppUserLinkshells.Add(new AppUserLinkshell
        {
            Id = 2, LinkshellId = 99, AppUserId = "user-2", CharacterName = "Stranger", Rank = "Member",
        });
        db.SaveChanges();
        return db;
    }

    private static readonly ChartBoardActor Actor = new("user-1", "Aeris");

    [Fact]
    public async Task SetAsync_TicksACellAndStampsWhoDidIt()
    {
        using var db = WithRoster();
        var error = await new ChartKeyItemService(db).SetAsync(
            1, "dynamis", "  hydra corps lantern ", 1, has: true, Actor, CancellationToken.None);
        await db.SaveChangesAsync();

        Assert.Null(error);
        var row = db.ChartMemberKeyItems.Single();
        // Canonicalised on the way in, so it can never land in a column the grid does not draw.
        Assert.Equal("Hydra Corps Lantern", row.KeyItemName);
        Assert.Equal("Dynamis", row.Board);
        // The name comes off the ROSTER, never off the request.
        Assert.Equal("Aeris", row.CharacterName);
        Assert.Equal("user-1", row.SetByAppUserId);
    }

    // Presence is the fact, so unticking DELETES rather than storing a false.
    [Fact]
    public async Task SetAsync_UntickingDeletesTheRow()
    {
        using var db = WithRoster();
        var service = new ChartKeyItemService(db);

        await service.SetAsync(1, "Dynamis", "Hydra Corps Lantern", 1, true, Actor, CancellationToken.None);
        await db.SaveChangesAsync();
        await service.SetAsync(1, "Dynamis", "Hydra Corps Lantern", 1, false, Actor, CancellationToken.None);
        await db.SaveChangesAsync();

        Assert.Empty(db.ChartMemberKeyItems);
    }

    // A double-clicked checkbox is a UI slip, not two facts - and the unique index would refuse the
    // second row anyway.
    [Fact]
    public async Task SetAsync_IsIdempotentInBothDirections()
    {
        using var db = WithRoster();
        var service = new ChartKeyItemService(db);

        for (var attempt = 0; attempt < 3; attempt++)
        {
            await service.SetAsync(1, "Dynamis", "Hydra Corps Lantern", 1, true, Actor, CancellationToken.None);
            await db.SaveChangesAsync();
        }
        Assert.Single(db.ChartMemberKeyItems);

        for (var attempt = 0; attempt < 3; attempt++)
        {
            await service.SetAsync(1, "Dynamis", "Hydra Corps Lantern", 1, false, Actor, CancellationToken.None);
            await db.SaveChangesAsync();
        }
        Assert.Empty(db.ChartMemberKeyItems);
    }

    // Same boundary ResolveCreditsAsync draws: a request cannot tick a cell for somebody else's
    // member.
    [Fact]
    public async Task SetAsync_RefusesAMembershipFromAnotherLinkshell()
    {
        using var db = WithRoster();
        var error = await new ChartKeyItemService(db).SetAsync(
            1, "Dynamis", "Hydra Corps Lantern", 2, true, Actor, CancellationToken.None);

        Assert.NotNull(error);
        Assert.Empty(db.ChartMemberKeyItems);
    }

    [Fact]
    public async Task SetAsync_RefusesAKeyItemTheCatalogDoesNotHave()
    {
        using var db = WithRoster();
        Assert.NotNull(await new ChartKeyItemService(db).SetAsync(
            1, "Dynamis", "Hydra Corps Monocle", 1, true, Actor, CancellationToken.None));
        Assert.Empty(db.ChartMemberKeyItems);
    }

    [Fact]
    public async Task SetAsync_RefusesABoardThatTracksNoKeyItems()
    {
        using var db = WithRoster();
        Assert.NotNull(await new ChartKeyItemService(db).SetAsync(
            1, "Limbus", "Hydra Corps Lantern", 1, true, Actor, CancellationToken.None));
        Assert.Empty(db.ChartMemberKeyItems);
    }
}
