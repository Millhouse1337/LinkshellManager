using System;
using System.Linq;
using System.Threading.Tasks;
using LinkshellManagerDiscordApp.Data;
using LinkshellManagerDiscordApp.Models;
using LinkshellManagerDiscordApp.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Xunit;
using RevokeResult = LinkshellManagerDiscordApp.Services.AddonApiAuthService.RevokeResult;

namespace LinkshellManager.Tests;

// WHO MAY DISCONNECT WHOSE ADDON.
//
// Revoking someone else's addon pairing used to need only "Use the addon", which every default
// role has -- so any member could loop the revoke endpoint over token ids and knock every
// officer's addon offline mid-camp, in other linkshells too (one pairing's batch siblings went
// with it). Now: your own pairing always; anyone else's needs "Manage members" on that token's
// linkshell, and their siblings go only where the caller holds that right as well.
public class AddonTokenRevokeTests
{
    private const string Me = "user-me";
    private const string Them = "user-them";
    private const int HomeLinkshellId = 5;
    private const int OtherLinkshellId = 6;

    private static ApplicationDbContext NewDb() =>
        new(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static AddonApiAuthService NewService(ApplicationDbContext db)
    {
        var cache = new MemoryCache(new MemoryCacheOptions());
        return new AddonApiAuthService(db, new GlobalSettingsService(db, cache), cache);
    }

    // Both linkshells get the default Leader / Officer / Member roles: only the Leader role has
    // "Manage members" by default, while every role may use the addon.
    private static async Task<ApplicationDbContext> SeededAsync()
    {
        var db = NewDb();
        db.Linkshells.Add(new Linkshell { Id = HomeLinkshellId, LinkshellName = "Home" });
        db.Linkshells.Add(new Linkshell { Id = OtherLinkshellId, LinkshellName = "Other" });
        db.LinkshellRoles.AddRange(LinkshellRoleDefaults.BuildDefaultRoles(HomeLinkshellId));
        db.LinkshellRoles.AddRange(LinkshellRoleDefaults.BuildDefaultRoles(OtherLinkshellId));
        await db.SaveChangesAsync();
        return db;
    }

    private static async Task JoinAsync(ApplicationDbContext db, string appUserId, int linkshellId, string rank)
    {
        db.AppUserLinkshells.Add(new AppUserLinkshell { AppUserId = appUserId, LinkshellId = linkshellId, Rank = rank });
        await db.SaveChangesAsync();
    }

    private static async Task AddTokenAsync(
        ApplicationDbContext db, int id, string issuedTo, int linkshellId, Guid? batchId = null)
    {
        db.AddonApiTokens.Add(new AddonApiToken
        {
            Id = id,
            LinkshellId = linkshellId,
            IssuedToAppUserId = issuedTo,
            TokenHash = $"hash-{id}",
            TokenPrefix = $"att_{id}",
            PairingBatchId = batchId,
            CreatedAt = DateTime.UtcNow.AddDays(-1),
        });
        await db.SaveChangesAsync();
    }

    private static async Task<int[]> RevokedIdsAsync(ApplicationDbContext db) =>
        await db.AddonApiTokens.AsNoTracking()
            .Where(t => t.RevokedAt != null)
            .Select(t => t.Id)
            .OrderBy(id => id)
            .ToArrayAsync();

    [Fact]
    public async Task OwnToken_IsRevoked_WithEverySibling_EvenAsAPlainMember()
    {
        await using var db = await SeededAsync();
        await JoinAsync(db, Me, HomeLinkshellId, LinkshellRanks.Member);
        await JoinAsync(db, Me, OtherLinkshellId, LinkshellRanks.Member);
        var batch = Guid.NewGuid();
        await AddTokenAsync(db, 1, Me, HomeLinkshellId, batch);
        await AddTokenAsync(db, 2, Me, OtherLinkshellId, batch);

        var result = await NewService(db).RevokeAsync(1, Me, callerHasAdminOverride: false);

        Assert.Equal(RevokeResult.Revoked, result);
        Assert.Equal(new[] { 1, 2 }, await RevokedIdsAsync(db));
    }

    [Fact]
    public async Task SomeoneElsesToken_AsAMemberWhoMayUseTheAddon_IsForbidden_AndNothingIsRevoked()
    {
        await using var db = await SeededAsync();
        await JoinAsync(db, Me, HomeLinkshellId, LinkshellRanks.Member);
        await JoinAsync(db, Them, HomeLinkshellId, LinkshellRanks.Officer);
        await AddTokenAsync(db, 1, Them, HomeLinkshellId);

        var result = await NewService(db).RevokeAsync(1, Me, callerHasAdminOverride: false);

        Assert.Equal(RevokeResult.Forbidden, result);
        Assert.Empty(await RevokedIdsAsync(db));
    }

    [Fact]
    public async Task SomeoneElsesToken_AsAnOfficerWithoutManageMembers_IsForbidden()
    {
        await using var db = await SeededAsync();
        await JoinAsync(db, Me, HomeLinkshellId, LinkshellRanks.Officer);
        await JoinAsync(db, Them, HomeLinkshellId, LinkshellRanks.Member);
        await AddTokenAsync(db, 1, Them, HomeLinkshellId);

        var result = await NewService(db).RevokeAsync(1, Me, callerHasAdminOverride: false);

        Assert.Equal(RevokeResult.Forbidden, result);
        Assert.Empty(await RevokedIdsAsync(db));
    }

    [Fact]
    public async Task SomeoneElsesToken_WithARoleThatMayManageMembers_IsRevoked()
    {
        await using var db = await SeededAsync();
        var officer = await db.LinkshellRoles.SingleAsync(
            r => r.LinkshellId == HomeLinkshellId && r.Name == LinkshellRanks.Officer);
        officer.CanManageMembers = true;
        await db.SaveChangesAsync();
        await JoinAsync(db, Me, HomeLinkshellId, LinkshellRanks.Officer);
        await JoinAsync(db, Them, HomeLinkshellId, LinkshellRanks.Member);
        await AddTokenAsync(db, 1, Them, HomeLinkshellId);

        var result = await NewService(db).RevokeAsync(1, Me, callerHasAdminOverride: false);

        Assert.Equal(RevokeResult.Revoked, result);
        Assert.Equal(new[] { 1 }, await RevokedIdsAsync(db));
    }

    [Fact]
    public async Task Leader_RevokesSomeoneElsesToken_OnlyInTheirOwnLinkshell_NotItsSiblingElsewhere()
    {
        await using var db = await SeededAsync();
        await JoinAsync(db, Me, HomeLinkshellId, LinkshellRanks.Leader);
        await JoinAsync(db, Them, HomeLinkshellId, LinkshellRanks.Member);
        await JoinAsync(db, Them, OtherLinkshellId, LinkshellRanks.Member);
        var batch = Guid.NewGuid();
        await AddTokenAsync(db, 1, Them, HomeLinkshellId, batch);
        await AddTokenAsync(db, 2, Them, OtherLinkshellId, batch);

        var result = await NewService(db).RevokeAsync(1, Me, callerHasAdminOverride: false);

        // Token 2 belongs to a linkshell the leader is not even in, so it keeps working.
        Assert.Equal(RevokeResult.Revoked, result);
        Assert.Equal(new[] { 1 }, await RevokedIdsAsync(db));
    }

    [Fact]
    public async Task Leader_InBothLinkshells_RevokesTheWholeBatch()
    {
        await using var db = await SeededAsync();
        await JoinAsync(db, Me, HomeLinkshellId, LinkshellRanks.Leader);
        await JoinAsync(db, Me, OtherLinkshellId, LinkshellRanks.Leader);
        var batch = Guid.NewGuid();
        await AddTokenAsync(db, 1, Them, HomeLinkshellId, batch);
        await AddTokenAsync(db, 2, Them, OtherLinkshellId, batch);

        var result = await NewService(db).RevokeAsync(1, Me, callerHasAdminOverride: false);

        Assert.Equal(RevokeResult.Revoked, result);
        Assert.Equal(new[] { 1, 2 }, await RevokedIdsAsync(db));
    }

    [Fact]
    public async Task AdminOverride_LiftsAMember_ButOnlyInsideALinkshellTheyJoined()
    {
        await using var db = await SeededAsync();
        await JoinAsync(db, Me, HomeLinkshellId, LinkshellRanks.Member);
        await AddTokenAsync(db, 1, Them, HomeLinkshellId);
        await AddTokenAsync(db, 2, Them, OtherLinkshellId);
        var service = NewService(db);

        Assert.Equal(RevokeResult.Revoked, await service.RevokeAsync(1, Me, callerHasAdminOverride: true));
        // Not a member of the Other linkshell: the override never reaches past membership.
        Assert.Equal(RevokeResult.Forbidden, await service.RevokeAsync(2, Me, callerHasAdminOverride: true));
        Assert.Equal(new[] { 1 }, await RevokedIdsAsync(db));
    }

    [Fact]
    public async Task UnknownToken_IsNotFound()
    {
        await using var db = await SeededAsync();
        await JoinAsync(db, Me, HomeLinkshellId, LinkshellRanks.Leader);

        Assert.Equal(RevokeResult.NotFound, await NewService(db).RevokeAsync(99, Me, callerHasAdminOverride: false));
    }
}
