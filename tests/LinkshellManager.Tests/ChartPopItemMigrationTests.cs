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
                board.PopItemsFor(newBoss),
                option => string.Equals(option.Name, item, StringComparison.OrdinalIgnoreCase));

            // And it is NOT still offered by the card it came from, or the move would be pointless.
            Assert.DoesNotContain(
                board.PopItemsFor(oldBoss),
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
    /// The Sky mapping is COMPLETE: every item any Sky card now lists is either moved or was already
    /// filed correctly. Curtana is the only one in the second category — Suzaku dropped it before the
    /// restructure and still does — so anything else appearing here is an item the migration forgot,
    /// and rows of it would be stranded on whichever card used to hold them.
    /// </summary>
    [Fact]
    public void EverySkyItem_IsEitherMovedOrWasAlreadyInPlace()
    {
        var moved = RefileSkyPopItemsOntoTheirDroppers.Moves
            .Select(move => move.Item)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var onTheBoard = Sky.Bosses
            .SelectMany(boss => boss.PopItems ?? Array.Empty<ChartPopItemOption>())
            .Select(option => option.Name)
            .ToList();

        Assert.Equal(
            new[] { "Curtana" },
            onTheBoard.Where(name => !moved.Contains(name)).ToArray());
    }

    /// <summary>
    /// Sea's mapping is complete with NO exception: unlike Sky, where Curtana already sat on the god
    /// that drops it, every single item on the old Sea board was filed under the boss it is spent on.
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
