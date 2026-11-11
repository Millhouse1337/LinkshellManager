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

    private static AppUserLinkshell Member(string rank) =>
        new() { LinkshellId = Ls, AppUserId = "poster", CharacterName = "Poster", Rank = rank };

    // ---- The rule itself.

    [Theory]
    [InlineData(LinkshellRanks.Leader, true)]
    [InlineData(LinkshellRanks.Officer, true)]
    [InlineData(LinkshellRanks.Member, false)]
    [InlineData(LinkshellRanks.Trial, false)]
    public void DefaultRoles_PostAddonLoot(string rank, bool expected)
    {
        var role = LinkshellRoleDefaults.BuildDefaultRoles(linkshellId: Ls).Single(r => r.Name == rank);

        Assert.Equal(expected, AddonApiController.CanPostAddonLoot(Member(rank), role));
    }

    [Fact]
    public void NotAMemberAnyMore_IsRefused_EvenWithALootRole()
    {
        var role = new LinkshellRole { LinkshellId = Ls, Name = "Officer", CanAddLoot = true };

        Assert.False(AddonApiController.CanPostAddonLoot(null, role));
    }

    [Fact]
    public void Leader_PassesEvenWithAStaleRoleRow()
    {
        var staleRole = new LinkshellRole { LinkshellId = Ls, Name = LinkshellRanks.Leader };

        Assert.True(AddonApiController.CanPostAddonLoot(Member(LinkshellRanks.Leader), staleRole));
        Assert.True(AddonApiController.CanPostAddonLoot(Member(LinkshellRanks.Leader), null));
    }

    [Fact]
    public void EitherLootRight_IsEnough()
    {
        var addLootOnly = new LinkshellRole { LinkshellId = Ls, Name = "Looter", CanAddLoot = true };
        var addonOfficerOnly = new LinkshellRole { LinkshellId = Ls, Name = "Camp", CanManageAttendanceWithAddon = true };
        var neither = new LinkshellRole { LinkshellId = Ls, Name = "Plain", CanUseAddon = true };

        Assert.True(AddonApiController.CanPostAddonLoot(Member("Looter"), addLootOnly));
        Assert.True(AddonApiController.CanPostAddonLoot(Member("Camp"), addonOfficerOnly));
        Assert.False(AddonApiController.CanPostAddonLoot(Member("Plain"), neither));
        Assert.False(AddonApiController.CanPostAddonLoot(Member("Plain"), null));
    }

    // ---- Wiring. Reflection can't prove a call site, so pin the shape (precedent:
    // TodEditPermissionTests): the gate runs before anything is written, and the affordability
    // reply no longer prints the winner's available DKP.

    [Fact]
    public void AddAddonLoot_ChecksThePermissionBeforeWriting()
    {
        var body = AddAddonLootBody();

        var gate = body.IndexOf("CanPostAddonLoot(issuerMembership, issuerRole)", StringComparison.Ordinal);
        var write = body.IndexOf("manualLoot.AddAsync(", StringComparison.Ordinal);
        Assert.True(gate >= 0, "AddAddonLootAsync no longer checks CanPostAddonLoot.");
        Assert.True(write > gate, "The permission check must run before the loot is written.");
        Assert.Contains("GetTokenIssuerRoleAsync(token, token.LinkshellId", body);
        Assert.Contains("Status403Forbidden", body);
        Assert.Contains("recordedByAppUserId: token.IssuedToAppUserId", body);
    }

    [Fact]
    public void AddAddonLoot_DoesNotReadOutTheWinnersBalance()
    {
        var body = AddAddonLootBody();

        Assert.DoesNotContain("{availableDkp", body);
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
        var to = text.IndexOf("internal static bool CanPostAddonLoot(", from + 1, StringComparison.Ordinal);
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
