using System;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using LinkshellManagerDiscordApp.Data;
using LinkshellManagerDiscordApp.Models;
using LinkshellManagerDiscordApp.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Caching.Memory;
using Xunit;

namespace LinkshellManager.Tests;

// AN ADDON TOKEN IS ONLY AS GOOD AS ITS OWNER'S MEMBERSHIP.
//
// Tokens used to check only hash, revoke, kill switch and inactivity. A member who was kicked, who
// left, or whose role lost "Use the addon" kept reading ToDs, camps and the roster in game for as
// long as their addon kept polling -- and a polling addon refreshed LastUsedAt every minute, so the
// token never expired either. These pin the membership check, the 180-day lifetime, the revoke on
// kick/leave, and the pairing fan-out skipping linkshells where the role may not use the addon.
public class AddonTokenMembershipTests
{
    private const string Me = "user-me";
    private const string SomeoneElse = "user-them";
    private const int LinkshellId = 5;
    private const int OtherLinkshellId = 6;

    private static ApplicationDbContext NewDb() =>
        new(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            // Pairing redeem runs inside a transaction; the in-memory provider has none.
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options);

    private static AddonApiAuthService NewService(ApplicationDbContext db, IMemoryCache? cache = null)
    {
        cache ??= new MemoryCache(new MemoryCacheOptions());
        return new AddonApiAuthService(db, new GlobalSettingsService(db, cache), cache, TestPermissions.For(db));
    }

    // Same hashing the service stores, so a test can hand ValidateTokenAsync a raw token.
    private static string Hash(string raw) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw)));

    private static async Task<ApplicationDbContext> SeededAsync(
        string? rank = LinkshellRanks.Member,
        bool memberRoleCanUseAddon = true,
        bool seedRoles = true)
    {
        var db = NewDb();
        db.Linkshells.Add(new Linkshell { Id = LinkshellId, LinkshellName = "Home" });
        db.Linkshells.Add(new Linkshell { Id = OtherLinkshellId, LinkshellName = "Other" });
        if (seedRoles)
        {
            db.LinkshellRoles.Add(new LinkshellRole
            {
                LinkshellId = LinkshellId,
                Name = LinkshellRanks.Member,
                CanUseAddon = memberRoleCanUseAddon,
            });
        }
        if (rank is not null)
        {
            db.AppUserLinkshells.Add(new AppUserLinkshell
            {
                AppUserId = Me,
                LinkshellId = LinkshellId,
                Rank = rank,
            });
        }
        await db.SaveChangesAsync();
        return db;
    }

    private static async Task<string> AddTokenAsync(
        ApplicationDbContext db,
        int id,
        string? issuedTo = Me,
        int linkshellId = LinkshellId,
        DateTime? createdAt = null,
        DateTime? lastUsedAt = null)
    {
        var raw = $"att_test-token-{id}";
        db.AddonApiTokens.Add(new AddonApiToken
        {
            Id = id,
            LinkshellId = linkshellId,
            IssuedToAppUserId = issuedTo,
            TokenHash = Hash(raw),
            TokenPrefix = raw[..12],
            CreatedAt = createdAt ?? DateTime.UtcNow.AddDays(-1),
            LastUsedAt = lastUsedAt,
        });
        await db.SaveChangesAsync();
        return raw;
    }

    // ---- ValidateTokenAsync ----

    [Fact]
    public async Task MemberWhoseRoleMayUseTheAddon_IsAccepted()
    {
        await using var db = await SeededAsync();
        var raw = await AddTokenAsync(db, 1);

        Assert.NotNull(await NewService(db).ValidateTokenAsync(raw));
    }

    [Fact]
    public async Task IssuerNoLongerAMember_IsRejected()
    {
        await using var db = await SeededAsync(rank: null);
        var raw = await AddTokenAsync(db, 1);

        Assert.Null(await NewService(db).ValidateTokenAsync(raw));
    }

    [Fact]
    public async Task RoleWithoutUseTheAddon_IsRejected_ButNotRevoked()
    {
        await using var db = await SeededAsync(memberRoleCanUseAddon: false);
        var raw = await AddTokenAsync(db, 1);

        Assert.Null(await NewService(db).ValidateTokenAsync(raw));
        // Rejected, not revoked: turning the permission back on makes the same pairing work again.
        Assert.Null((await db.AddonApiTokens.SingleAsync()).RevokedAt);
    }

    [Fact]
    public async Task Leader_PassesEvenWhenTheRoleRowSaysNo()
    {
        await using var db = await SeededAsync(rank: LinkshellRanks.Leader, memberRoleCanUseAddon: false);
        var raw = await AddTokenAsync(db, 1);

        Assert.NotNull(await NewService(db).ValidateTokenAsync(raw));
    }

    [Fact]
    public async Task RankWithNoRoleRow_FallsBackToTheMemberRole()
    {
        await using var db = await SeededAsync(rank: "Recruit", memberRoleCanUseAddon: false);
        var raw = await AddTokenAsync(db, 1);

        Assert.Null(await NewService(db).ValidateTokenAsync(raw));
    }

    // A linkshell with no role rows at all gets the default roles seeded first (LinkshellPermissions,
    // exactly as the app does when anyone opens it), and every default role may use the addon.
    // This used to fall back to the old Leader/Officer rank test instead, so a Member was refused
    // here until the next time someone opened the app and the seeding made them welcome.
    [Fact]
    public async Task NoRolesSeeded_TheDefaultRolesDecide()
    {
        await using var officerDb = await SeededAsync(rank: LinkshellRanks.Officer, seedRoles: false);
        var officerRaw = await AddTokenAsync(officerDb, 1);
        Assert.NotNull(await NewService(officerDb).ValidateTokenAsync(officerRaw));

        await using var memberDb = await SeededAsync(rank: LinkshellRanks.Member, seedRoles: false);
        var memberRaw = await AddTokenAsync(memberDb, 1);
        Assert.NotNull(await NewService(memberDb).ValidateTokenAsync(memberRaw));
        Assert.True(await memberDb.LinkshellRoles.AnyAsync(r => r.LinkshellId == LinkshellId && r.Name == LinkshellRanks.Member));
    }

    [Fact]
    public async Task CustomRole_ItsOwnUseTheAddonFlagDecides()
    {
        await using var db = await SeededAsync(rank: "Scout", memberRoleCanUseAddon: false);
        db.LinkshellRoles.Add(new LinkshellRole { LinkshellId = LinkshellId, Name = "Scout", CanUseAddon = true });
        db.LinkshellRoles.Add(new LinkshellRole { LinkshellId = LinkshellId, Name = "Mule", CanUseAddon = false });
        db.AppUserLinkshells.Add(new AppUserLinkshell { AppUserId = SomeoneElse, LinkshellId = LinkshellId, Rank = "Mule" });
        await db.SaveChangesAsync();
        var scoutRaw = await AddTokenAsync(db, 1);
        var muleRaw = await AddTokenAsync(db, 2, issuedTo: SomeoneElse);

        var service = NewService(db);
        Assert.NotNull(await service.ValidateTokenAsync(scoutRaw));
        Assert.Null(await service.ValidateTokenAsync(muleRaw));
    }

    // New with LinkshellPermissions: the super-admin override counts here too, but only inside a
    // linkshell the super admin has joined.
    [Fact]
    public async Task AdminOverride_LetsAMemberSuperAdminUseTheAddon()
    {
        await using var db = await SeededAsync(memberRoleCanUseAddon: false);
        db.Users.Add(new AppUser { Id = Me, UserName = Me, IsSuperAdmin = true });
        await db.SaveChangesAsync();
        await new GlobalSettingsService(db, new MemoryCache(new MemoryCacheOptions()))
            .SetAdminOverrideEnabledAsync(true);
        var raw = await AddTokenAsync(db, 1);
        var notJoinedRaw = await AddTokenAsync(db, 2, linkshellId: OtherLinkshellId);

        var service = NewService(db);
        Assert.NotNull(await service.ValidateTokenAsync(raw));
        Assert.Null(await service.ValidateTokenAsync(notJoinedRaw));
    }

    [Fact]
    public async Task TokenWithNoIssuer_IsRejected()
    {
        await using var db = await SeededAsync();
        var raw = await AddTokenAsync(db, 1, issuedTo: null);

        Assert.Null(await NewService(db).ValidateTokenAsync(raw));
    }

    [Fact]
    public async Task TokenOlderThanItsLifetime_IsRevoked_EvenWhenUsedEveryMinute()
    {
        await using var db = await SeededAsync();
        var raw = await AddTokenAsync(
            db, 1,
            createdAt: DateTime.UtcNow.AddDays(-AddonApiAuthService.TokenAbsoluteLifetimeDays - 1),
            lastUsedAt: DateTime.UtcNow.AddMinutes(-1));

        Assert.Null(await NewService(db).ValidateTokenAsync(raw));
        Assert.NotNull((await db.AddonApiTokens.SingleAsync()).RevokedAt);
    }

    [Fact]
    public async Task RejectedToken_DoesNotRefreshLastUsedAt()
    {
        await using var db = await SeededAsync(rank: null);
        var raw = await AddTokenAsync(db, 1);

        await NewService(db).ValidateTokenAsync(raw);

        Assert.Null((await db.AddonApiTokens.SingleAsync()).LastUsedAt);
    }

    // The membership answer is cached for a minute, but a kick revokes the token outright and the
    // revoke is read before the cache -- so a kicked member is cut off at once, not a minute later.
    [Fact]
    public async Task RevokeOnKick_BeatsTheCachedYes()
    {
        await using var db = await SeededAsync();
        var raw = await AddTokenAsync(db, 1);
        var cache = new MemoryCache(new MemoryCacheOptions());
        var service = NewService(db, cache);
        Assert.NotNull(await service.ValidateTokenAsync(raw));

        db.AppUserLinkshells.Remove(await db.AppUserLinkshells.SingleAsync());
        await service.RevokeAllForMemberAsync(Me, LinkshellId);
        await db.SaveChangesAsync();

        Assert.Null(await service.ValidateTokenAsync(raw));
    }

    // ---- RevokeAllForMemberAsync ----

    [Fact]
    public async Task RevokeAllForMember_RevokesOnlyThatMembersTokensOnThatLinkshell()
    {
        await using var db = await SeededAsync();
        await AddTokenAsync(db, 1);                                      // mine, here
        await AddTokenAsync(db, 2);                                      // mine, here (second device)
        await AddTokenAsync(db, 3, linkshellId: OtherLinkshellId);       // mine, other linkshell
        await AddTokenAsync(db, 4, issuedTo: SomeoneElse);               // theirs, here

        var revoked = await NewService(db).RevokeAllForMemberAsync(Me, LinkshellId);
        await db.SaveChangesAsync();

        Assert.Equal(2, revoked);
        var stillLive = await db.AddonApiTokens
            .Where(t => t.RevokedAt == null).Select(t => t.Id).OrderBy(id => id).ToArrayAsync();
        Assert.Equal(new[] { 3, 4 }, stillLive);
    }

    [Fact]
    public async Task RevokeAllForMember_DropsTheirUnusedPairingCodesThere()
    {
        await using var db = await SeededAsync();
        db.AddonPairingCodes.AddRange(
            new AddonPairingCode { Code = "MINEHERE", LinkshellId = LinkshellId, IssuedToAppUserId = Me, CreatedAt = DateTime.UtcNow, ExpiresAt = DateTime.UtcNow.AddMinutes(10) },
            new AddonPairingCode { Code = "MINEUSED", LinkshellId = LinkshellId, IssuedToAppUserId = Me, CreatedAt = DateTime.UtcNow, ExpiresAt = DateTime.UtcNow.AddMinutes(10), ConsumedAt = DateTime.UtcNow },
            new AddonPairingCode { Code = "MINEELSE", LinkshellId = OtherLinkshellId, IssuedToAppUserId = Me, CreatedAt = DateTime.UtcNow, ExpiresAt = DateTime.UtcNow.AddMinutes(10) },
            new AddonPairingCode { Code = "THEIRS01", LinkshellId = LinkshellId, IssuedToAppUserId = SomeoneElse, CreatedAt = DateTime.UtcNow, ExpiresAt = DateTime.UtcNow.AddMinutes(10) });
        await db.SaveChangesAsync();

        await NewService(db).RevokeAllForMemberAsync(Me, LinkshellId);
        await db.SaveChangesAsync();

        var left = await db.AddonPairingCodes.Select(c => c.Code).OrderBy(c => c).ToArrayAsync();
        Assert.Equal(new[] { "MINEELSE", "MINEUSED", "THEIRS01" }, left);
    }

    // ---- Pairing fan-out ----

    [Fact]
    public async Task PairingFanOut_SkipsLinkshellsWhereTheRoleMayNotUseTheAddon()
    {
        await using var db = await SeededAsync();
        const int AllowedLinkshellId = 7;
        db.Linkshells.Add(new Linkshell { Id = AllowedLinkshellId, LinkshellName = "Allowed" });
        // Member of both other linkshells; only one lets Members use the addon.
        db.LinkshellRoles.Add(new LinkshellRole { LinkshellId = OtherLinkshellId, Name = LinkshellRanks.Member, CanUseAddon = false });
        db.LinkshellRoles.Add(new LinkshellRole { LinkshellId = AllowedLinkshellId, Name = LinkshellRanks.Member, CanUseAddon = true });
        db.AppUserLinkshells.Add(new AppUserLinkshell { AppUserId = Me, LinkshellId = OtherLinkshellId, Rank = LinkshellRanks.Member });
        db.AppUserLinkshells.Add(new AppUserLinkshell { AppUserId = Me, LinkshellId = AllowedLinkshellId, Rank = LinkshellRanks.Member });
        await db.SaveChangesAsync();

        var service = NewService(db);
        var code = await service.CreatePairingCodeAsync(LinkshellId, Me, label: null);
        var results = await service.RedeemPairingCodeForAllAsync(code);

        Assert.Equal(
            new[] { LinkshellId, AllowedLinkshellId },
            results.Select(r => r.Linkshell.Id).ToArray());
    }
}
