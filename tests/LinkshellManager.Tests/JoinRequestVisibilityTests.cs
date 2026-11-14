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
}
