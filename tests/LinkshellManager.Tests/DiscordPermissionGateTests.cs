using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LinkshellManagerDiscordApp.Controllers;
using LinkshellManagerDiscordApp.Data;
using LinkshellManagerDiscordApp.Models;
using LinkshellManagerDiscordApp.Options;
using LinkshellManagerDiscordApp.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LinkshellManager.Tests;

// The Discord bot's permission gates, now all answered by LinkshellPermissions.
//
// Before, the bot had its own copies: the board's officer buttons (Add Member, Move, Set Leader,
// Lock, Remove) and the /lsm launch card tested the Leader/Officer RANK, and End Camp read the role
// row itself without the Member-role fallback. So a custom role with "Manage parties" could move
// people in the app but not with the Discord buttons, and an Officer whose role had it switched off
// still could in Discord.
//
// The default-role cases give the same answers the old copies gave (characterization). The
// custom-role and switched-off-Officer cases are the deliberate change.
public class DiscordPermissionGateTests
{
    private const int Ls = 1;
    private const int OtherLs = 2;
    private const string UserId = "user-1";
    private const string Guild = "guild-1";
    private const string OtherGuild = "guild-2";

    private static ApplicationDbContext NewDb() =>
        new(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    // Only the database and the permission service are used by the gates; the rest stay null.
    private static DiscordInteractionsController NewController(ApplicationDbContext db) =>
        new(null!, db, null!, NullLogger<DiscordInteractionsController>.Instance, null!,
            Microsoft.Extensions.Options.Options.Create(new DiscordOAuthOptions()),
            null!, null!, null!, null!, null!, null!, TestPermissions.For(db));

    private static Event BoardIn(int linkshellId) => new() { Id = 10, LinkshellId = linkshellId };

    // Ls is linked to Guild, OtherLs to OtherGuild. The default roles for both, with `replace`
    // swapped in for the default of that name in Ls, and the user in Ls with `rank`.
    private static async Task<ApplicationDbContext> SeededAsync(
        string rank, LinkshellRole? replace = null, bool superAdmin = false)
    {
        var db = NewDb();
        db.Linkshells.Add(new Linkshell { Id = Ls, LinkshellName = "LS1", LootStructure = "Dkp", DiscordGuildId = Guild });
        db.Linkshells.Add(new Linkshell { Id = OtherLs, LinkshellName = "LS2", LootStructure = "Dkp", DiscordGuildId = OtherGuild });
        db.LinkshellRoles.AddRange(LinkshellRoleDefaults.BuildDefaultRoles(Ls)
            .Where(r => !string.Equals(r.Name, replace?.Name, StringComparison.OrdinalIgnoreCase)));
        db.LinkshellRoles.AddRange(LinkshellRoleDefaults.BuildDefaultRoles(OtherLs));
        if (replace is not null)
        {
            replace.LinkshellId = Ls;
            db.LinkshellRoles.Add(replace);
        }
        db.Users.Add(new AppUser { Id = UserId, UserName = UserId, IsSuperAdmin = superAdmin });
        db.AppUserLinkshells.Add(new AppUserLinkshell { AppUserId = UserId, LinkshellId = Ls, Rank = rank });
        await db.SaveChangesAsync();
        return db;
    }

    private static Task SwitchOverrideOnAsync(ApplicationDbContext db) =>
        new GlobalSettingsService(db, new MemoryCache(new MemoryCacheOptions())).SetAdminOverrideEnabledAsync(true);

    // ---- Default roles answer as the old rank checks did.

    [Theory]
    [InlineData(LinkshellRanks.Leader, true)]
    [InlineData(LinkshellRanks.Officer, true)]
    [InlineData(LinkshellRanks.Member, false)]
    [InlineData(LinkshellRanks.Trial, false)]
    public async Task DefaultRoles_AnswerAsBefore_OnEveryGate(string rank, bool expected)
    {
        await using var db = await SeededAsync(rank);
        var controller = NewController(db);

        Assert.Equal(expected, await controller.CanEditPartyBoardAsync(BoardIn(Ls), UserId, CancellationToken.None));
        Assert.Equal(expected, await controller.CanEndCampAsync(UserId, Ls, CancellationToken.None));
        Assert.Equal(expected, await controller.CanPostLaunchCardAsync(UserId, Guild, CancellationToken.None));
    }

    [Fact]
    public async Task NoAccount_OrNotAMemberThere_IsRefused()
    {
        await using var db = await SeededAsync(LinkshellRanks.Leader);
        var controller = NewController(db);

        Assert.False(await controller.CanEditPartyBoardAsync(BoardIn(Ls), null, CancellationToken.None));
        Assert.False(await controller.CanEndCampAsync(null, Ls, CancellationToken.None));
        Assert.False(await controller.CanPostLaunchCardAsync(null, Guild, CancellationToken.None));

        Assert.False(await controller.CanEditPartyBoardAsync(BoardIn(OtherLs), UserId, CancellationToken.None));
        Assert.False(await controller.CanEndCampAsync(UserId, OtherLs, CancellationToken.None));
        Assert.False(await controller.CanPostLaunchCardAsync(UserId, OtherGuild, CancellationToken.None));
    }

    [Fact]
    public async Task AdminOverride_LiftsAMember_OnlyWhereTheyJoined()
    {
        await using var db = await SeededAsync(LinkshellRanks.Member, superAdmin: true);
        await SwitchOverrideOnAsync(db);
        var controller = NewController(db);

        Assert.True(await controller.CanEditPartyBoardAsync(BoardIn(Ls), UserId, CancellationToken.None));
        Assert.True(await controller.CanEndCampAsync(UserId, Ls, CancellationToken.None));
        Assert.True(await controller.CanPostLaunchCardAsync(UserId, Guild, CancellationToken.None));

        Assert.False(await controller.CanEditPartyBoardAsync(BoardIn(OtherLs), UserId, CancellationToken.None));
        Assert.False(await controller.CanEndCampAsync(UserId, OtherLs, CancellationToken.None));
        Assert.False(await controller.CanPostLaunchCardAsync(UserId, OtherGuild, CancellationToken.None));
    }

    // ---- The deliberate change: the role's flag decides, not the rank's name.

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task PartyBoard_CustomRole_ManagePartiesDecides(bool flag)
    {
        await using var db = await SeededAsync("Party Lead",
            new LinkshellRole { Name = "Party Lead", CanManageParties = flag });

        Assert.Equal(flag, await NewController(db).CanEditPartyBoardAsync(BoardIn(Ls), UserId, CancellationToken.None));
    }

    [Fact]
    public async Task PartyBoard_OfficerWithManagePartiesOff_IsRefused()
    {
        await using var db = await SeededAsync(LinkshellRanks.Officer,
            new LinkshellRole { Name = LinkshellRanks.Officer, IsSystem = true, CanManageEvents = true, CanManageParties = false });
        var controller = NewController(db);

        Assert.False(await controller.CanEditPartyBoardAsync(BoardIn(Ls), UserId, CancellationToken.None));
        // Only the board controls are off; ending a camp is a different permission.
        Assert.True(await controller.CanEndCampAsync(UserId, Ls, CancellationToken.None));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task LaunchCard_CustomRole_ManageEventsDecides(bool flag)
    {
        await using var db = await SeededAsync("Camp Lead",
            new LinkshellRole { Name = "Camp Lead", CanManageEvents = flag });

        Assert.Equal(flag, await NewController(db).CanPostLaunchCardAsync(UserId, Guild, CancellationToken.None));
    }

    [Fact]
    public async Task LaunchCard_OfficerWithManageEventsOff_IsRefused()
    {
        await using var db = await SeededAsync(LinkshellRanks.Officer,
            new LinkshellRole { Name = LinkshellRanks.Officer, IsSystem = true, CanManageEvents = false, CanManageParties = true });

        Assert.False(await NewController(db).CanPostLaunchCardAsync(UserId, Guild, CancellationToken.None));
    }

    // A Leader always passes, even when their role row was saved before a permission existed.
    [Fact]
    public async Task Leader_PassesEvenWhenTheRoleRowSaysNo()
    {
        await using var db = await SeededAsync(LinkshellRanks.Leader,
            new LinkshellRole { Name = LinkshellRanks.Leader, IsSystem = true });
        var controller = NewController(db);

        Assert.True(await controller.CanEditPartyBoardAsync(BoardIn(Ls), UserId, CancellationToken.None));
        Assert.True(await controller.CanEndCampAsync(UserId, Ls, CancellationToken.None));
        Assert.True(await controller.CanPostLaunchCardAsync(UserId, Guild, CancellationToken.None));
    }
}
