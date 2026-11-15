using LinkshellManagerDiscordApp.Data;
using LinkshellManagerDiscordApp.Models;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace LinkshellManager.Tests;

// Every save keeps ChartMemberKeyItem.AppUserId in step with MembershipId (ApplicationDbContext,
// AccountJobsAndKeyItemAccounts): the account of that membership when it is in the tick's own
// linkshell, else null -- the same rule as the migration's fill. The grid still reads MembershipId,
// so this only has to be right, not used.
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

    private static ChartMemberKeyItem Tick(int id, int linkshellId, int membershipId, string? appUserId = null) => new()
    {
        Id = id, LinkshellId = linkshellId, Board = "Dynamis", KeyItemName = "K" + id,
        MembershipId = membershipId, AppUserId = appUserId,
    };

    [Theory]
    [InlineData(1, 1, "a-1")]   // a member's own tick
    [InlineData(1, 2, null)]    // no account (sheet-only row)
    [InlineData(1, 3, null)]    // membership of ANOTHER linkshell: never borrows its account
    [InlineData(1, 99, null)]   // membership gone
    public async Task NewTick_GetsItsMembershipsAccount(int linkshellId, int membershipId, string? expected)
    {
        var name = await SeededAsync();
        await using (var db = NewInMemoryContext(name))
        {
            // A wrong value set by hand is replaced: the membership decides.
            db.ChartMemberKeyItems.Add(Tick(1, linkshellId, membershipId, appUserId: "typed-by-hand"));
            await db.SaveChangesAsync();
        }

        await using var check = NewInMemoryContext(name);
        Assert.Equal(expected, (await check.ChartMemberKeyItems.SingleAsync()).AppUserId);
    }

    [Fact]
    public async Task SynchronousSave_StampsToo()
    {
        var name = await SeededAsync();
        using (var db = NewInMemoryContext(name))
        {
            db.ChartMemberKeyItems.AddRange(Tick(1, 1, 1), Tick(2, 2, 3), Tick(3, 2, 1));
            db.SaveChanges();
        }

        using var check = NewInMemoryContext(name);
        Assert.Equal("a-1", check.ChartMemberKeyItems.Single(k => k.Id == 1).AppUserId);
        Assert.Equal("b-2", check.ChartMemberKeyItems.Single(k => k.Id == 2).AppUserId);
        Assert.Null(check.ChartMemberKeyItems.Single(k => k.Id == 3).AppUserId);
    }

    [Fact]
    public async Task MovingATickToAnotherMembership_MovesItsAccount_AndAnUnrelatedChangeLeavesItAlone()
    {
        var name = await SeededAsync();
        await using (var db = NewInMemoryContext(name))
        {
            db.ChartMemberKeyItems.Add(Tick(1, 1, 1));
            await db.SaveChangesAsync();
        }

        // Unrelated change: the account stays, even though the membership has since lost it.
        await using (var db = NewInMemoryContext(name))
        {
            (await db.AppUserLinkshells.SingleAsync(m => m.Id == 1)).AppUserId = "someone-else";
            await db.SaveChangesAsync();
        }
        await using (var db = NewInMemoryContext(name))
        {
            (await db.ChartMemberKeyItems.SingleAsync()).SetByCharacterName = "Officer";
            await db.SaveChangesAsync();
        }
        await using (var check = NewInMemoryContext(name))
        {
            Assert.Equal("a-1", (await check.ChartMemberKeyItems.SingleAsync()).AppUserId);
        }

        // MembershipId changes (what a placeholder claim does): re-stamped from the new membership.
        await using (var db = NewInMemoryContext(name))
        {
            (await db.ChartMemberKeyItems.SingleAsync()).MembershipId = 2;
            await db.SaveChangesAsync();
        }
        await using (var check = NewInMemoryContext(name))
        {
            Assert.Null((await check.ChartMemberKeyItems.SingleAsync()).AppUserId);
        }

        // Account set in the same save as the move: the caller's value is kept.
        await using (var db = NewInMemoryContext(name))
        {
            var tick = await db.ChartMemberKeyItems.SingleAsync();
            tick.MembershipId = 1;
            tick.AppUserId = "moved-by-merge";
            await db.SaveChangesAsync();
        }
        await using (var check = NewInMemoryContext(name))
        {
            Assert.Equal("moved-by-merge", (await check.ChartMemberKeyItems.SingleAsync()).AppUserId);
        }
    }
}
