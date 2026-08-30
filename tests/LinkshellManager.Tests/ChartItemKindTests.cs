using System;
using LinkshellManagerDiscordApp.Models;
using LinkshellManagerDiscordApp.Services;
using Xunit;

namespace LinkshellManager.Tests;

// Pop and drop rows share one table, one credit model and one ledger; the ONLY thing Kind changes is
// which list a name is canonicalised against and which pill the row wears. These pin that, and pin
// the one place the distinction must NOT reach.
public class ChartItemKindTests
{
    [Theory]
    [InlineData("Drop", ChartItemKinds.Drop)]
    [InlineData("  drop ", ChartItemKinds.Drop)]
    [InlineData("DROP", ChartItemKinds.Drop)]
    [InlineData("Pop", ChartItemKinds.Pop)]
    [InlineData("pop", ChartItemKinds.Pop)]
    // Anything unrecognised, INCLUDING silence, reads as Pop: a form or a payload that says nothing
    // about kind is one written before drops existed, and the right reading of silence is the
    // original behaviour.
    [InlineData("", ChartItemKinds.Pop)]
    [InlineData("   ", ChartItemKinds.Pop)]
    [InlineData(null, ChartItemKinds.Pop)]
    [InlineData("Loot", ChartItemKinds.Pop)]
    public void NormalizeItemKind_DefaultsToPop(string? typed, string expected) =>
        Assert.Equal(expected, ChartBoardCatalog.NormalizeItemKind(typed));

    // The pop list still canonicalises pop names, exactly as before drops existed.
    [Fact]
    public void NormalizeDraft_CanonicalisesAPopNameAgainstThePopList()
    {
        var draft = ChartBoardService.NormalizeDraft(
            "sky", "Olla Grande", "  winterstone ", null, null, 1, null, ChartItemKinds.Pop);

        Assert.NotNull(draft);
        Assert.Equal("Winterstone", draft!.ItemName);
        Assert.Equal(ChartItemKinds.Pop, draft.Kind);
    }

    // A DROP draft is canonicalised against the DROP list, which is empty on every board today - so
    // the name passes through as typed, exactly as a free-text pop item does on a board with no
    // list. That is the same contract, reached by a different list.
    [Fact]
    public void NormalizeDraft_PassesAnUnlistedDropNameThroughAsTyped()
    {
        var draft = ChartBoardService.NormalizeDraft(
            "sky", "Byakko", "  Byakko's Haidate ", null, null, 1, null, ChartItemKinds.Drop);

        Assert.NotNull(draft);
        Assert.Equal("Byakko's Haidate", draft!.ItemName);
        Assert.Equal(ChartItemKinds.Drop, draft.Kind);
    }

    // A pop item name is NOT canonicalised by the drop list. Nothing declares drop items yet, so
    // this is really pinning that the two lists are consulted separately rather than merged.
    [Fact]
    public void NormalizeDraft_DoesNotCanonicaliseADropNameAgainstThePopList()
    {
        var draft = ChartBoardService.NormalizeDraft(
            "sky", "Olla Grande", "winterstone", null, null, 1, null, ChartItemKinds.Drop);

        Assert.NotNull(draft);
        Assert.Equal("winterstone", draft!.ItemName);
    }

    // Every caller written before drop items existed omits the argument entirely.
    [Fact]
    public void NormalizeDraft_DefaultsToPopWhenNoKindIsGiven()
    {
        var draft = ChartBoardService.NormalizeDraft(
            "sea", "Jailer of Love", "Anything", null, null, 1, null);

        Assert.NotNull(draft);
        Assert.Equal(ChartItemKinds.Pop, draft!.Kind);
    }

    /// <summary>
    /// THE anti-stranding test.
    ///
    /// Dynamis and Limbus no longer offer an add form, but they still hold rows officers entered
    /// before that, and NormalizeDraft is on the EDIT path as well as the add path. A feature check
    /// moved into it - which reads like a tidy simplification - would refuse every one of those rows
    /// and make them permanently uneditable and undeletable. The check belongs in the two Add
    /// actions only.
    /// </summary>
    [Fact]
    public void NormalizeDraft_DoesNotRefuseARowOnABoardThatNoLongerTakesAdds()
    {
        Assert.False(ChartBoardCatalog.Find(ChartBoardCatalog.Dynamis)!.AllowsPopItems);

        var draft = ChartBoardService.NormalizeDraft(
            "Dynamis", "Xarcabard", "Montiont Silverpiece", "Millhouse", null, 3, "in the vault");

        Assert.NotNull(draft);
        Assert.Equal("Dynamis", draft!.Board);
        Assert.Equal("Xarcabard", draft.Boss);
        Assert.Equal(ChartItemKinds.Pop, draft.Kind);
    }

    // Drop rows are farmed and credited exactly like pop rows, so the ledger must not look at Kind
    // at all. Counting them separately would tell somebody who was there that they are only partly
    // square with a boss they cleared.
    [Fact]
    public void BuildLedger_CountsDropRowsAlongsidePopRows()
    {
        var board = ChartBoardCatalog.Find(ChartBoardCatalog.Sky)!;
        var roster = new[] { new ChartRosterEntry(1, "user-1", "Aeris", "Member", Array.Empty<string>()) };

        var items = new[]
        {
            Row(1, "Byakko", "Seal of Byakko", ChartItemKinds.Pop, "Aeris"),
            Row(2, "Byakko", "Byakko's Haidate", ChartItemKinds.Drop, "Aeris"),
        };

        var ledger = ChartBoardService.BuildLedger(board, items, roster);
        var row = Assert.Single(ledger.Rows);
        var byakko = Assert.Single(row.Cells, cell => cell.Boss == "Byakko");

        Assert.Equal(2, byakko.TotalItems);
        Assert.Equal(2, byakko.CreditedItems);
        Assert.Equal(ChartCreditStatuses.Credited, byakko.Status);
    }

    private static ChartPopItem Row(int id, string boss, string name, string kind, string creditedTo)
    {
        var item = new ChartPopItem
        {
            Id = id,
            LinkshellId = 1,
            Board = ChartBoardCatalog.Sky,
            Boss = boss,
            ItemName = name,
            Kind = kind,
        };
        item.Credits.Add(new ChartPopItemCredit
        {
            ChartPopItemId = id,
            LinkshellId = 1,
            CharacterName = creditedTo,
        });
        return item;
    }
}
