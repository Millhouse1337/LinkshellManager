using LinkshellManagerDiscordApp.Data;
using LinkshellManagerDiscordApp.Models;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace LinkshellManager.Tests;

// ChartMemberKeyItem.AppUserId comes from the CALLER (ChartKeyItemService, UserMergeService, Data
// Admin). Until Round 4 deploy 3 every save re-stamped it from MembershipId; that hook is gone,
// because MembershipId is no longer written and the next deploy drops it (ROUND4-DROP). So a save
// keeps whatever account the caller set, whatever MembershipId says, and a tick with no account is
// refused (the column is required since RosterSwitch).
public class KeyItemAccountStampTests
{
    private static ApplicationDbContext NewInMemoryContext(string name) =>
        new(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(name)
            .Options);

    // Linkshell 1: Aeris (account a-1) and a sheet-only row with no account. Linkshell 2: Bob (b-2).
    private static async Task<string> SeededAsync()
    {
        var name = Guid.NewGuid().ToString();
        await using var db = NewInMemoryContext(name);
        db.AppUserLinkshells.AddRange(
            new AppUserLinkshell { Id = 1, LinkshellId = 1, AppUserId = "a-1", CharacterName = "Aeris" },
            new AppUserLinkshell { Id = 2, LinkshellId = 1, AppUserId = null, CharacterName = "Sheet" },
            new AppUserLinkshell { Id = 3, LinkshellId = 2, AppUserId = "b-2", CharacterName = "Bob" });
        await db.SaveChangesAsync();
        return name;
    }

    private static ChartMemberKeyItem Tick(int id, int linkshellId, int? membershipId, string? appUserId = null) => new()
    {
        Id = id, LinkshellId = linkshellId, Board = "Dynamis", KeyItemName = "K" + id,
        MembershipId = membershipId, AppUserId = appUserId,
    };

    // Before: a tick on membership 1 was re-stamped to a-1. Now the caller's account always stays.
    [Theory]
    [InlineData(1, 1)]      // a member's own membership: no longer overrides the caller
    [InlineData(1, 2)]      // no account (sheet-only row)
    [InlineData(1, 3)]      // membership of ANOTHER linkshell
    [InlineData(1, 99)]     // membership gone
    [InlineData(1, null)]   // no membership at all (what every new tick is now)
    public async Task NewTick_KeepsTheCallersAccount(int linkshellId, int? membershipId)
    {
        var name = await SeededAsync();
        await using (var db = NewInMemoryContext(name))
        {
            db.ChartMemberKeyItems.Add(Tick(1, linkshellId, membershipId, appUserId: "typed-by-hand"));
            await db.SaveChangesAsync();
        }

        await using var check = NewInMemoryContext(name);
        Assert.Equal("typed-by-hand", (await check.ChartMemberKeyItems.SingleAsync()).AppUserId);
    }

    // The account is required, and nothing fills it in any more, so a tick without one is refused
    // even when its membership has an account (before, membership 1 would have given it a-1).
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(99)]
    [InlineData(null)]
    public async Task NewTick_WithNoAccount_IsRefused(int? membershipId)
    {
        var name = await SeededAsync();
        await using var db = NewInMemoryContext(name);
        db.ChartMemberKeyItems.Add(Tick(1, 1, membershipId));

        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task SynchronousSave_KeepsTheCallersAccountToo()
    {
        var name = await SeededAsync();
        using (var db = NewInMemoryContext(name))
        {
            db.ChartMemberKeyItems.AddRange(Tick(1, 1, 1, appUserId: "x"), Tick(2, 2, 3, appUserId: "y"));
            db.SaveChanges();
        }

        using var check = NewInMemoryContext(name);
        Assert.Equal("x", check.ChartMemberKeyItems.Single(k => k.Id == 1).AppUserId);
        Assert.Equal("y", check.ChartMemberKeyItems.Single(k => k.Id == 2).AppUserId);
    }

    // Data Admin can still change the old column on an existing row; the account no longer follows.
    [Fact]
    public async Task ChangingTheOldMembershipColumn_NeverMovesTheAccount()
    {
        var name = await SeededAsync();
        await using (var db = NewInMemoryContext(name))
        {
            db.ChartMemberKeyItems.Add(Tick(1, 1, null, appUserId: "a-1"));
            await db.SaveChangesAsync();
        }

        await using (var db = NewInMemoryContext(name))
        {
            (await db.ChartMemberKeyItems.SingleAsync()).MembershipId = 3;
            await db.SaveChangesAsync();
        }
        await using (var check = NewInMemoryContext(name))
        {
            Assert.Equal("a-1", (await check.ChartMemberKeyItems.SingleAsync()).AppUserId);
        }

        // The account set by the caller (what a placeholder claim does) is what is saved.
        await using (var db = NewInMemoryContext(name))
        {
            (await db.ChartMemberKeyItems.SingleAsync()).AppUserId = "moved-by-merge";
            await db.SaveChangesAsync();
        }
        await using (var check = NewInMemoryContext(name))
        {
            Assert.Equal("moved-by-merge", (await check.ChartMemberKeyItems.SingleAsync()).AppUserId);
        }
    }
}
