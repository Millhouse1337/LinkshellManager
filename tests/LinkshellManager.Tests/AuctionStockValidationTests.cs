using System;
using System.IO;
using System.Threading.Tasks;
using LinkshellManagerDiscordApp.Controllers;
using LinkshellManagerDiscordApp.Data;
using LinkshellManagerDiscordApp.Models;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace LinkshellManager.Tests;

// What an auction may sell. Close pays gil items out of the treasury and draws a linked treasury
// item's stock down, so both create AND edit must check that:
//   * the gil being auctioned is gil the treasury actually holds, and
//   * every linked treasury item belongs to the auction's own linkshell.
// Edit used to skip both, so an officer could create a small gil auction and then raise it, or
// point an item at another linkshell's inventory.
public class AuctionStockValidationTests
{
    private static ApplicationDbContext NewDb() =>
        new(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static ActivityAuctionItemInput ItemRow(int? sourceItemId = null, long? gil = null) =>
        new(Id: 0, ItemName: gil.HasValue ? null : "Ridill", ItemType: null, StartingBidDkp: 0,
            Notes: null, SourceItemId: sourceItemId, GilAmount: gil);

    private static Item TreasuryItem(int id, int linkshellId) => new()
    {
        Id = id,
        LinkshellId = linkshellId,
        ItemName = "Ridill",
        Quantity = 1
    };

    [Fact]
    public void Gil_WithinTheTreasury_IsAllowed()
    {
        Assert.Null(ActivityLootController.DescribeAuctionGilShortfall(requestedGil: 50_000, availableGil: 50_000));
    }

    [Fact]
    public void Gil_MoreThanTheTreasury_IsRefused()
    {
        var error = ActivityLootController.DescribeAuctionGilShortfall(requestedGil: 60_000, availableGil: 50_000);
        Assert.NotNull(error);
        Assert.Contains("Not enough gil", error);
    }

    [Fact]
    public void Gil_FromAnEmptyTreasury_IsRefused()
    {
        Assert.NotNull(ActivityLootController.DescribeAuctionGilShortfall(requestedGil: 1, availableGil: 0));
    }

    [Fact]
    public void NoGilItems_NeedNoTreasury()
    {
        Assert.Null(ActivityLootController.DescribeAuctionGilShortfall(requestedGil: 0, availableGil: 0));
        Assert.Equal(0, ActivityLootController.SumAuctionGil(new[] { ItemRow() }));
    }

    [Fact]
    public void SumAuctionGil_AddsEveryGilRow()
    {
        Assert.Equal(30_000, ActivityLootController.SumAuctionGil(new[]
        {
            ItemRow(gil: 10_000),
            ItemRow(gil: 20_000),
            ItemRow()
        }));
    }

    [Fact]
    public async Task SourceItem_FromTheSameLinkshell_IsAllowed()
    {
        await using var db = NewDb();
        db.Items.Add(TreasuryItem(id: 5, linkshellId: 1));
        await db.SaveChangesAsync();

        var error = await ActivityLootController.ValidateAuctionSourceItemsAsync(
            db, linkshellId: 1, new[] { ItemRow(sourceItemId: 5) }, default);

        Assert.Null(error);
    }

    // Close would draw down the OTHER linkshell's stock.
    [Fact]
    public async Task SourceItem_FromAnotherLinkshell_IsRefused()
    {
        await using var db = NewDb();
        db.Items.Add(TreasuryItem(id: 5, linkshellId: 2));
        await db.SaveChangesAsync();

        var error = await ActivityLootController.ValidateAuctionSourceItemsAsync(
            db, linkshellId: 1, new[] { ItemRow(sourceItemId: 5) }, default);

        Assert.NotNull(error);
    }

    [Fact]
    public async Task SourceItem_ThatDoesNotExist_IsRefused()
    {
        await using var db = NewDb();

        var error = await ActivityLootController.ValidateAuctionSourceItemsAsync(
            db, linkshellId: 1, new[] { ItemRow(sourceItemId: 99) }, default);

        Assert.NotNull(error);
    }

    // The same item listed twice is still one item of this linkshell, not a mismatch.
    [Fact]
    public async Task SourceItem_ListedTwice_IsStillAllowed()
    {
        await using var db = NewDb();
        db.Items.Add(TreasuryItem(id: 5, linkshellId: 1));
        await db.SaveChangesAsync();

        var error = await ActivityLootController.ValidateAuctionSourceItemsAsync(
            db, linkshellId: 1, new[] { ItemRow(sourceItemId: 5), ItemRow(sourceItemId: 5) }, default);

        Assert.Null(error);
    }

    // A gil row's SourceItemId is thrown away on save, so it isn't checked either.
    [Fact]
    public async Task GilRow_IgnoresItsSourceItemId()
    {
        await using var db = NewDb();

        var error = await ActivityLootController.ValidateAuctionSourceItemsAsync(
            db, linkshellId: 1, new[] { ItemRow(sourceItemId: 99, gil: 1_000) }, default);

        Assert.Null(error);
    }

    // The controller can't be built in a unit test, so pin the shape: both Create and Update run
    // both checks before they save. Precedent: AuctionPermissionTests.
    [Theory]
    [InlineData("CreateAuctionAsync(", "_dbContext.Auctions.Add(")]
    [InlineData("UpdateAuctionAsync(", "_dbContext.SaveChangesAsync(")]
    public void CreateAndUpdate_RunBothStockChecks(string start, string end)
    {
        var text = File.ReadAllText(Path.Combine(FindRepoPath("Controllers"), "Activity", "Loot", "ActivityLootController.Auctions.cs"));

        var startIndex = text.IndexOf(start, StringComparison.Ordinal);
        var endIndex = text.IndexOf(end, startIndex, StringComparison.Ordinal);
        Assert.True(startIndex >= 0 && endIndex > startIndex, $"{start} not found.");
        var body = text[startIndex..endIndex];

        Assert.Contains("ValidateAuctionGilAsync(", body);
        Assert.Contains("ValidateAuctionSourceItemsAsync(", body);
    }

    /// <summary>Walks up from the test binary to the repo root, which holds the app's Controllers.</summary>
    private static string FindRepoPath(string relative)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, relative);
            if (Directory.Exists(candidate))
            {
                return candidate;
            }
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException($"Could not locate '{relative}' above {AppContext.BaseDirectory}.");
    }
}
