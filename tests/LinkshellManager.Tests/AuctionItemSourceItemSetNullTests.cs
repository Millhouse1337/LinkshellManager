using LinkshellManagerDiscordApp.Data;
using LinkshellManagerDiscordApp.Models;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace LinkshellManager.Tests;

// Closing an auction deletes the treasury Item once its stack runs out, but closed-auction history
// rows keep SourceItemId. With no delete rule Postgres refused that DELETE whenever an older history
// row (one EF had not loaded) still pointed at the Item, and the whole close rolled back.
//
// The in-memory provider does not enforce foreign keys, so the real proof is the model's delete
// rule (which is what the migration turns into ON DELETE SET NULL). The second test pins the
// visible outcome: the Item goes, the history row stays with its name.
public class AuctionItemSourceItemSetNullTests
{
    private static ApplicationDbContext NewDb(string name) =>
        new(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(name)
            .Options);

    [Fact]
    public void SourceItemForeignKey_IsSetNull()
    {
        using var db = NewDb(Guid.NewGuid().ToString());

        var fk = db.Model.FindEntityType(typeof(AuctionItem))!
            .GetForeignKeys()
            .Single(key => key.Properties.Single().Name == nameof(AuctionItem.SourceItemId));

        Assert.Equal(typeof(Item), fk.PrincipalEntityType.ClrType);
        Assert.Equal(DeleteBehavior.SetNull, fk.DeleteBehavior);
    }

    [Fact]
    public async Task DeletingTheItem_KeepsTheHistoryRow_WithItsName()
    {
        var name = Guid.NewGuid().ToString();
        int itemId;
        int historyItemId;
        await using (var seed = NewDb(name))
        {
            var item = new Item { LinkshellId = 1, ItemName = "Ridill", Quantity = 1 };
            seed.Items.Add(item);
            await seed.SaveChangesAsync();

            var historyItem = new AuctionItem
            {
                ItemName = "Ridill",
                Status = "NoBids",
                SourceItemId = item.Id
            };
            seed.AuctionItems.Add(historyItem);
            await seed.SaveChangesAsync();
            itemId = item.Id;
            historyItemId = historyItem.Id;
        }

        await using (var db = NewDb(name))
        {
            db.AuctionItems.Load();
            db.Items.Remove(await db.Items.SingleAsync(i => i.Id == itemId));
            await db.SaveChangesAsync();
        }

        await using (var check = NewDb(name))
        {
            Assert.False(await check.Items.AnyAsync(i => i.Id == itemId));
            var history = await check.AuctionItems.SingleAsync(i => i.Id == historyItemId);
            Assert.Null(history.SourceItemId);
            Assert.Equal("Ridill", history.ItemName);
        }
    }
}
