using LinkshellManagerDiscordApp.Data;
using LinkshellManagerDiscordApp.Models;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace LinkshellManager.Tests;

// ChartMemberKeyItem.AppUserId comes from the CALLER (ChartKeyItemService, UserMergeService, Data
// Admin). Nothing stamps it on save any more (Round 4 deploy 3 removed the old hook, and deploy 4
// dropped the membership column it read). So a save keeps whatever account the caller set, and a
// tick with no account is refused (the column is required since RosterSwitch).
public class KeyItemAccountStampTests
{
    private static ApplicationDbContext NewInMemoryContext(string name) =>
        new(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(name)
            .Options);

    private static ChartMemberKeyItem Tick(int id, int linkshellId, string? appUserId = null) => new()
    {
        Id = id, LinkshellId = linkshellId, Board = "Dynamis", KeyItemName = "K" + id,
        AppUserId = appUserId,
    };

    [Fact]
    public async Task NewTick_KeepsTheCallersAccount()
    {
        var name = Guid.NewGuid().ToString();
        await using (var db = NewInMemoryContext(name))
        {
            db.ChartMemberKeyItems.Add(Tick(1, 1, appUserId: "typed-by-hand"));
            await db.SaveChangesAsync();
        }

        await using var check = NewInMemoryContext(name);
        Assert.Equal("typed-by-hand", (await check.ChartMemberKeyItems.SingleAsync()).AppUserId);
    }

    // The account is required, and nothing fills it in, so a tick without one is refused.
    [Fact]
    public async Task NewTick_WithNoAccount_IsRefused()
    {
        await using var db = NewInMemoryContext(Guid.NewGuid().ToString());
        db.ChartMemberKeyItems.Add(Tick(1, 1));

        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task SynchronousSave_KeepsTheCallersAccountToo()
    {
        var name = Guid.NewGuid().ToString();
        using (var db = NewInMemoryContext(name))
        {
            db.ChartMemberKeyItems.AddRange(Tick(1, 1, appUserId: "x"), Tick(2, 2, appUserId: "y"));
            db.SaveChanges();
        }

        using var check = NewInMemoryContext(name);
        Assert.Equal("x", check.ChartMemberKeyItems.Single(k => k.Id == 1).AppUserId);
        Assert.Equal("y", check.ChartMemberKeyItems.Single(k => k.Id == 2).AppUserId);
    }

    // The account set by the caller (what a placeholder claim does) is what is saved on a change.
    [Fact]
    public async Task ChangingTheAccount_SavesTheCallersValue()
    {
        var name = Guid.NewGuid().ToString();
        await using (var db = NewInMemoryContext(name))
        {
            db.ChartMemberKeyItems.Add(Tick(1, 1, appUserId: "a-1"));
            await db.SaveChangesAsync();
        }

        await using (var db = NewInMemoryContext(name))
        {
            (await db.ChartMemberKeyItems.SingleAsync()).AppUserId = "moved-by-merge";
            await db.SaveChangesAsync();
        }
        await using var check = NewInMemoryContext(name);
        Assert.Equal("moved-by-merge", (await check.ChartMemberKeyItems.SingleAsync()).AppUserId);
    }
}
