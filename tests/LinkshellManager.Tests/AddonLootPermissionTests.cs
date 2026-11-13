using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LinkshellManagerDiscordApp.Controllers;
using LinkshellManagerDiscordApp.Data;
using LinkshellManagerDiscordApp.Models;
using LinkshellManagerDiscordApp.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LinkshellManager.Tests;

// Who may post loot from the addon, and that every loot row records who posted it.
//
// The addon's loot routes used to check only that the token belonged to the linkshell, so any
// paired member (even a Trial, or someone since removed) could name anyone as the winner and take
// their DKP. They now need Add loot or the addon's officer right (Leader always passes), and the
// "can't afford it" reply no longer reads out the winner's balance. Each row also stamps
// RecordedByAppUserId so a bad post can be traced to a person.
public class AddonLootPermissionTests
{
    private const int Ls = 1;

    private static ApplicationDbContext NewDb() =>
        new(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    // The poster's account in the linkshell, with a role of that name (none for "no row").
    private static async Task<ApplicationDbContext> SeededAsync(string rank, LinkshellRole? role = null)
    {
        var db = NewDb();
        db.Linkshells.Add(new Linkshell { Id = Ls, LinkshellName = "LS", LootStructure = "Dkp" });
        db.LinkshellRoles.AddRange(LinkshellRoleDefaults.BuildDefaultRoles(Ls).Where(r => r.Name != role?.Name));
        if (role is not null)
        {
            db.LinkshellRoles.Add(role);
        }
        db.AppUserLinkshells.Add(new AppUserLinkshell { LinkshellId = Ls, AppUserId = "poster", CharacterName = "Poster", Rank = rank });
        await db.SaveChangesAsync();
        return db;
    }

    private static Task<bool> MayPostAsync(ApplicationDbContext db, string appUserId = "poster") =>
        TestPermissions.For(db).CanAsync(appUserId, Ls, AddonApiController.PostsAddonLoot, CancellationToken.None);

    // ---- The rule itself (LinkshellPermissions + PostsAddonLoot).

    [Theory]
    [InlineData(LinkshellRanks.Leader, true)]
    [InlineData(LinkshellRanks.Officer, true)]
    [InlineData(LinkshellRanks.Member, false)]
    [InlineData(LinkshellRanks.Trial, false)]
    public async Task DefaultRoles_PostAddonLoot(string rank, bool expected)
    {
        await using var db = await SeededAsync(rank);

        Assert.Equal(expected, await MayPostAsync(db));
    }

    [Fact]
    public async Task NotAMemberAnyMore_IsRefused()
    {
        await using var db = await SeededAsync(LinkshellRanks.Officer);

        Assert.False(await MayPostAsync(db, "someone-who-left"));
    }

    [Fact]
    public async Task Leader_PassesEvenWithAStaleRoleRow()
    {
        await using var db = await SeededAsync(
            LinkshellRanks.Leader, new LinkshellRole { LinkshellId = Ls, Name = LinkshellRanks.Leader });

        Assert.True(await MayPostAsync(db));
    }

    [Theory]
    [InlineData(true, false, true)]
    [InlineData(false, true, true)]
    [InlineData(false, false, false)]
    public async Task CustomRole_EitherLootRight_IsEnough(bool canAddLoot, bool addonOfficer, bool expected)
    {
        await using var db = await SeededAsync("Camp", new LinkshellRole
        {
            LinkshellId = Ls, Name = "Camp", CanUseAddon = true,
            CanAddLoot = canAddLoot, CanManageAttendanceWithAddon = addonOfficer,
        });

        Assert.Equal(expected, await MayPostAsync(db));
    }

    // ---- Wiring. Reflection can't prove a call site, so pin the shape (precedent:
    // TodEditPermissionTests): the gate runs before anything is written, and the affordability
    // reply no longer prints the winner's available DKP.

    [Fact]
    public void AddAddonLoot_ChecksThePermissionBeforeWriting()
    {
        var body = AddAddonLootBody();

        var gate = body.IndexOf("TokenIssuerCanAsync(token, token.LinkshellId, PostsAddonLoot", StringComparison.Ordinal);
        var write = body.IndexOf("manualLoot.AddAsync(", StringComparison.Ordinal);
        Assert.True(gate >= 0, "AddAddonLootAsync no longer checks PostsAddonLoot.");
        Assert.True(write > gate, "The permission check must run before the loot is written.");
        Assert.Contains("Status403Forbidden", body);
        Assert.Contains("recordedByAppUserId: token.IssuedToAppUserId", body);
    }

    [Fact]
    public void AddAddonLoot_DoesNotReadOutTheWinnersBalance()
    {
        var body = AddAddonLootBody();

        Assert.DoesNotContain("{availableDkp", body);
    }

    // The addon hides its Loot Pool Save from someone the server would refuse, so /me has to
    // answer with the SAME rule the loot routes use -- not canModerate, which misses Add loot.
    [Fact]
    public void Me_SendsCanPostLoot_FromTheLootRule()
    {
        var text = File.ReadAllText(Path.Combine(FindRepoPath("Controllers"), "AddonApiController.AddonAuth.cs"));
        var from = text.IndexOf("public async Task<IActionResult> MeAsync(", StringComparison.Ordinal);
        Assert.True(from >= 0, "MeAsync not found.");
        var body = text[from..];

        Assert.Contains("var canPostLoot = await TokenIssuerCanAsync(token, token.LinkshellId, PostsAddonLoot", body);
        Assert.Contains("canPostLoot,", body);
    }

    // ---- RecordedByAppUserId.

    [Fact]
    public async Task ManualLoot_RecordsWhoPostedIt()
    {
        await using var db = NewDb();
        db.Linkshells.Add(new Linkshell { Id = Ls, LinkshellName = "LS", LootStructure = "Dkp" });
        db.Users.Add(new AppUser { Id = "winner", UserName = "alice" });
        db.AppUserLinkshells.Add(new AppUserLinkshell
        {
            Id = 1, LinkshellId = Ls, AppUserId = "winner", CharacterName = "Alice", LinkshellDkp = 100,
        });
        await db.SaveChangesAsync();

        var pools = new DkpPoolResolver(db, new DkpPoolProvisioner(db));
        var service = new ManualLootService(
            db,
            new DkpLedgerWriter(db, pools, NullLogger<DkpLedgerWriter>.Instance),
            pools,
            new DkpPoolBalanceService(db, pools));

        var result = await service.AddAsync(
            Ls, ManualLootTarget.None, "Ridill", "Alice", 30, null,
            recordedByAppUserId: "officer-1", CancellationToken.None);

        Assert.True(result.Success, result.Error);
        Assert.Equal("officer-1", (await db.EventLootDetails.SingleAsync()).RecordedByAppUserId);
    }

    private static string AddAddonLootBody()
    {
        var text = File.ReadAllText(Path.Combine(FindRepoPath("Controllers"), "AddonApiController.AddonParticipation.cs"));
        var from = text.IndexOf("private async Task<IActionResult> AddAddonLootAsync(", StringComparison.Ordinal);
        var to = text.IndexOf("public async Task<IActionResult> PostTodAsync(", from + 1, StringComparison.Ordinal);
        Assert.True(from >= 0 && to > from, "AddAddonLootAsync not found.");
        return text[from..to];
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
