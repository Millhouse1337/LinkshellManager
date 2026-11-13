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
            "sky", "Brigandish Blade", "  curtana ", null, 1, null, ChartItemKinds.Pop);

        Assert.NotNull(draft);
        Assert.Equal("Curtana", draft!.ItemName);
        Assert.Equal(ChartItemKinds.Pop, draft.Kind);
    }

    // And the drop list canonicalises drop names, the same way.
    [Fact]
    public void NormalizeDraft_CanonicalisesADropNameAgainstTheDropList()
    {
        var draft = ChartBoardService.NormalizeDraft(
            "sky", "Olla Grande", "  winterstone ", null, 1, null, ChartItemKinds.Drop);

        Assert.NotNull(draft);
        Assert.Equal("Winterstone", draft!.ItemName);
        Assert.Equal(ChartItemKinds.Drop, draft.Kind);
    }

    // A drop the card does not list passes through as typed, exactly as a free-text pop item does
    // on a board with no list. That is the same contract, reached by a different list.
    [Fact]
    public void NormalizeDraft_PassesAnUnlistedDropNameThroughAsTyped()
    {
        var draft = ChartBoardService.NormalizeDraft(
            "sky", "Byakko", "  Byakko's Haidate ", null, 1, null, ChartItemKinds.Drop);

        Assert.NotNull(draft);
        Assert.Equal("Byakko's Haidate", draft!.ItemName);
        Assert.Equal(ChartItemKinds.Drop, draft.Kind);
    }

    // A pop item name typed into the DROP form is not canonicalised by the pop list. This pins that
    // the two lists are consulted separately rather than merged.
    [Fact]
    public void NormalizeDraft_DoesNotCanonicaliseADropNameAgainstThePopList()
    {
        var draft = ChartBoardService.NormalizeDraft(
            "sky", "Brigandish Blade", "curtana", null, 1, null, ChartItemKinds.Drop);

        Assert.NotNull(draft);
        Assert.Equal("curtana", draft!.ItemName);
    }

    // Every caller written before drop items existed omits the argument entirely.
    [Fact]
    public void NormalizeDraft_DefaultsToPopWhenNoKindIsGiven()
    {
        var draft = ChartBoardService.NormalizeDraft(
            "sea", "Jailer of Love", "Anything", null, 1, null);

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
            "Dynamis", "Xarcabard", "Montiont Silverpiece", "Millhouse", 3, "in the vault");

        Assert.NotNull(draft);
        Assert.Equal("Dynamis", draft!.Board);
        Assert.Equal("Xarcabard", draft.Boss);
        Assert.Equal(ChartItemKinds.Pop, draft.Kind);
    }
}
