using System.Linq;
using LinkshellManagerDiscordApp.Models;
using LinkshellManagerDiscordApp.Services;
using Xunit;

namespace LinkshellManager.Tests;

// Pins what each built-in rank can do out of the box.
//
// These defaults are only applied when a role row is CREATED — EnsureDefaultRolesForLinkshellsAsync
// inserts missing roles and never back-fills a new column onto an existing one. So a change here
// silently applies to new linkshells only, and needs a paired data migration for the rest.
public class LinkshellRoleDefaultsTests
{
    private static LinkshellRole Role(string name) =>
        LinkshellRoleDefaults.BuildDefaultRoles(linkshellId: 1).Single(role => role.Name == name);

    // Charts is a shared operational board like ToDs / Events / Rules — officers get it. It is not a
    // money surface like Treasury, where withholding by default is the point.
    [Fact]
    public void Officer_CanManageCharts()
    {
        Assert.True(Role(LinkshellRanks.Officer).CanManageCharts);
        Assert.False(Role(LinkshellRanks.Officer).CanManageTreasury);
    }

    [Fact]
    public void Leader_CanManageCharts()
    {
        Assert.True(Role(LinkshellRanks.Leader).CanManageCharts);
    }

    [Fact]
    public void Member_CannotManageCharts()
    {
        Assert.False(Role(LinkshellRanks.Member).CanManageCharts);
    }

    [Fact]
    public void Trial_CannotManageCharts()
    {
        Assert.False(Role(LinkshellRanks.Trial).CanManageCharts);
    }

    // The leader owns the linkshell; a stale or mis-edited role row must never be the reason they
    // cannot do something. CanAsync also short-circuits for Leader, but the seeded row should agree
    // with that rather than contradict it in the Permissions editor.
    [Fact]
    public void Leader_HasEveryManagementPermission()
    {
        var leader = Role(LinkshellRanks.Leader);

        Assert.True(leader.CanManageRoles);
        Assert.True(leader.CanManageMembers);
        Assert.True(leader.CanManageEvents);
        Assert.True(leader.CanManageInventory);
        Assert.True(leader.CanManageCharts);
        Assert.True(leader.CanManageTreasury);
        Assert.True(leader.CanCustomizeLinkshell);
    }
}
