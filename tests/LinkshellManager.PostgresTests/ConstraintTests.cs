using System;
using System.Threading.Tasks;
using LinkshellManagerDiscordApp.Models;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Xunit;

namespace LinkshellManager.PostgresTests;

// Database rules the in-memory provider silently ignores. If these pass, the fixture is enforcing
// the real constraints, so later race and delete tests can trust it.
[Collection("postgres")]
public class ConstraintTests : IAsyncLifetime
{
    private readonly PostgresFixture _pg;

    public ConstraintTests(PostgresFixture pg) => _pg = pg;

    public Task InitializeAsync() => _pg.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    // One window credit per person per window. The posts check before inserting, but two posts at
    // the same moment both pass that check, so this unique index is what stops the second credit.
    [Fact]
    public async Task SecondCreditForTheSamePersonAndWindowIsRejected()
    {
        int windowId;
        await using (var db = _pg.CreateContext())
        {
            var window = new EventAttendanceWindow { SequenceNumber = 1, PostedAt = DateTime.UtcNow };
            db.EventAttendanceWindows.Add(window);
            db.AppUserEventWindows.Add(new AppUserEventWindow
            {
                EventAttendanceWindow = window,
                AppUserId = "user-1",
                VerifiedAt = DateTime.UtcNow,
            });
            await db.SaveChangesAsync();
            windowId = window.Id;
        }

        // A separate context, like a second request that never saw the first row.
        await using (var db = _pg.CreateContext())
        {
            db.AppUserEventWindows.Add(new AppUserEventWindow
            {
                EventAttendanceWindowId = windowId,
                AppUserId = "user-1",
                VerifiedAt = DateTime.UtcNow,
            });

            var ex = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
            var pg = Assert.IsType<PostgresException>(ex.InnerException);
            Assert.Equal(PostgresErrorCodes.UniqueViolation, pg.SqlState);
        }

        // A different person on the same window is still fine.
        await using (var db = _pg.CreateContext())
        {
            db.AppUserEventWindows.Add(new AppUserEventWindow
            {
                EventAttendanceWindowId = windowId,
                AppUserId = "user-2",
                VerifiedAt = DateTime.UtcNow,
            });
            await db.SaveChangesAsync();
            Assert.Equal(2, await db.AppUserEventWindows.CountAsync());
        }
    }

    // P1.13: closing an auction deletes the treasury Item once its stack runs out. Older auction
    // history rows that still point at it must let go (SET NULL), not block the delete.
    [Fact]
    public async Task DeletingAnItemClearsAuctionItemSourceItemId()
    {
        int auctionItemId;
        int itemId;
        await using (var db = _pg.CreateContext())
        {
            var linkshell = new Linkshell { LinkshellName = "LS" };
            db.Linkshells.Add(linkshell);
            var item = new Item { Linkshell = linkshell, ItemName = "Ridill", Quantity = 1 };
            db.Items.Add(item);
            var auctionItem = new AuctionItem { ItemName = "Ridill", SourceItem = item };
            db.AuctionItems.Add(auctionItem);
            await db.SaveChangesAsync();
            auctionItemId = auctionItem.Id;
            itemId = item.Id;
        }

        // Delete with raw SQL so EF cannot null the column itself (it would if the AuctionItem were
        // loaded): only the database's ON DELETE rule is being tested here.
        await using (var db = _pg.CreateContext())
        {
            var deleted = await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM \"Items\" WHERE \"Id\" = {itemId}");
            Assert.Equal(1, deleted);
        }

        await using (var db = _pg.CreateContext())
        {
            var row = await db.AuctionItems.AsNoTracking().SingleAsync(a => a.Id == auctionItemId);
            Assert.Null(row.SourceItemId);
            Assert.Equal("Ridill", row.ItemName);
        }
    }
}
