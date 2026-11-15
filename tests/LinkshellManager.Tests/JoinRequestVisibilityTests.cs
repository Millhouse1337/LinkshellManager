using System;
using System.Collections.Generic;
using System.Linq;
using LinkshellManagerDiscordApp.Controllers;
using LinkshellManagerDiscordApp.Models;
using LinkshellManagerDiscordApp.Services;
using Xunit;

namespace LinkshellManager.Tests;

// Who the overview sends a linkshell's open join requests to. It must match who the approve and
// deny routes let through ("Manage invites", the Leader always), so a plain member never sees a
// player's name and note, and an officer never sees buttons that answer 403.
public class JoinRequestVisibilityTests
{
    private const int LinkshellId = 7;

    private static Dictionary<int, Dictionary<string, LinkshellRole>> Roles(Action<List<LinkshellRole>>? change = null)
    {
        var roles = LinkshellRoleDefaults.BuildDefaultRoles(LinkshellId).ToList();
        change?.Invoke(roles);
        return new Dictionary<int, Dictionary<string, LinkshellRole>>
        {
            [LinkshellId] = roles.ToDictionary(r => r.Name, StringComparer.OrdinalIgnoreCase),
        };
    }

    [Theory]
    [InlineData("Leader", true)]
    [InlineData("Officer", true)]
    [InlineData("Member", false)]
    [InlineData("Trial", false)]
    [InlineData(null, false)]
    [InlineData("Unknown custom role", false)] // falls back to Member
    public void DefaultRoles(string? rank, bool sees)
        => Assert.Equal(sees, ActivityMappers.AnswersJoinRequests(LinkshellId, rank, Roles(), adminOverrideActive: false));

    [Fact]
    public void AnOfficerWithoutManageInvites_SeesNone()
    {
        var roles = Roles(list => list.Single(r => r.Name == LinkshellRanks.Officer).CanManageInvites = false);
        Assert.False(ActivityMappers.AnswersJoinRequests(LinkshellId, "Officer", roles, adminOverrideActive: false));
    }

    [Fact]
    public void ACustomRoleWithManageInvites_SeesThem()
    {
        var roles = Roles(list => list.Add(new LinkshellRole { LinkshellId = LinkshellId, Name = "Recruiter", CanManageInvites = true }));
        Assert.True(ActivityMappers.AnswersJoinRequests(LinkshellId, "Recruiter", roles, adminOverrideActive: false));
    }

    [Fact]
    public void TheLeader_SeesThemEvenIfTheirRoleRowLostTheFlag()
    {
        var roles = Roles(list => list.Single(r => r.Name == LinkshellRanks.Leader).CanManageInvites = false);
        Assert.True(ActivityMappers.AnswersJoinRequests(LinkshellId, "Leader", roles, adminOverrideActive: false));
    }

    [Fact]
    public void TheAdminOverride_SeesThem()
        => Assert.True(ActivityMappers.AnswersJoinRequests(LinkshellId, "Member", Roles(), adminOverrideActive: true));

    // ---- role id (RosterSwitch): the overview must read the role the same way enforcement does ----

    // Default roles with ids, so a membership can point at one (AppUserLinkshell.LinkshellRoleId).
    private static Dictionary<int, Dictionary<string, LinkshellRole>> RolesWithIds(Action<List<LinkshellRole>>? change = null)
        => Roles(list =>
        {
            for (var i = 0; i < list.Count; i++)
            {
                list[i].Id = 100 + i;
            }
            change?.Invoke(list);
        });

    // For every default role, an id stamped from the Rank gives exactly the rank's answer.
    [Theory]
    [InlineData("Leader")]
    [InlineData("Officer")]
    [InlineData("Member")]
    [InlineData("Trial")]
    public void ARoleIdStampedFromTheRank_GivesTheSamePermissions(string rank)
    {
        var roles = RolesWithIds();
        var roleId = roles[LinkshellId][rank].Id;

        Assert.Equal(
            ActivityMappers.ResolvePermissionsFor(LinkshellId, rank, roles, adminOverrideActive: false),
            ActivityMappers.ResolvePermissionsFor(LinkshellId, rank, roles, adminOverrideActive: false, roleId));
        Assert.Equal(
            ActivityMappers.AnswersJoinRequests(LinkshellId, rank, roles, adminOverrideActive: false),
            ActivityMappers.AnswersJoinRequests(LinkshellId, rank, roles, adminOverrideActive: false, roleId));
    }

    // A custom role renamed in Data Admin: the holder's Rank still says the old name, but their id
    // still names the role, so the overview shows its rights, as LinkshellPermissions enforces.
    [Fact]
    public void ARenamedRole_KeepsItsHoldersRights_ByTheirRoleId()
    {
        var roles = RolesWithIds(list => list.Add(
            new LinkshellRole { Id = 500, LinkshellId = LinkshellId, Name = "Greeter", CanManageInvites = true }));

        Assert.False(ActivityMappers.AnswersJoinRequests(LinkshellId, "Recruiter", roles, adminOverrideActive: false));
        Assert.True(ActivityMappers.AnswersJoinRequests(LinkshellId, "Recruiter", roles, adminOverrideActive: false, roleId: 500));
        Assert.True(ActivityMappers.ResolvePermissionsFor(LinkshellId, "Recruiter", roles, adminOverrideActive: false, roleId: 500)?.CanManageInvites);
    }

    // An id that is not one of this linkshell's roles is ignored: the rank decides, as before.
    [Fact]
    public void ARoleIdThatIsNotThisLinkshells_FallsBackToTheRank()
    {
        var roles = RolesWithIds();

        Assert.True(ActivityMappers.AnswersJoinRequests(LinkshellId, "Officer", roles, adminOverrideActive: false, roleId: 9999));
        Assert.False(ActivityMappers.AnswersJoinRequests(LinkshellId, "Member", roles, adminOverrideActive: false, roleId: 9999));
    }

    // The Leader rule still comes first, whatever role the id names.
    [Fact]
    public void TheLeader_SeesThem_EvenWhenTheirRoleIdNamesAPlainRole()
    {
        var roles = RolesWithIds();
        var memberRoleId = roles[LinkshellId][LinkshellRanks.Member].Id;

        Assert.True(ActivityMappers.AnswersJoinRequests(LinkshellId, "Leader", roles, adminOverrideActive: false, memberRoleId));
    }
}
