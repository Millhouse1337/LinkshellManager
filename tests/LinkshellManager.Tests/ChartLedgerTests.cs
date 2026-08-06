using System;
using System.Collections.Generic;
using System.Linq;
using LinkshellManagerDiscordApp.Models;
using LinkshellManagerDiscordApp.Services;
using Xunit;

namespace LinkshellManager.Tests;

// Pins the ONE derivation of the Farming Credit Ledger, for every board.
//
// Farming credit is stored per (pop item × person) and the member × boss grid is derived from those
// rows on every read. That is deliberate: the boss card shows a per-item farmer count and the ledger
// shows a per-boss fraction, and if both were stored they could disagree on the same screen.
//
// The maths is board-agnostic on purpose — Sky's five gods and Sea's eight Jailers run the same code
// — so these tests use both to prove nothing is special-cased.
public class ChartLedgerTests
{
    private static readonly ChartBoard SkyBoard = ChartBoardCatalog.Find(ChartBoardCatalog.Sky)!;
    private static readonly ChartBoard SeaBoard = ChartBoardCatalog.Find(ChartBoardCatalog.Sea)!;

    private static ChartPopItem Item(string board, string boss, string name, params string[] creditedTo)
    {
        var item = new ChartPopItem
        {
            Id = Math.Abs(HashCode.Combine(board, boss, name)),
            LinkshellId = 1,
            Board = board,
            Boss = boss,
            ItemName = name,
        };
        foreach (var farmer in creditedTo)
        {
            item.Credits.Add(new ChartPopItemCredit
            {
                ChartPopItemId = item.Id,
                LinkshellId = 1,
                CharacterName = farmer,
            });
        }
        return item;
    }

    private static ChartPopItem Sky(string boss, string name, params string[] creditedTo) =>
        Item(ChartBoardCatalog.Sky, boss, name, creditedTo);

    private static ChartPopItem Sea(string boss, string name, params string[] creditedTo) =>
        Item(ChartBoardCatalog.Sea, boss, name, creditedTo);

    // Alts are carried for the "Held by" list only and mean nothing to the ledger — credit belongs
    // to a membership, and an alt is the same person.
    private static ChartRosterEntry Member(int id, string name, string rank = "Member") =>
        new(id, $"user-{id}", name, rank, Array.Empty<string>());

    private static ChartLedgerCell CellFor(ChartLedger ledger, string character, string boss) =>
        ledger.Rows.Single(row => row.CharacterName == character).Cells.Single(cell => cell.Boss == boss);

    // THE trap. A boss with no items entered is *vacuously* fully credited — every one of its zero
    // items is farmed. Reporting that as a green tick would tell a linkshell everyone is square on
    // Absolute Virtue when the truth is that nobody has typed its sins in yet.
    [Fact]
    public void ABossWithNoItems_IsNotTracked_NotCredited()
    {
        var ledger = ChartBoardService.BuildLedger(
            SeaBoard,
            new[] { Sea("Jailer of Temperance", "Temperance Organ", "Aeris") },
            new[] { Member(1, "Aeris") });

        var av = CellFor(ledger, "Aeris", "Absolute Virtue");
        Assert.Equal(ChartCreditStatuses.NotTracked, av.Status);
        Assert.Equal("—", av.Detail);
        Assert.Equal(0, av.TotalItems);
    }

    [Fact]
    public void AllItemsCredited_IsCredited_AndReadsAsAFraction()
    {
        var ledger = ChartBoardService.BuildLedger(
            SeaBoard,
            new[]
            {
                Sea("Jailer of Faith", "Faith Bangles", "Valeria"),
                Sea("Jailer of Faith", "Faith Torque", "Valeria"),
            },
            new[] { Member(1, "Valeria") });

        var cell = CellFor(ledger, "Valeria", "Jailer of Faith");
        Assert.Equal(ChartCreditStatuses.Credited, cell.Status);
        Assert.Equal("2 / 2", cell.Detail);
    }

    [Fact]
    public void SomeItemsCredited_IsPartial()
    {
        var ledger = ChartBoardService.BuildLedger(
            SeaBoard,
            new[]
            {
                Sea("Jailer of Love", "Love Halberd", "Selene"),
                Sea("Jailer of Love", "Love Torque", "Selene"),
                Sea("Jailer of Love", "Aura of Adulation", "Rexxar"),
            },
            new[] { Member(1, "Selene") });

        var cell = CellFor(ledger, "Selene", "Jailer of Love");
        Assert.Equal(ChartCreditStatuses.Partial, cell.Status);
        Assert.Equal("2 / 3", cell.Detail);
    }

    [Fact]
    public void NoItemsCredited_IsNone_AndStillShowsTheDenominator()
    {
        var ledger = ChartBoardService.BuildLedger(
            SeaBoard,
            new[] { Sea("Jailer of Hope", "Hope Staff", "Miyu") },
            new[] { Member(2, "Zeroth") });

        var cell = CellFor(ledger, "Zeroth", "Jailer of Hope");
        Assert.Equal(ChartCreditStatuses.None, cell.Status);
        Assert.Equal("0 / 1", cell.Detail);
    }

    // The row total counts ITEMS, not bosses cleared. Counting bosses would let somebody who did one
    // small boss outrank somebody who did most of the work on a large one — and it would not compare
    // across boards, where Sky has thirteen cards and Sea eleven.
    [Fact]
    public void RowTotal_CountsItems_AcrossTheWholeBoard()
    {
        var ledger = ChartBoardService.BuildLedger(
            SeaBoard,
            new[]
            {
                Sea("Jailer of Temperance", "Temperance Organ", "Aeris"),
                Sea("Jailer of Temperance", "Temperance Torque", "Aeris"),
                Sea("Jailer of Faith", "Faith Bangles", "Aeris"),
                Sea("Jailer of Faith", "Faith Torque", "Rexxar"),
            },
            new[] { Member(1, "Aeris") });

        var row = ledger.Rows.Single(item => item.CharacterName == "Aeris");
        Assert.Equal(3, row.TotalCredited);
        Assert.Equal(4, row.TotalTracked);
        Assert.Equal(75, row.CreditedPercent);
    }

    // Bosses with nothing tracked contribute 0 to BOTH sides, so an untouched board is 0 / 0 rather
    // than everyone sitting at 100%.
    [Fact]
    public void AnEmptyBoard_IsZeroOfZero_NotFullMarks()
    {
        var ledger = ChartBoardService.BuildLedger(
            SeaBoard, Array.Empty<ChartPopItem>(), new[] { Member(1, "Aeris") });

        var row = ledger.Rows.Single();
        Assert.Equal(0, row.TotalCredited);
        Assert.Equal(0, row.TotalTracked);
        // Never a divide by zero, and never a misleading 100%.
        Assert.Equal(0, row.CreditedPercent);
        Assert.All(row.Cells, cell => Assert.Equal(ChartCreditStatuses.NotTracked, cell.Status));
    }

    [Fact]
    public void CreditedPercent_Rounds()
    {
        var items = Enumerable.Range(0, 3)
            .Select(index => Sea("Jailer of Justice", $"Justice Item {index}", index < 2 ? "Aeris" : "Rexxar"))
            .ToList();

        var ledger = ChartBoardService.BuildLedger(SeaBoard, items, new[] { Member(1, "Aeris") });

        // 2 of 3 → 66.67 → 67
        Assert.Equal(67, ledger.Rows.Single(row => row.CharacterName == "Aeris").CreditedPercent);
    }

    // Dropping somebody from the roster must not erase the fact that they farmed. Same call the
    // treasury makes when a named member has left: shown, flagged, not deleted.
    [Fact]
    public void ACreditedNameNoLongerOnTheRoster_StillGetsARow()
    {
        var ledger = ChartBoardService.BuildLedger(
            SeaBoard,
            new[] { Sea("Jailer of Prudence", "Prudence Rod", "Ghostwind") },
            new[] { Member(1, "Aeris") });

        var departed = ledger.Rows.Single(row => row.CharacterName == "Ghostwind");
        Assert.False(departed.IsCurrentMember);
        Assert.Null(departed.MembershipId);
        Assert.Null(departed.Rank);
        Assert.Equal(
            ChartCreditStatuses.Credited,
            departed.Cells.Single(cell => cell.Boss == "Jailer of Prudence").Status);

        Assert.True(ledger.Rows.Single(row => row.CharacterName == "Aeris").IsCurrentMember);
    }

    // Current members first, ex-members after, so the working roster is not interleaved with people
    // who left.
    [Fact]
    public void CurrentMembersSortBeforeExMembers()
    {
        var ledger = ChartBoardService.BuildLedger(
            SkyBoard,
            new[] { Sky("Faust", "Summerstone", "Aaronson") },
            new[] { Member(1, "Zeke") });

        Assert.Equal(new[] { "Zeke", "Aaronson" }, ledger.Rows.Select(row => row.CharacterName).ToArray());
    }

    /// <summary>The rank shown under the member's name comes straight off the roster entry.</summary>
    [Fact]
    public void RowCarriesTheRosterRank()
    {
        var ledger = ChartBoardService.BuildLedger(
            SkyBoard, Array.Empty<ChartPopItem>(), new[] { Member(1, "Aeris", "Officer") });

        Assert.Equal("Officer", ledger.Rows.Single().Rank);
    }

    [Fact]
    public void DuplicateCreditsForOnePerson_CountOnce()
    {
        var ledger = ChartBoardService.BuildLedger(
            SkyBoard,
            new[] { Sky("Olla Grande", "Winterstone", "Edicius", "Edicius") },
            new[] { Member(1, "Edicius") });

        var cell = CellFor(ledger, "Edicius", "Olla Grande");
        Assert.Equal(1, cell.CreditedItems);
        Assert.Equal(ChartCreditStatuses.Credited, cell.Status);
    }

    // FFXI names are case-insensitive in practice and officers type them by hand.
    [Fact]
    public void CharacterNameMatchingIsCaseInsensitive()
    {
        var ledger = ChartBoardService.BuildLedger(
            SkyBoard,
            new[] { Sky("Mother Globe", "Springstone", "  eDiCiUs ") },
            new[] { Member(1, "Edicius") });

        Assert.Equal(ChartCreditStatuses.Credited, CellFor(ledger, "Edicius", "Mother Globe").Status);
        // ...and they are not ALSO listed as a departed farmer under the other spelling.
        Assert.Single(ledger.Rows);
    }

    // A row typed as "olla grande" still lands on the Olla Grande card rather than vanishing. A
    // two-word boss on purpose: the farm NMs put spaces into names that used to be single words.
    [Fact]
    public void BossMatchingIsCaseInsensitive()
    {
        var ledger = ChartBoardService.BuildLedger(
            SkyBoard,
            new[] { Sky("olla grande", "Winterstone", "Kie") },
            new[] { Member(1, "Kie") });

        Assert.Equal(1, CellFor(ledger, "Kie", "Olla Grande").TotalItems);
    }

    [Fact]
    public void BossColumnsComeBackInBoardOrder()
    {
        // Sky's order is PATH order — each god's two feeders then the god, path by path, then Kirin.
        // That is the order its cards stack into columns, and the ledger takes the same one.
        var sky = ChartBoardService.BuildLedger(
            SkyBoard, Array.Empty<ChartPopItem>(), new[] { Member(1, "Edicius") });
        Assert.Equal(
            new[]
            {
                "Faust", "Brigandish Blade", "Suzaku",
                "Zipacna", "Olla Grande", "Genbu",
                "Steam Cleaner", "Mother Globe", "Seiryu",
                "Despot", "Ullikummi", "Byakko",
                "Kirin",
            },
            sky.Bosses.ToArray());

        // Sea's order is PATH order, like Sky's: everything that feeds a Jailer then the Jailer, path
        // by path, then the final stage. That is the order its cards stack into columns, and it reads
        // downwards as the order the path is farmed in.
        var sea = ChartBoardService.BuildLedger(
            SeaBoard, Array.Empty<ChartPopItem>(), new[] { Member(1, "Aeris") });
        Assert.Equal(
            new[]
            {
                "Ghrah", "Jailer of Fortitude", "Ix'aern (DRK)", "Xzomit", "Jailer of Justice",
                "Aern", "Ix'aern (MNK)", "Jailer of Temperance", "Phuabo", "Jailer of Hope",
                "Euvhi", "Jailer of Faith", "Ix'aern (DRG)", "Hpemde", "Jailer of Prudence",
                "Jailer of Love", "Absolute Virtue",
            },
            sea.Bosses.ToArray());

        Assert.Equal(sea.Bosses, sea.Rows.Single().Cells.Select(cell => cell.Boss).ToArray());
    }

    // Rows for one board must never be counted on another. Sharing one table across boards makes
    // this the thing most likely to go wrong, so it is pinned: a Sky row handed to the Sea board
    // contributes nothing rather than landing on some card by accident.
    [Fact]
    public void RowsFromAnotherBoard_AreNotCounted()
    {
        var ledger = ChartBoardService.BuildLedger(
            SeaBoard,
            new[] { Sky("Suzaku", "Summerstone", "Aeris") },
            new[] { Member(1, "Aeris") });

        var row = ledger.Rows.Single();
        Assert.Equal(0, row.TotalTracked);
        Assert.All(row.Cells, cell => Assert.Equal(ChartCreditStatuses.NotTracked, cell.Status));
    }
}
