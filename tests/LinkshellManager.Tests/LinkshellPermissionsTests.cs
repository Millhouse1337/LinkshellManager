using LinkshellManagerDiscordApp.Data;
using LinkshellManagerDiscordApp.Models;
using LinkshellManagerDiscordApp.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LinkshellManager.Tests;

// LinkshellPermissions is the one copy of "may this member do X in this linkshell?".
// It was moved out of ActivityDataController.HelpersAuth unchanged, so these tests pin the
// rule order that controller already used:
//   no membership -> false; admin override -> true; Leader -> true; role flag, with an
//   unknown rank falling back to the Member role.
// Later steps point the addon and Discord copies at this service; if one of these tests
// has to change, a permission changed for every surface at once — that must be on purpose.
public class LinkshellPermissionsTests
{
    private const int Ls = 1;
    private const int OtherLs = 2;
    private const string AdminId = "admin-1";
    private const string UserId = "user-1";

    private static ApplicationDbContext NewDb() =>
        new(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static async Task<LinkshellPermissions> NewServiceAsync(ApplicationDbContext db, bool overrideOn = false)
    {
        var settings = new GlobalSettingsService(db, new MemoryCache(new MemoryCacheOptions()));
        if (overrideOn)
        {
            await settings.SetAdminOverrideEnabledAsync(true);
        }
        var adminOverride = new AdminOverrideService(db, settings, NullLogger<AdminOverrideService>.Instance);
        return new LinkshellPermissions(db, adminOverride);
    }

    private static async Task<AppUserLinkshell> SeedMemberAsync(
        ApplicationDbContext db, string userId, string? rank, int linkshellId = Ls, bool superAdmin = false)
    {
        if (!await db.Linkshells.AnyAsync(l => l.Id == linkshellId))
        {
            db.Linkshells.Add(new Linkshell { Id = linkshellId, LinkshellName = $"LS{linkshellId}", LootStructure = "Dkp" });
        }
        if (await db.Users.FindAsync(userId) is null)
        {
            db.Users.Add(new AppUser { Id = userId, UserName = userId, IsSuperAdmin = superAdmin });
        }
        var membership = new AppUserLinkshell { AppUserId = userId, LinkshellId = linkshellId, Rank = rank };
        db.AppUserLinkshells.Add(membership);
        await db.SaveChangesAsync();
        return membership;
    }

    [Fact]
    public async Task NullMembership_IsDenied_EvenWithOverrideOn()
    {
        await using var db = NewDb();
        var permissions = await NewServiceAsync(db, overrideOn: true);

        Assert.False(await permissions.CanAsync((AppUserLinkshell?)null, r => true, CancellationToken.None));
    }

    [Fact]
    public async Task ByIds_NonMemberAndBlankId_AreDenied()
    {
        await using var db = NewDb();
        // The super admin is a member of another linkshell only: the override must not reach
        // a linkshell they never joined.
        await SeedMemberAsync(db, AdminId, LinkshellRanks.Leader, linkshellId: OtherLs, superAdmin: true);
        db.Linkshells.Add(new Linkshell { Id = Ls, LinkshellName = "LS1", LootStructure = "Dkp" });
        await db.SaveChangesAsync();
        var permissions = await NewServiceAsync(db, overrideOn: true);

        Assert.False(await permissions.CanAsync(AdminId, Ls, r => true, CancellationToken.None));
        Assert.False(await permissions.CanAsync((string?)null, Ls, r => true, CancellationToken.None));
        Assert.False(await permissions.CanAsync("  ", Ls, r => true, CancellationToken.None));
        Assert.True(await permissions.CanAsync(AdminId, OtherLs, r => r.CanManageTreasury, CancellationToken.None));
    }

    [Fact]
    public async Task AdminOverride_GrantsEverything_ToAMemberSuperAdmin()
    {
        await using var db = NewDb();
        var membership = await SeedMemberAsync(db, AdminId, LinkshellRanks.Member, superAdmin: true);
        var permissions = await NewServiceAsync(db, overrideOn: true);

        Assert.True(await permissions.CanAsync(membership, r => r.CanManageTreasury, CancellationToken.None));
        Assert.True(await permissions.CanAsync(membership, r => false, CancellationToken.None));
    }

    [Fact]
    public async Task AdminOverrideOff_SuperAdminGetsOnlyTheirRank()
    {
        await using var db = NewDb();
        var membership = await SeedMemberAsync(db, AdminId, LinkshellRanks.Member, superAdmin: true);
        var permissions = await NewServiceAsync(db, overrideOn: false);

        Assert.False(await permissions.CanAsync(membership, r => r.CanManageTreasury, CancellationToken.None));
        Assert.True(await permissions.CanAsync(membership, r => r.CanBid, CancellationToken.None));
    }

    [Fact]
    public async Task Leader_AlwaysAllowed_EvenWhenTheLeaderRoleRowSaysNo()
    {
        await using var db = NewDb();
        var membership = await SeedMemberAsync(db, UserId, "leader"); // rank match is case-insensitive here
        var permissions = await NewServiceAsync(db);
        // A mis-edited Leader role row with every flag off.
        db.LinkshellRoles.Add(new LinkshellRole { LinkshellId = Ls, Name = LinkshellRanks.Leader, IsSystem = true });
        await db.SaveChangesAsync();

        Assert.True(await permissions.CanAsync(membership, r => r.CanManageRoles, CancellationToken.None));
        Assert.True(await permissions.CanAsync(membership, r => false, CancellationToken.None));
    }

    [Fact]
    public async Task CustomRole_UsesItsOwnFlags()
    {
        await using var db = NewDb();
        var membership = await SeedMemberAsync(db, UserId, "Raid Lead");
        db.LinkshellRoles.Add(new LinkshellRole { LinkshellId = Ls, Name = "Raid Lead", CanManageEvents = true });
        await db.SaveChangesAsync();
        var permissions = await NewServiceAsync(db);

        Assert.True(await permissions.CanAsync(membership, r => r.CanManageEvents, CancellationToken.None));
        Assert.False(await permissions.CanAsync(membership, r => r.CanAddLoot, CancellationToken.None));
        // The custom role does not inherit Member's flags: it is used as-is.
        Assert.False(await permissions.CanAsync(membership, r => r.CanBid, CancellationToken.None));
    }

    [Fact]
    public async Task UnknownRank_FallsBackToTheMemberRole()
    {
        await using var db = NewDb();
        var membership = await SeedMemberAsync(db, UserId, "Deleted Role");
        var permissions = await NewServiceAsync(db);

        Assert.True(await permissions.CanAsync(membership, r => r.CanBid, CancellationToken.None));
        Assert.False(await permissions.CanAsync(membership, r => r.CanManageEvents, CancellationToken.None));

        var role = await permissions.GetEffectiveRoleAsync("Deleted Role", Ls, CancellationToken.None);
        Assert.Equal(LinkshellRanks.Member, role?.Name);
    }

    [Fact]
    public async Task BlankRank_IsTreatedAsMember()
    {
        await using var db = NewDb();
        var membership = await SeedMemberAsync(db, UserId, rank: null);
        var permissions = await NewServiceAsync(db);

        Assert.True(await permissions.CanAsync(membership, r => r.CanBid, CancellationToken.None));
        Assert.False(await permissions.CanAsync(membership, r => r.CanManageMembers, CancellationToken.None));
        Assert.Equal(LinkshellRanks.Member, (await permissions.GetEffectiveRoleAsync("  ", Ls, CancellationToken.None))?.Name);
    }

    [Fact]
    public async Task Officer_UsesTheSeededOfficerDefaults()
    {
        await using var db = NewDb();
        var membership = await SeedMemberAsync(db, UserId, LinkshellRanks.Officer);
        var permissions = await NewServiceAsync(db);

        Assert.True(await permissions.CanAsync(membership, r => r.CanManageEvents, CancellationToken.None));
        Assert.False(await permissions.CanAsync(membership, r => r.CanManageTreasury, CancellationToken.None));
    }

    [Fact]
    public async Task EnsureDefaultRoles_SeedsMissingRolesOnce()
    {
        await using var db = NewDb();
        await SeedMemberAsync(db, UserId, LinkshellRanks.Member);
        var permissions = await NewServiceAsync(db);

        var first = await permissions.EnsureDefaultRolesAsync(Ls, CancellationToken.None);
        var second = await permissions.EnsureDefaultRolesAsync(Ls, CancellationToken.None);

        var expected = LinkshellRoleDefaults.BuildDefaultRoles(Ls).Count();
        Assert.Equal(expected, first.Count);
        Assert.Equal(expected, second.Count);
        Assert.Equal(expected, await db.LinkshellRoles.CountAsync(r => r.LinkshellId == Ls));
    }

    [Fact]
    public async Task GetMembership_LoadsTheLinkshell_AndIgnoresOtherLinkshells()
    {
        await using var db = NewDb();
        await SeedMemberAsync(db, UserId, LinkshellRanks.Member, linkshellId: OtherLs);
        var permissions = await NewServiceAsync(db);

        Assert.Null(await permissions.GetMembershipAsync(UserId, Ls, CancellationToken.None));
        var membership = await permissions.GetMembershipAsync(UserId, OtherLs, CancellationToken.None);
        Assert.NotNull(membership?.Linkshell);
    }

    // The controller keeps thin wrappers so its many call sites stay unchanged. Guard against
    // the old body growing back there (two copies is how the rules drifted in the first place).
    [Fact]
    public void ActivityController_DelegatesToTheService()
    {
        var text = File.ReadAllText(Path.Combine(FindRepoPath("Controllers"), "Activity", "ActivityControllerBase.cs"));

        Assert.Contains("_permissions.CanAsync(", text);
        Assert.Contains("_permissions.GetEffectiveRoleAsync(", text);
        Assert.Contains("_permissions.GetMembershipAsync(", text);
        Assert.DoesNotContain("LinkshellRanks.IsLeader(", text);
        Assert.DoesNotContain("BuildDefaultRoles(", text);
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
