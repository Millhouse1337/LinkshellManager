using System.Collections.Generic;
using System.Linq;
using LinkshellManagerDiscordApp.Services;
using LinkshellManagerDiscordApp.ViewModels;
using Xunit;

// ChartBoardViewModel.BossGroups is the website's half of the grouping feature, and its twin is the
// Activity's bossGroups computed. Both collapse ADJACENT cards with the same label into one run,
// which is what lets a single .chart-grid carry a full-row heading without a sub-grid per group.
//
// ChartBoardCatalogTests guards the catalog data (runs are contiguous, a board groups all or none);
// this guards the transform that turns that data into headings.
namespace LinkshellManager.Tests;

public class ChartBossGroupingTests
{
    [Fact]
    public void UngroupedBoard_IsOneRunWithNoLabel()
    {
        // The Sky and Sea shape. One run with a null label means the view's heading test never fires,
        // so those boards render exactly the markup they did before groups existed.
        var model = BoardOf(("Byakko", null), ("Kirin", null), ("Suzaku", null));

        var run = Assert.Single(model.BossGroups);
        Assert.Null(run.Label);
        Assert.Equal(new[] { "Byakko", "Kirin", "Suzaku" }, run.Bosses.Select(boss => boss.Boss));
    }

    [Fact]
    public void GroupedBoard_SplitsIntoRuns_PreservingCatalogOrder()
    {
        // The Dynamis shape, in miniature.
        var model = BoardOf(
            ("San d'Oria", "Original Dynamis"),
            ("Bastok", "Original Dynamis"),
            ("Valkurm", "Dreamworld Dynamis"),
            ("Tavnazia", "Dreamworld Dynamis"));

        var runs = model.BossGroups;

        Assert.Equal(2, runs.Count);
        Assert.Equal("Original Dynamis", runs[0].Label);
        Assert.Equal(new[] { "San d'Oria", "Bastok" }, runs[0].Bosses.Select(boss => boss.Boss));
        Assert.Equal("Dreamworld Dynamis", runs[1].Label);
        Assert.Equal(new[] { "Valkurm", "Tavnazia" }, runs[1].Bosses.Select(boss => boss.Boss));
    }

    // Runs, NOT a GroupBy. A group-by would quietly pull the third card up beside the first to match
    // the first sighting of "A" — reordering the board the catalog declared. Splitting instead makes
    // the duplicate heading visible, which is the failure ChartBoardCatalogTests then forbids.
    [Fact]
    public void ARestartedGroup_SplitsRatherThanReordering()
    {
        var model = BoardOf(("First", "A"), ("Second", "B"), ("Third", "A"));

        var runs = model.BossGroups;

        Assert.Equal(new[] { "A", "B", "A" }, runs.Select(run => run.Label));
        Assert.Equal(new[] { "First", "Second", "Third" }, runs.SelectMany(run => run.Bosses).Select(boss => boss.Boss));
    }

    // EVERY kind belongs to its group's run, in declared order. Mini NMs and finales used to render
    // in their own blocks beneath the grid, which detached them from the heading they belong under —
    // Limbus has a chip area and a finale inside EACH of Apollyon and Temenos, so Apollyon's pair
    // would have surfaced below all of Temenos's cards.
    [Fact]
    public void EveryKindStaysInItsGroupRun()
    {
        var model = new ChartBoardViewModel
        {
            Bosses =
            {
                Card("NW", "Apollyon"),
                Card("Central", "Apollyon", ChartBossKinds.Final),
                Card("CS", "Apollyon", ChartBossKinds.MiniNm),
                Card("Western Tower", "Temenos"),
                Card("Central 4F", "Temenos", ChartBossKinds.Final),
                Card("Central B1", "Temenos", ChartBossKinds.MiniNm),
            },
        };

        var runs = model.BossGroups;

        Assert.Equal(2, runs.Count);
        Assert.Equal(new[] { "NW", "Central", "CS" }, runs[0].Bosses.Select(boss => boss.Boss));
        Assert.Equal(new[] { "Western Tower", "Central 4F", "Central B1" }, runs[1].Bosses.Select(boss => boss.Boss));

        // Both finales survive. FinalBoss was a FirstOrDefault, so the second one rendered nowhere.
        Assert.Equal(2, runs.SelectMany(run => run.Bosses).Count(boss => boss.Kind == ChartBossKinds.Final));
    }

    [Fact]
    public void EmptyBoard_HasNoRuns()
    {
        Assert.Empty(new ChartBoardViewModel().BossGroups);
    }

    private static ChartBoardViewModel BoardOf(params (string Boss, string? Group)[] bosses)
    {
        var model = new ChartBoardViewModel();
        foreach (var (boss, group) in bosses)
        {
            model.Bosses.Add(Card(boss, group));
        }
        return model;
    }

    private static ChartBossCardViewModel Card(string boss, string? group, string kind = ChartBossKinds.Standard) =>
        new() { Boss = boss, Group = group, Kind = kind };
}
