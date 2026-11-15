using System.Security.Claims;
using LinkshellManager.TestSupport;
using LinkshellManagerDiscordApp.Controllers;
using LinkshellManagerDiscordApp.Data;
using LinkshellManagerDiscordApp.Models;
using LinkshellManagerDiscordApp.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace LinkshellManager.PostgresTests;

// The save hook keeps AppUserLinkshells.LinkshellRoleId in step with Rank (AddMembershipRoleId),
// here on real Postgres, where the two-column foreign key actually checks what EF writes: a new
// member is linked to the role their Rank names, a rank change through the app's own endpoint
// moves the link (both halves of a Leader hand-over), a Rank that names no role leaves it empty,
// and deleting a role or a whole linkshell still works the way the app does it.
[Collection("postgres")]
public class MembershipRoleStampTests : IAsyncLifetime
{
    private const string Leader = "leader";
    private const string Raider = "raider";
    private const string Padded = "padded";
    private const string Shouty = "shouty";
    private const string Elsewhere = "elsewhere";

    private readonly PostgresFixture _pg;

    public MembershipRoleStampTests(PostgresFixture pg) => _pg = pg;

    public Task InitializeAsync() => _pg.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    private sealed record Seed(int Main, int Other, Dictionary<(int, string), int> Roles, Dictionary<string, int> Members);

    // "Main" has the default roles plus "Raider"; "Other" has its own default roles. The members
    // are added through EF after the roles exist, as every join path does.
    private async Task<Seed> SeedAsync()
    {
        await using var db = _pg.CreateContext();
        var main = new Linkshell { LinkshellName = "Main LS" };
        var other = new Linkshell { LinkshellName = "Other LS" };
        db.Linkshells.AddRange(main, other);
        db.Users.AddRange(
            new AppUser { Id = Leader, UserName = Leader, CharacterName = "Boss" },
            new AppUser { Id = Raider, UserName = Raider, CharacterName = "Raidy" },
            new AppUser { Id = Padded, UserName = Padded, CharacterName = "Spacey" },
            new AppUser { Id = Shouty, UserName = Shouty, CharacterName = "Loud" },
            new AppUser { Id = Elsewhere, UserName = Elsewhere, CharacterName = "Away" });
        await db.SaveChangesAsync();

        var roles = LinkshellRoleDefaults.BuildDefaultRoles(main.Id)
            .Concat(LinkshellRoleDefaults.BuildDefaultRoles(other.Id))
            .Append(new LinkshellRole { LinkshellId = main.Id, Name = "Raider", SortOrder = 10 })
            .ToList();
        db.LinkshellRoles.AddRange(roles);
        await db.SaveChangesAsync();

        var members = new Dictionary<string, AppUserLinkshell>
        {
            [Leader] = new() { LinkshellId = main.Id, AppUserId = Leader, CharacterName = "Boss", Rank = LinkshellRanks.Leader, LinkshellDkp = 0 },
            [Raider] = new() { LinkshellId = main.Id, AppUserId = Raider, CharacterName = "Raidy", Rank = "Raider", LinkshellDkp = 0 },
            [Padded] = new() { LinkshellId = main.Id, AppUserId = Padded, CharacterName = "Spacey", Rank = " Raider ", LinkshellDkp = 0 },
            [Shouty] = new() { LinkshellId = main.Id, AppUserId = Shouty, CharacterName = "Loud", Rank = "OFFICER", LinkshellDkp = 0 },
            [Elsewhere] = new() { LinkshellId = other.Id, AppUserId = Elsewhere, CharacterName = "Away", Rank = LinkshellRanks.Officer, LinkshellDkp = 0 },
        };
        db.AppUserLinkshells.AddRange(members.Values);
        await db.SaveChangesAsync();

        return new Seed(
            main.Id,
            other.Id,
            roles.ToDictionary(r => (r.LinkshellId, r.Name), r => r.Id),
            members.ToDictionary(pair => pair.Key, pair => pair.Value.Id));
    }

    [Fact]
    public async Task NewMembers_AreLinkedToTheRoleTheirRankNames()
    {
        var seed = await SeedAsync();

        Assert.Equal(seed.Roles[(seed.Main, LinkshellRanks.Leader)], await RoleIdOfAsync(seed.Members[Leader]));
        Assert.Equal(seed.Roles[(seed.Main, "Raider")], await RoleIdOfAsync(seed.Members[Raider]));
        Assert.Equal(seed.Roles[(seed.Main, "Raider")], await RoleIdOfAsync(seed.Members[Padded]));
        Assert.Null(await RoleIdOfAsync(seed.Members[Shouty]));  // capitals only: looked up by name, as today
        Assert.Equal(seed.Roles[(seed.Other, LinkshellRanks.Officer)], await RoleIdOfAsync(seed.Members[Elsewhere]));
    }

    [Fact]
    public async Task RankChange_ThroughTheEndpoint_MovesTheLink_ForBothSidesOfALeaderHandOver()
    {
        var seed = await SeedAsync();

        await using (var db = _pg.CreateContext())
        {
            var result = await Controller(db, Leader).UpdateMemberRoleAsync(
                seed.Main, seed.Members[Shouty], new ActivityUpdateMemberRoleRequest(LinkshellRanks.Officer), CancellationToken.None);
            Assert.IsType<OkObjectResult>(result);
        }
        Assert.Equal(seed.Roles[(seed.Main, LinkshellRanks.Officer)], await RoleIdOfAsync(seed.Members[Shouty]));

        await using (var db = _pg.CreateContext())
        {
            var result = await Controller(db, Leader).UpdateMemberRoleAsync(
                seed.Main, seed.Members[Raider], new ActivityUpdateMemberRoleRequest(LinkshellRanks.Leader), CancellationToken.None);
            Assert.IsType<OkObjectResult>(result);
        }
        Assert.Equal(seed.Roles[(seed.Main, LinkshellRanks.Leader)], await RoleIdOfAsync(seed.Members[Raider]));
        Assert.Equal(seed.Roles[(seed.Main, LinkshellRanks.Officer)], await RoleIdOfAsync(seed.Members[Leader]));
    }

    [Fact]
    public async Task RankThatNamesNoRole_ClearsTheLink_AndAnUnrelatedSaveLeavesItAlone()
    {
        var seed = await SeedAsync();

        await using (var db = _pg.CreateContext())
        {
            var member = await db.AppUserLinkshells.SingleAsync(m => m.Id == seed.Members[Raider]);
            member.Status = "Active";
            await db.SaveChangesAsync();
        }
        Assert.Equal(seed.Roles[(seed.Main, "Raider")], await RoleIdOfAsync(seed.Members[Raider]));

        await using (var db = _pg.CreateContext())
        {
            var member = await db.AppUserLinkshells.SingleAsync(m => m.Id == seed.Members[Raider]);
            member.Rank = "Auctionner";
            await db.SaveChangesAsync();
        }
        Assert.Null(await RoleIdOfAsync(seed.Members[Raider]));
    }

    // The role delete checks holders by name AND by id: " Raider " holds Raider by id only, and
    // without that the database would refuse the delete with an error instead of a message.
    [Fact]
    public async Task RoleDelete_IsRefusedWhileAnyoneHoldsIt_ThenWorks()
    {
        var seed = await SeedAsync();
        var raiderRole = seed.Roles[(seed.Main, "Raider")];

        await using (var db = _pg.CreateContext())
        {
            var member = await db.AppUserLinkshells.SingleAsync(m => m.Id == seed.Members[Raider]);
            member.Rank = LinkshellRanks.Member;
            await db.SaveChangesAsync();
        }

        await using (var db = _pg.CreateContext())
        {
            var refused = await Controller(db, Leader).DeleteLinkshellRoleAsync(seed.Main, raiderRole, CancellationToken.None);
            Assert.IsType<BadRequestObjectResult>(refused);
        }

        await using (var db = _pg.CreateContext())
        {
            var member = await db.AppUserLinkshells.SingleAsync(m => m.Id == seed.Members[Padded]);
            member.Rank = LinkshellRanks.Member;
            await db.SaveChangesAsync();
        }

        await using (var db = _pg.CreateContext())
        {
            var deleted = await Controller(db, Leader).DeleteLinkshellRoleAsync(seed.Main, raiderRole, CancellationToken.None);
            Assert.IsType<OkObjectResult>(deleted);
            Assert.False(await db.LinkshellRoles.AnyAsync(r => r.Id == raiderRole));
        }
    }

    // RosterSwitch: permissions read the role id, so renaming a custom role keeps every holder's
    // rights -- " Raider " too, whom the old rename (matching Rank exactly) left behind on Member.
    // Their Rank follows the new name, so Rank and id still name the same role.
    [Fact]
    public async Task RoleRename_ThroughTheEndpoint_KeepsEveryHoldersRights()
    {
        var seed = await SeedAsync();
        var raiderRole = seed.Roles[(seed.Main, "Raider")];
        await using (var db = _pg.CreateContext())
        {
            (await db.LinkshellRoles.SingleAsync(r => r.Id == raiderRole)).CanManageEvents = true;
            await db.SaveChangesAsync();
        }
        Assert.True(await CanManageEventsAsync(seed.Members[Raider]));
        Assert.True(await CanManageEventsAsync(seed.Members[Padded]));

        await using (var db = _pg.CreateContext())
        {
            var result = await Controller(db, Leader).UpdateLinkshellRoleAsync(
                seed.Main, raiderRole, RaiderPermissions("Strikers"), CancellationToken.None);
            Assert.IsType<OkObjectResult>(result);
        }

        foreach (var holder in new[] { Raider, Padded })
        {
            Assert.True(await CanManageEventsAsync(seed.Members[holder]), holder);
            Assert.Equal(raiderRole, await RoleIdOfAsync(seed.Members[holder]));
            await using var db = _pg.CreateContext();
            Assert.Equal("Strikers", (await db.AppUserLinkshells.AsNoTracking().SingleAsync(m => m.Id == seed.Members[holder])).Rank);
        }
        // Nobody else picked the role up.
        Assert.False(await CanManageEventsAsync(seed.Members[Shouty]));
    }

    // A role renamed straight in the table (Data Admin edits only the role row): the holders keep
    // it by id, so they keep its rights, even though their Rank text still says the old name.
    [Fact]
    public async Task RoleRenamedOnlyInTheTable_HoldersKeepItsRights()
    {
        var seed = await SeedAsync();
        var raiderRole = seed.Roles[(seed.Main, "Raider")];
        await using (var db = _pg.CreateContext())
        {
            var role = await db.LinkshellRoles.SingleAsync(r => r.Id == raiderRole);
            role.CanManageEvents = true;
            role.Name = "Strikers";
            await db.SaveChangesAsync();
        }

        Assert.True(await CanManageEventsAsync(seed.Members[Raider]));
        Assert.True(await CanManageEventsAsync(seed.Members[Padded]));
    }

    private async Task<bool> CanManageEventsAsync(int membershipId)
    {
        await using var db = _pg.CreateContext();
        var settings = new GlobalSettingsService(db, new MemoryCache(new MemoryCacheOptions()));
        var permissions = new LinkshellPermissions(db, new AdminOverrideService(db, settings, NullLogger<AdminOverrideService>.Instance));
        var member = await db.AppUserLinkshells.AsNoTracking().SingleAsync(m => m.Id == membershipId);
        return await permissions.CanAsync(member, r => r.CanManageEvents, CancellationToken.None);
    }

    private static ActivityLinkshellRolePermissions RaiderPermissions(string name) => new(
        name,
        CanManageRoles: false, CanManageMembers: false, CanManageEvents: true, CanModerateLiveEvent: false,
        CanAddLoot: false, CanManageInventory: false, CanManageCharts: false, CanManageTreasury: false,
        CanManageRules: false, CanManageAnnouncements: false, CanManageTods: false, CanAuditDkp: false,
        CanManageAuctions: false, CanLockAuctions: false, CanCustomizeLinkshell: false, CanManageParties: false,
        CanManageInvites: false);

    // The linkshell delete removes its members through EF and lets the database cascade the roles.
    [Fact]
    public async Task LinkshellDelete_WithMembersHoldingRoles_StillWorks()
    {
        var seed = await SeedAsync();

        await using (var db = _pg.CreateContext())
        {
            var linkshell = await db.Linkshells.Include(l => l.AppUserLinkshells).SingleAsync(l => l.Id == seed.Main);
            db.AppUserLinkshells.RemoveRange(linkshell.AppUserLinkshells);
            db.Linkshells.Remove(linkshell);
            await db.SaveChangesAsync();
        }

        await using (var db = _pg.CreateContext())
        {
            Assert.False(await db.LinkshellRoles.AnyAsync(r => r.LinkshellId == seed.Main));
            Assert.False(await db.AppUserLinkshells.AnyAsync(m => m.LinkshellId == seed.Main));
            Assert.True(await db.AppUserLinkshells.AnyAsync(m => m.LinkshellId == seed.Other));
        }
    }

    private async Task<int?> RoleIdOfAsync(int membershipId)
    {
        await using var db = _pg.CreateContext();
        return await db.AppUserLinkshells.AsNoTracking()
            .Where(m => m.Id == membershipId)
            .Select(m => m.LinkshellRoleId)
            .SingleAsync();
    }

    private static ActivityRosterController Controller(ApplicationDbContext db, string userId)
    {
        var userManager = new UserManager<AppUser>(
            new UserStore<AppUser>(db),
            Options.Create(new IdentityOptions()),
            new PasswordHasher<AppUser>(),
            Array.Empty<IUserValidator<AppUser>>(),
            Array.Empty<IPasswordValidator<AppUser>>(),
            new UpperInvariantLookupNormalizer(),
            new IdentityErrorDescriber(),
            null!,
            NullLogger<UserManager<AppUser>>.Instance);
        var settings = new GlobalSettingsService(db, new MemoryCache(new MemoryCacheOptions()));
        var adminOverride = new AdminOverrideService(db, settings, NullLogger<AdminOverrideService>.Instance);
        var http = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
                new[] { new Claim(ClaimTypes.NameIdentifier, userId) }, authenticationType: "Test")),
        };
        return ActivityControllerFactory.Create<ActivityRosterController>(
            http, db, userManager, adminOverride, new LinkshellPermissions(db, adminOverride));
    }
}
