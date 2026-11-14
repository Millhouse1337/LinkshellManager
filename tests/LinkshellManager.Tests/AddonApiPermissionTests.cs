using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using LinkshellManagerDiscordApp.Controllers;
using LinkshellManagerDiscordApp.Data;
using LinkshellManagerDiscordApp.Models;
using LinkshellManagerDiscordApp.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LinkshellManager.Tests;

// The addon controller's permission helpers, now all answered by LinkshellPermissions.
//
// Before, the addon had its own copies: the token checks read the role row but skipped the Leader
// and the super-admin override, and the live-camp management routes the app calls (window DKP,
// bonuses, closing tick, Misc posts, deleting a window or attendee) tested the Leader/Officer RANK
// while the app's own routes on the same camp card tested a permission.
//
// The default-role cases below give the same answers the old copies gave (characterization). The
// Leader-with-a-stale-row, custom-role and override cases are the deliberate changes.
public class AddonApiPermissionTests
{
    private const int Ls = 1;
    private const int OtherLs = 2;
    private const string UserId = "user-1";

    private static ApplicationDbContext NewDb() =>
        new(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static AddonApiController NewController(ApplicationDbContext db) =>
        new(db, null!, null!, null!, null!, null!, null!, null!, null!, null!,
            TestPermissions.For(db), NullLogger<AddonApiController>.Instance);

    private static AddonApiToken Token(string? issuedTo = UserId) =>
        new() { Id = 1, LinkshellId = Ls, IssuedToAppUserId = issuedTo, TokenPrefix = "att_1", TokenHash = "h" };

    // The default roles for both linkshells, with `replace` swapped in for the default of that name,
    // and the user in Ls with `rank`.
    private static async Task<(ApplicationDbContext Db, AppUserLinkshell Membership)> SeededAsync(
        string rank, LinkshellRole? replace = null, bool superAdmin = false)
    {
        var db = NewDb();
        db.Linkshells.Add(new Linkshell { Id = Ls, LinkshellName = "LS1", LootStructure = "Dkp" });
        db.Linkshells.Add(new Linkshell { Id = OtherLs, LinkshellName = "LS2", LootStructure = "Dkp" });
        db.LinkshellRoles.AddRange(LinkshellRoleDefaults.BuildDefaultRoles(Ls)
            .Where(r => !string.Equals(r.Name, replace?.Name, StringComparison.OrdinalIgnoreCase)));
        db.LinkshellRoles.AddRange(LinkshellRoleDefaults.BuildDefaultRoles(OtherLs));
        if (replace is not null)
        {
            replace.LinkshellId = Ls;
            db.LinkshellRoles.Add(replace);
        }
        db.Users.Add(new AppUser { Id = UserId, UserName = UserId, IsSuperAdmin = superAdmin });
        var membership = new AppUserLinkshell { AppUserId = UserId, LinkshellId = Ls, Rank = rank };
        db.AppUserLinkshells.Add(membership);
        await db.SaveChangesAsync();
        return (db, membership);
    }

    private static Task SwitchOverrideOnAsync(ApplicationDbContext db) =>
        new GlobalSettingsService(db, new MemoryCache(new MemoryCacheOptions())).SetAdminOverrideEnabledAsync(true);

    // ---- TokenIssuerCanModerateAsync: the addon's officer right (Manage attendance with the addon).

    [Theory]
    [InlineData(LinkshellRanks.Leader, true)]
    [InlineData(LinkshellRanks.Officer, true)]
    [InlineData(LinkshellRanks.Member, false)]
    [InlineData(LinkshellRanks.Trial, false)]
    public async Task Moderate_DefaultRoles_AnswerAsBefore(string rank, bool expected)
    {
        var (db, _) = await SeededAsync(rank);
        await using var _db = db;

        Assert.Equal(expected, await NewController(db).TokenIssuerCanModerateAsync(Token(), Ls, CancellationToken.None));
    }

    [Fact]
    public async Task Moderate_Leader_PassesEvenWhenTheRoleRowSaysNo()
    {
        var (db, _) = await SeededAsync(LinkshellRanks.Leader,
            new LinkshellRole { Name = LinkshellRanks.Leader, CanManageAttendanceWithAddon = false });
        await using var _db = db;

        Assert.True(await NewController(db).TokenIssuerCanModerateAsync(Token(), Ls, CancellationToken.None));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Moderate_CustomRole_ItsFlagDecides(bool flag)
    {
        var (db, _) = await SeededAsync("Camp Lead",
            new LinkshellRole { Name = "Camp Lead", CanUseAddon = true, CanManageAttendanceWithAddon = flag });
        await using var _db = db;

        Assert.Equal(flag, await NewController(db).TokenIssuerCanModerateAsync(Token(), Ls, CancellationToken.None));
    }

    [Fact]
    public async Task Moderate_NoIssuer_OrNotAMemberThere_IsRefused()
    {
        var (db, _) = await SeededAsync(LinkshellRanks.Leader);
        await using var _db = db;
        var controller = NewController(db);

        Assert.False(await controller.TokenIssuerCanModerateAsync(Token(issuedTo: null), Ls, CancellationToken.None));
        Assert.False(await controller.TokenIssuerCanModerateAsync(Token(), OtherLs, CancellationToken.None));
    }

    [Fact]
    public async Task Moderate_AdminOverride_LiftsAMember_OnlyWhereTheyJoined()
    {
        var (db, _) = await SeededAsync(LinkshellRanks.Member, superAdmin: true);
        await using var _db = db;
        await SwitchOverrideOnAsync(db);
        var controller = NewController(db);

        Assert.True(await controller.TokenIssuerCanModerateAsync(Token(), Ls, CancellationToken.None));
        Assert.False(await controller.TokenIssuerCanModerateAsync(Token(), OtherLs, CancellationToken.None));
    }

    // ---- TokenIssuerCanAsync with the ToD flags (posting a ToD, its claim, a Claim Shield capture).

    [Theory]
    [InlineData(LinkshellRanks.Leader, true, true)]
    [InlineData(LinkshellRanks.Officer, true, true)]
    [InlineData(LinkshellRanks.Member, false, false)]
    [InlineData(LinkshellRanks.Trial, false, false)]
    public async Task TodFlags_DefaultRoles_AnswerAsBefore(string rank, bool manage, bool submit)
    {
        var (db, _) = await SeededAsync(rank);
        await using var _db = db;
        var controller = NewController(db);

        Assert.Equal(manage, await controller.TokenIssuerCanAsync(Token(), Ls, r => r.CanManageTods, CancellationToken.None));
        Assert.Equal(submit, await controller.TokenIssuerCanAsync(Token(), Ls, r => r.CanSubmitTodForApproval, CancellationToken.None));
    }

    [Fact]
    public async Task TodFlags_LeaderWithAStaleRow_AndASubmitOnlyCustomRole()
    {
        var (leaderDb, _) = await SeededAsync(LinkshellRanks.Leader,
            new LinkshellRole { Name = LinkshellRanks.Leader });
        await using (leaderDb)
        {
            Assert.True(await NewController(leaderDb).TokenIssuerCanAsync(
                Token(), Ls, r => r.CanManageTods, CancellationToken.None));
        }

        var (db, _) = await SeededAsync("Spotter",
            new LinkshellRole { Name = "Spotter", CanSubmitTodForApproval = true });
        await using var _db = db;
        var controller = NewController(db);
        Assert.False(await controller.TokenIssuerCanAsync(Token(), Ls, r => r.CanManageTods, CancellationToken.None));
        Assert.True(await controller.TokenIssuerCanAsync(Token(), Ls, r => r.CanSubmitTodForApproval, CancellationToken.None));
    }

    // ---- CanEditLiveCampAsync: the management routes the app's live camp card calls.

    // Same answers the old Leader/Officer rank test gave for the default roles.
    [Theory]
    [InlineData(LinkshellRanks.Leader, true)]
    [InlineData(LinkshellRanks.Officer, true)]
    [InlineData(LinkshellRanks.Member, false)]
    [InlineData(LinkshellRanks.Trial, false)]
    public async Task LiveCamp_DefaultRoles_AnswerAsTheOldRankRule(string rank, bool expected)
    {
        var (db, membership) = await SeededAsync(rank);
        await using var _db = db;

        Assert.Equal(expected, await NewController(db).CanEditLiveCampAsync(membership, CancellationToken.None));
    }

    [Theory]
    [InlineData(true, false, true)]
    [InlineData(false, true, true)]
    [InlineData(false, false, false)]
    public async Task LiveCamp_CustomRole_ModerateOrManageEvents(bool moderate, bool manageEvents, bool expected)
    {
        var (db, membership) = await SeededAsync("Camp Lead",
            new LinkshellRole { Name = "Camp Lead", CanModerateLiveEvent = moderate, CanManageEvents = manageEvents });
        await using var _db = db;

        Assert.Equal(expected, await NewController(db).CanEditLiveCampAsync(membership, CancellationToken.None));
    }

    // The deliberate tightening: the Officer RANK alone no longer edits a camp's pay when the
    // linkshell switched both permissions off for its Officer role.
    [Fact]
    public async Task LiveCamp_OfficerWhoseRoleHasNeitherFlag_IsRefused()
    {
        var (db, membership) = await SeededAsync(LinkshellRanks.Officer,
            new LinkshellRole { Name = LinkshellRanks.Officer, IsSystem = true });
        await using var _db = db;

        Assert.False(await NewController(db).CanEditLiveCampAsync(membership, CancellationToken.None));
    }

    [Fact]
    public async Task LiveCamp_LeaderWithAStaleRow_NoMembership_AndTheOverride()
    {
        var (leaderDb, leader) = await SeededAsync(LinkshellRanks.Leader,
            new LinkshellRole { Name = LinkshellRanks.Leader });
        await using (leaderDb)
        {
            var controller = NewController(leaderDb);
            Assert.True(await controller.CanEditLiveCampAsync(leader, CancellationToken.None));
            Assert.False(await controller.CanEditLiveCampAsync(null, CancellationToken.None));
        }

        var (db, member) = await SeededAsync(LinkshellRanks.Member, superAdmin: true);
        await using var _db = db;
        await SwitchOverrideOnAsync(db);
        Assert.True(await NewController(db).CanEditLiveCampAsync(member, CancellationToken.None));
    }

    // ---- Wiring. Reflection can't prove a call site, so pin the shape (precedent:
    // TodEditPermissionTests).

    // No hand-written rule is left in the addon controller: no rank test, no role-row lookup.
    [Fact]
    public void AddonController_HasNoPermissionRuleOfItsOwn()
    {
        foreach (var file in Directory.GetFiles(FindRepoPath("Controllers"), "AddonApiController*.cs"))
        {
            var text = File.ReadAllText(file);
            var name = Path.GetFileName(file);
            Assert.DoesNotContain("CanManageLinkshell(", text);
            Assert.DoesNotContain("GetTokenIssuerRoleAsync", text);
            Assert.DoesNotContain("IsLeaderOrOfficer", text);
            Assert.False(text.Contains("LinkshellRoles", StringComparison.Ordinal), $"{name} reads LinkshellRoles itself.");
        }

        var auth = File.ReadAllText(Path.Combine(FindRepoPath("Services"), "AddonApiAuthService.cs"));
        Assert.DoesNotContain("LinkshellRoles", auth);
        Assert.DoesNotContain("IsLeaderOrOfficer", auth);
    }

    // Every management route that resolves a signed-in user and then edits a live camp asks
    // CanEditLiveCampAsync, and nothing else, before it writes.
    [Theory]
    [InlineData("AddonApiController.CloseBonus.cs", 1)]
    [InlineData("AddonApiController.OpenBonus.cs", 1)]
    [InlineData("AddonApiController.ClosingWindow.cs", 1)]
    [InlineData("AddonApiController.KillBonus.cs", 1)]
    [InlineData("AddonApiController.MiscPostDkp.cs", 1)]
    [InlineData("AddonApiController.WindowDkp.cs", 1)]
    [InlineData("AddonApiController.WindowPosts.cs", 1)]
    [InlineData("AddonApiController.WebManagement.cs", 2)]
    public void LiveCampManagementRoutes_UseTheOneRule(string file, int expected)
    {
        var text = File.ReadAllText(Path.Combine(FindRepoPath("Controllers"), file));

        Assert.Equal(expected, Regex.Matches(text, @"if \(!await CanEditLiveCampAsync\(\w+, cancellationToken\)\)").Count);
    }

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
