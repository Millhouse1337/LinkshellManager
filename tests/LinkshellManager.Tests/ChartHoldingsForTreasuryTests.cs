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

// Treasury -> Items lists the Charts boards' held items under its own stockpile, read straight off
// the chart rows (ChartBoardService.LoadHoldingsAsync) rather than copied into the Items table.
public class ChartHoldingsForTreasuryTests
{
    private const int LinkshellId = 12;

    private static ApplicationDbContext NewInMemoryContext() =>
        new(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static ChartPopItem Row(int id, string board, string boss, string item, int quantity,
        string? holder = null, int linkshellId = LinkshellId) => new()
    {
        Id = id,
        LinkshellId = linkshellId,
        Board = board,
        Boss = boss,
        Kind = ChartItemKinds.Pop,
        ItemName = item,
        Quantity = quantity,
        HeldByCharacterName = holder,
    };

    [Fact]
    public async Task HeldItems_ComeBackInBoardThenCardOrder_SkippingEmptyRowsAndOtherLinkshells()
    {
        var sky = ChartBoardCatalog.Find("Sky")!;
        var sea = ChartBoardCatalog.Find("Sea")!;
        var skyFirst = sky.Bosses[0].Name;
        var skySecond = sky.Bosses[1].Name;
        var seaFirst = sea.Bosses[0].Name;

        using var db = NewInMemoryContext();
        db.ChartPopItems.AddRange(
            Row(1, "Sea", seaFirst, "Sea thing", 1, "  Mill  "),
            Row(2, "Sky", skySecond, "Zebra item", 2),
            Row(3, "Sky", skySecond, "Apple item", 1),
            Row(4, "Sky", skyFirst, "First card item", 3),
            Row(5, "Sky", skyFirst, "Nobody holds this", 0),
            Row(6, "Sky", skyFirst, "Another shell's", 4, linkshellId: 99));
        await db.SaveChangesAsync();

        var holdings = await new ChartBoardService(db).LoadHoldingsAsync(LinkshellId, CancellationToken.None);

        Assert.Equal(new[] { 4, 3, 2, 1 }, holdings.Select(h => h.Id));
        Assert.Equal(sky.Label, holdings[0].BoardLabel);
        Assert.Equal(sea.Label, holdings[3].BoardLabel);
        Assert.Equal("Mill", holdings[3].HeldByCharacterName);
        Assert.Null(holdings[0].HeldByCharacterName);
    }

    [Fact]
    public async Task ARowOnACardTheCatalogNoLongerHas_StillListsLast()
    {
        var sky = ChartBoardCatalog.Find("Sky")!;
        using var db = NewInMemoryContext();
        db.ChartPopItems.AddRange(
            Row(1, "Sky", "Retired Boss", "Old item", 1),
            Row(2, "Sky", sky.Bosses[0].Name, "Current item", 1));
        await db.SaveChangesAsync();

        var holdings = await new ChartBoardService(db).LoadHoldingsAsync(LinkshellId, CancellationToken.None);

        Assert.Equal(new[] { 2, 1 }, holdings.Select(h => h.Id));
    }
}
