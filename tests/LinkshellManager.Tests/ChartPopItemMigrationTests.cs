using System;
using System.Linq;
using LinkshellManagerDiscordApp.Migrations;
using LinkshellManagerDiscordApp.Services;
using Xunit;

namespace LinkshellManager.Tests;

/// <summary>
/// RefileSkyPopItemsOntoTheirDroppers and RefileSeaPopItemsOntoTheirDroppers move every row on their
/// board from the boss that item POPS to the boss that DROPS it, because that is what both boards came
/// to mean when their feeders joined them.
///
/// Their SQL runs once, against real data, with nothing watching. A typo in the mapping files rows
/// onto a card that does not list the item — or onto no card at all, where they would render under no
/// heading and be invisible. These tests check the mappings against the catalog, which is the only
/// thing that can say whether a destination is right.
/// </summary>
public class ChartPopItemMigrationTests
{
    private static readonly ChartBoard Sky = ChartBoardCatalog.Find(ChartBoardCatalog.Sky)!;
    private static readonly ChartBoard Sea = ChartBoardCatalog.Find(ChartBoardCatalog.Sea)!;

    /// <summary>Both refiles, so the shared assertions are written once and neither board can drift
    /// into being checked less carefully than the other.</summary>
    public static TheoryData<string, (string OldBoss, string Item, string NewBoss)[]> Refiles => new()
    {
        { ChartBoardCatalog.Sky, RefileSkyPopItemsOntoTheirDroppers.Moves },
        { ChartBoardCatalog.Sea, RefileSeaPopItemsOntoTheirDroppers.Moves },
    };

    private static ChartBoard BoardFor(string key) => ChartBoardCatalog.Find(key)!;

    /// <summary>Both of a card's lists. The refiles predate the pop/drop split, so a row they moved
    /// may now sit in either one - Sky's gems and seals are drops since ReclassifySkyChartItems.</summary>
    private static ChartPopItemOption[] ItemsOn(ChartBoard board, string boss) =>
        board.PopItemsFor(boss).Concat(board.DropItemsFor(boss)).ToArray();

    [Theory]
    [MemberData(nameof(Refiles))]
    public void EveryMove_LandsOnTheCardThatNowListsTheItem(
        string boardKey, (string OldBoss, string Item, string NewBoss)[] moves)
    {
        var board = BoardFor(boardKey);

        foreach (var (oldBoss, item, newBoss) in moves)
        {
            // Both ends name real cards. A move FROM a card that no longer exists would quietly do
            // nothing, and every source card on both boards survived its restructure.
            Assert.True(board.Find(oldBoss) is not null, $"Move source '{oldBoss}' is not a card on {boardKey}.");
            Assert.True(board.Find(newBoss) is not null, $"Move target '{newBoss}' is not a card on {boardKey}.");

            // The destination is the card whose picker offers the item. This is the assertion that
            // catches a swapped pair — "Winterstone" onto Zipacna instead of Olla Grande, or a Phuabo
            // organ onto Xzomit.
            Assert.Contains(
                ItemsOn(board, newBoss),
                option => string.Equals(option.Name, item, StringComparison.OrdinalIgnoreCase));

            // And it is NOT still offered by the card it came from, or the move would be pointless.
            Assert.DoesNotContain(
                ItemsOn(board, oldBoss),
                option => string.Equals(option.Name, item, StringComparison.OrdinalIgnoreCase));
        }
    }

    /// <summary>
    /// Nothing is moved twice, and nothing is moved to where it already was. Moving one item name
    /// twice would make Down() something other than a true inverse — the second move would catch what
    /// the first one placed — which is the assumption both migrations state in their comments.
    /// </summary>
    [Theory]
    [MemberData(nameof(Refiles))]
    public void EachItemMovesExactlyOnce_AndNeverToItself(
        string boardKey, (string OldBoss, string Item, string NewBoss)[] moves)
    {
        _ = boardKey;

        var items = moves.Select(move => move.Item).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Assert.Equal(items.Count, moves.Length);
        Assert.All(moves, move => Assert.NotEqual(move.OldBoss, move.NewBoss));
    }

    /// <summary>
    /// The Sky mapping is COMPLETE: every drop any Sky card lists was refiled onto exactly that card.
    /// Sky's POP items are not the refile's business — Curtana reached Brigandish Blade through
    /// ReclassifySkyChartItems, and Diorite and Ro'Maeve Water arrived with it, so no old row of
    /// either ever sat on the wrong card.
    /// </summary>
    [Fact]
    public void EverySkyDrop_WasRefiledOntoTheCardThatListsIt()
    {
        Assert.Equal(
            SkyDropPairs(),
            RefileSkyPopItemsOntoTheirDroppers.Moves
                .Select(move => $"{move.NewBoss} / {move.Item}")
                .OrderBy(pair => pair, StringComparer.Ordinal));
    }

    /// <summary>"Boss / Item" for every drop on Sky, sorted, so two sets compare with one Equal.</summary>
    private static IOrderedEnumerable<string> SkyDropPairs() =>
        Sky.Bosses
            .SelectMany(boss => (boss.DropItems ?? Array.Empty<ChartPopItemOption>())
                .Select(option => $"{boss.Name} / {option.Name}"))
            .OrderBy(pair => pair, StringComparer.Ordinal);

    /// <summary>
    /// ReclassifySkyChartItems flips exactly the rows whose item is now on a card's DROP list. One
    /// missing here leaves its old rows under "Pop Item" forever, since an edit never changes Kind;
    /// one extra flips rows the catalog still calls pop items.
    /// </summary>
    [Fact]
    public void Reclassify_FlipsExactlyTheSkyDrops()
    {
        Assert.Equal(
            SkyDropPairs(),
            ReclassifySkyChartItems.NowDrops
                .Select(drop => $"{drop.Boss} / {drop.Item}")
                .OrderBy(pair => pair, StringComparer.Ordinal));
    }

    /// <summary>Curtana lands on the card whose POP list offers it, and leaves Suzaku's lists entirely.</summary>
    [Fact]
    public void Reclassify_MovesCurtanaOntoTheCardItPops()
    {
        var (oldBoss, item, newBoss) = ReclassifySkyChartItems.CurtanaMove;

        Assert.Contains(Sky.PopItemsFor(newBoss), option => option.Name == item);
        Assert.DoesNotContain(ItemsOn(Sky, oldBoss), option => option.Name == item);
    }

    /// <summary>
    /// The new migration interpolates names into single-quoted SQL with no escaping, the same as the
    /// Sky refile. Ro'Maeve Water has an apostrophe, which is fine only because no old row of it
    /// exists to move — this fails the moment someone adds it to either list without escaping.
    /// </summary>
    [Fact]
    public void Reclassify_NamesNeedNoQuoteEscapingAndCarryNoLikeWildcard()
    {
        var (oldBoss, curtana, newBoss) = ReclassifySkyChartItems.CurtanaMove;
        var values = ReclassifySkyChartItems.NowDrops
            .SelectMany(drop => new[] { drop.Boss, drop.Item })
            .Concat(new[] { oldBoss, curtana, newBoss });

        Assert.All(values, value =>
        {
            Assert.DoesNotContain('\'', value);
            Assert.DoesNotContain('%', value);
            Assert.DoesNotContain('_', value);
        });
    }

    /// <summary>
    /// Sea's mapping is complete with NO exception: every single item on the old Sea board was filed
    /// under the boss it is spent on.
    /// So the moves and the board's items are the same set, in both directions — an item on the board
    /// and not in the list is one the migration forgot, and one in the list and not on the board is a
    /// move to a card whose picker will not offer it.
    /// </summary>
    [Fact]
    public void EverySeaItem_IsMoved_AndEveryMoveNamesAnItemOnTheBoard()
    {
        Assert.Equal(
            Sea.Bosses
                .SelectMany(boss => boss.PopItems ?? Array.Empty<ChartPopItemOption>())
                .Select(option => option.Name)
                .OrderBy(name => name, StringComparer.Ordinal),
            RefileSeaPopItemsOntoTheirDroppers.Moves
                .Select(move => move.Item)
                .OrderBy(name => name, StringComparer.Ordinal));
    }

    // Every farm NM gets exactly one row moved onto it — the one item it drops — and each of the four
    // gods gets exactly its own seal. That is the whole board's new shape, restated from the
    // migration's side.
    [Fact]
    public void EveryFarmNmAndGod_ReceivesExactlyWhatItDrops()
    {
        var byTarget = RefileSkyPopItemsOntoTheirDroppers.Moves
            .GroupBy(move => move.NewBoss, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase);

        foreach (var nm in HnmConfig.SkyFarmNmOrder)
        {
            Assert.True(byTarget.TryGetValue(nm, out var moves), $"Nothing is refiled onto {nm}.");
            Assert.Equal(1, moves);
        }

        foreach (var god in new[] { "Suzaku", "Genbu", "Seiryu", "Byakko" })
        {
            Assert.Equal(1, byTarget[god]);
            Assert.Contains(
                RefileSkyPopItemsOntoTheirDroppers.Moves,
                move => move.NewBoss == god && move.Item == $"Seal of {god}" && move.OldBoss == "Kirin");
        }

        // Kirin receives nothing: it is the terminus, and its four seals all left for their gods.
        Assert.False(byTarget.ContainsKey("Kirin"));
    }

    /// <summary>
    /// The same statement from Sea's side: every card that now holds something receives exactly one
    /// row, and Jailer of Love — the terminus — receives none, its three virtues having left for the
    /// Jailers that drop them.
    /// </summary>
    [Fact]
    public void EverySeaCardThatHoldsSomething_ReceivesExactlyOneRow()
    {
        var byTarget = RefileSeaPopItemsOntoTheirDroppers.Moves
            .GroupBy(move => move.NewBoss, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase);

        foreach (var card in Sea.Bosses.Where(boss => boss.PopItems is { Count: > 0 }))
        {
            Assert.True(byTarget.TryGetValue(card.Name, out var moves), $"Nothing is refiled onto {card.Name}.");
            Assert.Equal(1, moves);
        }

        Assert.False(byTarget.ContainsKey("Jailer of Love"));
        Assert.False(byTarget.ContainsKey("Absolute Virtue"));
    }

    /// <summary>
    /// Sea is the board with apostrophes in its boss names — Ix'aern and its two siblings — and both
    /// migrations build their SQL by interpolating names into a single-quoted literal. An unescaped
    /// apostrophe closes that literal early, and the failure lands on the one run the migration ever
    /// gets. Sea's Move() doubles them; this is the check that the names needing it are known.
    ///
    /// Also pins that nothing on either board carries a LIKE wildcard: both use ILIKE for
    /// case-insensitive equality, and a stray % or _ would silently widen the match.
    /// </summary>
    [Theory]
    [MemberData(nameof(Refiles))]
    public void NoMoveCarriesALikeWildcard_AndOnlySeaNeedsQuoteEscaping(
        string boardKey, (string OldBoss, string Item, string NewBoss)[] moves)
    {
        var values = moves.SelectMany(move => new[] { move.OldBoss, move.Item, move.NewBoss }).ToList();

        Assert.All(values, value =>
        {
            Assert.DoesNotContain('%', value);
            Assert.DoesNotContain('_', value);
        });

        var quoted = values.Where(value => value.Contains('\'')).Distinct(StringComparer.Ordinal)
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();

        if (boardKey == ChartBoardCatalog.Sky)
        {
            Assert.Empty(quoted);
        }
        else
        {
            Assert.Equal(new[] { "Ix'aern (DRG)", "Ix'aern (DRK)", "Ix'aern (MNK)" }, quoted);
        }
    }
}
