using System;
using System.Linq;
using LinkshellManagerDiscordApp.Services;
using LinkshellManagerDiscordApp.ViewModels;
using Xunit;

namespace LinkshellManager.Tests;

// The website's view of a Charts board. Everything derived on it has a twin on the catalog or in the
// Activity, and these pin the two together — a twin that silently disagrees is the failure mode this
// file exists for.
public class ChartBoardViewModelTests
{
    private static ChartBoardViewModel ModelFor(ChartBoard board) => new()
    {
        BoardKey = board.Key,
        Bosses = board.Bosses
            .Select(boss => new ChartBossCardViewModel
            {
                Boss = boss.Name,
                ThemeKey = boss.ThemeKey,
                PopItemOptions = boss.PopItems ?? Array.Empty<ChartPopItemOption>(),
            })
            .ToList(),
    };

    /// <summary>
    /// HasPopItemOptions decides whether Board.cshtml emits the &lt;script id="chart-pop-items"&gt;
    /// block that the boss-change picker swap reads. It must be the CATALOG's answer for every board.
    ///
    /// It was All-based while ChartBoard.HasPopItemOptions is Any-based, so a board where ONE card
    /// declared nothing shipped no script at all and the swap bailed on its first line — true for Sea
    /// from the moment its lottery NMs landed, and true for Sky the moment Kirin stopped declaring
    /// items. That divergence is the whole reason this test exists.
    /// </summary>
    [Fact]
    public void HasPopItemOptions_MatchesTheCatalogForEveryBoard()
    {
        foreach (var board in ChartBoardCatalog.Boards)
        {
            Assert.Equal(board.HasPopItemOptions, ModelFor(board).HasPopItemOptions);
        }
    }

    // Any, not All: boards MIX. Sea's lottery NMs and Sky's Kirin take no trade item, and the
    // picker-or-free-text choice is made per BOSS by _ChartPopItemField — this flag only says whether
    // the page needs to ship the swap script's data at all.
    [Fact]
    public void HasPopItemOptions_IsTrueWhenAnyCardDeclaresItems()
    {
        var mixed = new ChartBoardViewModel
        {
            Bosses = new()
            {
                new ChartBossCardViewModel { Boss = "Lottery NM" },
                new ChartBossCardViewModel
                {
                    Boss = "Popped NM",
                    PopItemOptions = new[] { new ChartPopItemOption("Gem of the North") },
                },
            },
        };
        Assert.True(mixed.HasPopItemOptions);

        var none = new ChartBoardViewModel
        {
            Bosses = new() { new ChartBossCardViewModel { Boss = "Lottery NM" } },
        };
        Assert.False(none.HasPopItemOptions);

        // A board with no cards at all has nothing to pick, and Any is already false for it.
        Assert.False(new ChartBoardViewModel().HasPopItemOptions);
    }

    /// <summary>
    /// The consolidated card lines: one row per ITEM with the quantities summed, in first-seen order
    /// and first-seen spelling. Twin of ChartBoardSectionComponent.consolidatedItems — a card shows
    /// what a boss needs and how many are held, while the holdings table keeps the per-holder rows.
    /// </summary>
    [Fact]
    public void ConsolidatedItems_SumOneLinePerItem_InFirstSeenOrder()
    {
        var card = new ChartBossCardViewModel
        {
            Boss = "Brigandish Blade",
            Items = new()
            {
                new ChartPopItemViewModel { ItemName = "Gem of the South", Quantity = 1 },
                new ChartPopItemViewModel { ItemName = "Summerstone", Quantity = 2 },
                // Same item, different holder — and typed in a different case, which must still fold.
                new ChartPopItemViewModel { ItemName = "gem of the south", Quantity = 3 },
            },
        };

        Assert.Equal(
            new[] { ("Gem of the South", 4), ("Summerstone", 2) },
            card.ConsolidatedItems.Select(line => (line.Name, line.Quantity)).ToArray());

        // TotalItems counts the LINES a card shows; TotalQuantity still counts every copy held.
        Assert.Equal(2, card.TotalItems);
        Assert.Equal(6, card.TotalQuantity);
    }
}
