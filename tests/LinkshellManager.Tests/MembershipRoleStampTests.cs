using LinkshellManagerDiscordApp.Data;
using LinkshellManagerDiscordApp.Models;
using LinkshellManagerDiscordApp.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LinkshellManager.Tests;

// Every save keeps AppUserLinkshell.LinkshellRoleId in step with Rank (ApplicationDbContext,
// AddMembershipRoleId): the role in the member's own linkshell whose Name equals the trimmed Rank,
// case mattering, else null. Permissions still read Rank, so this only has to be right, not used.
public class MembershipRoleStampTests
{
    private static readonly AppUser Actor = new() { Id = "admin-1", UserName = "millhouse", IsSuperAdmin = true };

    private static ApplicationDbContext NewInMemoryContext(string name) =>
        new(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(name)
            .Options);

    // Linkshell 1 has Officer, Member and a custom "Raider"; linkshell 2 has its own Officer.
    private static async Task<string> SeededAsync()
    {
        var name = Guid.NewGuid().ToString();
        await using var db = NewInMemoryContext(name);
        db.Linkshells.AddRange(
            new Linkshell { Id = 1, LinkshellName = "Kraken LS" },
            new Linkshell { Id = 2, LinkshellName = "Titans" });
        db.LinkshellRoles.AddRange(
            new LinkshellRole { Id = 10, LinkshellId = 1, Name = LinkshellRanks.Officer },
            new LinkshellRole { Id = 11, LinkshellId = 1, Name = LinkshellRanks.Member },
            new LinkshellRole { Id = 12, LinkshellId = 1, Name = "Raider" },
            new LinkshellRole { Id = 20, LinkshellId = 2, Name = LinkshellRanks.Officer });
        await db.SaveChangesAsync();
        return name;
    }

    [Theory]
    [InlineData("Officer", 10)]
    [InlineData("  Officer\t", 10)]
    [InlineData("Raider", 12)]
    [InlineData("officer", null)]    // capitals only: today's lookup misses it too
    [InlineData("Auctionner", null)] // names no role
    [InlineData("", null)]
    [InlineData("   ", null)]
    [InlineData(null, null)]
    public async Task NewMember_IsLinkedToTheRoleItsRankNames(string? rank, int? expected)
    {
        var name = await SeededAsync();
        await using (var db = NewInMemoryContext(name))
        {
            db.AppUserLinkshells.Add(new AppUserLinkshell { Id = 1, LinkshellId = 1, Rank = rank });
            await db.SaveChangesAsync();
        }

        await using var check = NewInMemoryContext(name);
        Assert.Equal(expected, (await check.AppUserLinkshells.SingleAsync()).LinkshellRoleId);
    }

    [Fact]
    public async Task NewMember_NeverGetsAnotherLinkshellsRole_EvenOnTheSynchronousSave()
    {
        var name = await SeededAsync();
        using (var db = NewInMemoryContext(name))
        {
            db.AppUserLinkshells.AddRange(
                new AppUserLinkshell { Id = 1, LinkshellId = 2, Rank = LinkshellRanks.Officer },
                new AppUserLinkshell { Id = 2, LinkshellId = 2, Rank = "Raider" });
            db.SaveChanges();
        }

        using var check = NewInMemoryContext(name);
        Assert.Equal(20, check.AppUserLinkshells.Single(m => m.Id == 1).LinkshellRoleId);
        Assert.Null(check.AppUserLinkshells.Single(m => m.Id == 2).LinkshellRoleId);
    }

    [Fact]
    public async Task RankChange_MovesTheLink_AndAnUnrelatedChangeLeavesItAlone()
    {
        var name = await SeededAsync();
        await using (var db = NewInMemoryContext(name))
        {
            db.AppUserLinkshells.Add(new AppUserLinkshell { Id = 1, LinkshellId = 1, Rank = LinkshellRanks.Member });
            await db.SaveChangesAsync();
        }

        await using (var db = NewInMemoryContext(name))
        {
            var member = await db.AppUserLinkshells.SingleAsync();
            Assert.Equal(11, member.LinkshellRoleId);
            member.Rank = "Raider";
            await db.SaveChangesAsync();
            Assert.Equal(12, member.LinkshellRoleId);

            member.Rank = "Gone";
            await db.SaveChangesAsync();
            Assert.Null(member.LinkshellRoleId);
        }

        // An id set on purpose in a save that does not touch Rank is kept, and a later save that
        // still does not touch Rank leaves it alone too (no extra lookup on every member save).
        await using (var db = NewInMemoryContext(name))
        {
            var member = await db.AppUserLinkshells.SingleAsync();
            member.LinkshellRoleId = 10;
            await db.SaveChangesAsync();
            member.Status = "Active";
            await db.SaveChangesAsync();
            Assert.Equal(10, member.LinkshellRoleId);
        }
    }

    // Data Admin writes through the same SaveChanges, so editing Rank there moves the link too,
    // and the id itself is shown but never edited.
    [Fact]
    public async Task DataAdminRankEdit_MovesTheLink()
    {
        var name = await SeededAsync();
        await using (var db = NewInMemoryContext(name))
        {
            db.AppUserLinkshells.Add(new AppUserLinkshell { Id = 1, LinkshellId = 1, Rank = LinkshellRanks.Member });
            await db.SaveChangesAsync();
        }

        await using (var db = NewInMemoryContext(name))
        {
            var catalog = new DataAdminCatalog(db.Model, DataAdminPolicy.Default);
            var editor = new DataAdminEditor(db, new GlobalSettingsService(db, new MemoryCache(new MemoryCacheOptions())), NullLogger<DataAdminEditor>.Instance);
            var memberships = catalog.FindByClrType(typeof(AppUserLinkshell))!;
            Assert.DoesNotContain(memberships.EditableColumns, c => c.Name == nameof(AppUserLinkshell.LinkshellRoleId));
            Assert.Contains(memberships.DetailColumns, c => c.Name == nameof(AppUserLinkshell.LinkshellRoleId));

            var result = await editor.UpdateAsync(
                memberships,
                1,
                new Dictionary<string, string?> { [nameof(AppUserLinkshell.Rank)] = LinkshellRanks.Officer, [nameof(AppUserLinkshell.LinkshellRoleId)] = "12" },
                Actor,
                CancellationToken.None);
            Assert.True(result.Succeeded, string.Join("; ", result.Errors.Select(e => e.Message)));
        }

        await using var check = NewInMemoryContext(name);
        Assert.Equal(10, (await check.AppUserLinkshells.SingleAsync()).LinkshellRoleId);
    }
}
