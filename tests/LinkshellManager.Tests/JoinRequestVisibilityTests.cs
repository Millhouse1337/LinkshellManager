using System;
using System.Collections.Generic;
using System.Linq;
using LinkshellManagerDiscordApp.Controllers;
using LinkshellManagerDiscordApp.Models;
using LinkshellManagerDiscordApp.Services;
using Microsoft.Extensions.Logging;
using Xunit;

namespace LinkshellManager.Tests;

// Who the overview sends a linkshell's open join requests to. It must match who the approve and
// deny routes let through ("Manage invites", the Leader always), so a plain member never sees a
// player's name and note, and an officer never sees buttons that answer 403.
//
// Since R5.2 the overview reads the member's role by id only (AppUserLinkshell.LinkshellRoleId),
// like LinkshellPermissions; the Rank text is read only for the Leader rule.
public class JoinRequestVisibilityTests
{
    private const int LinkshellId = 7;

    // The default roles with ids (100, 101, ...), keyed by id the way the overview builds its map.
    private static Dictionary<int, Dictionary<int, LinkshellRole>> Roles(Action<List<LinkshellRole>>? change = null)
    {
        var roles = LinkshellRoleDefaults.BuildDefaultRoles(LinkshellId).ToList();
        for (var i = 0; i < roles.Count; i++)
        {
            roles[i].Id = 100 + i;
        }
        change?.Invoke(roles);
        return new Dictionary<int, Dictionary<int, LinkshellRole>>
        {
            [LinkshellId] = roles.ToDictionary(r => r.Id),
        };
    }

    private static int IdOf(Dictionary<int, Dictionary<int, LinkshellRole>> roles, string name)
        => roles[LinkshellId].Values.Single(r => r.Name == name).Id;

    // A member holding a role: the id names it and Rank is its name, as every save leaves them.
    private static bool Sees(Dictionary<int, Dictionary<int, LinkshellRole>> roles, string role, bool adminOverrideActive = false)
        => ActivityMappers.AnswersJoinRequests(LinkshellId, role, IdOf(roles, role), roles, adminOverrideActive);

    [Theory]
    [InlineData("Leader", true)]
    [InlineData("Officer", true)]
    [InlineData("Member", false)]
    [InlineData("Trial", false)]
    public void DefaultRoles(string role, bool sees)
        => Assert.Equal(sees, Sees(Roles(), role));

    [Fact]
    public void AnOfficerWithoutManageInvites_SeesNone()
    {
        var roles = Roles(list => list.Single(r => r.Name == LinkshellRanks.Officer).CanManageInvites = false);
        Assert.False(Sees(roles, LinkshellRanks.Officer));
    }

    [Fact]
    public void ACustomRoleWithManageInvites_SeesThem()
    {
        var roles = Roles(list => list.Add(new LinkshellRole { Id = 500, LinkshellId = LinkshellId, Name = "Recruiter", CanManageInvites = true }));
        Assert.True(Sees(roles, "Recruiter"));
    }

    [Fact]
    public void TheLeader_SeesThemEvenIfTheirRoleRowLostTheFlag()
    {
        var roles = Roles(list => list.Single(r => r.Name == LinkshellRanks.Leader).CanManageInvites = false);
        Assert.True(Sees(roles, LinkshellRanks.Leader));
    }

    [Fact]
    public void TheAdminOverride_SeesThem()
        => Assert.True(Sees(Roles(), LinkshellRanks.Member, adminOverrideActive: true));

    // A custom role renamed in Data Admin: whatever Rank text the member was read with, their id
    // still names the role, so the overview shows its rights, as LinkshellPermissions enforces.
    [Fact]
    public void ARenamedRole_KeepsItsHoldersRights_ByTheirRoleId()
    {
        var roles = Roles(list => list.Add(
            new LinkshellRole { Id = 500, LinkshellId = LinkshellId, Name = "Greeter", CanManageInvites = true }));

        Assert.True(ActivityMappers.AnswersJoinRequests(LinkshellId, "Recruiter", 500, roles, adminOverrideActive: false));
        Assert.True(ActivityMappers.ResolvePermissionsFor(LinkshellId, 500, roles, adminOverrideActive: false)?.CanManageInvites);
    }

    // The Rank text no longer picks the role: a member read with a stale "Officer" Rank whose id
    // names Member gets Member's rights.
    [Fact]
    public void TheRankText_DoesNotPickTheRole()
    {
        var roles = Roles();
        Assert.False(ActivityMappers.AnswersJoinRequests(LinkshellId, LinkshellRanks.Officer, IdOf(roles, LinkshellRanks.Member), roles, adminOverrideActive: false));
    }

    // An id that is none of this linkshell's roles, or no id at all (should never happen: the
    // column is NOT NULL and the foreign key keeps it in the linkshell): the Member role's rights,
    // never the rights the Rank text names, and a warning in the log.
    [Theory]
    [InlineData(9999)]
    [InlineData(null)]
    public void AnUnknownOrMissingRoleId_GetsMemberRights_AndIsLogged(int? roleId)
    {
        var roles = Roles();
        var logger = new CapturingLogger();

        var permissions = ActivityMappers.ResolvePermissionsFor(LinkshellId, roleId, roles, adminOverrideActive: false, logger);

        Assert.Equal(ActivityMappers.ResolvePermissionsFor(LinkshellId, IdOf(roles, LinkshellRanks.Member), roles, adminOverrideActive: false), permissions);
        Assert.False(ActivityMappers.AnswersJoinRequests(LinkshellId, LinkshellRanks.Officer, roleId, roles, adminOverrideActive: false));
        var warning = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Warning, warning.Level);
        Assert.Contains("Member role", warning.Message, StringComparison.Ordinal);
    }

    // The Leader rule still comes first, whatever role the id names.
    [Fact]
    public void TheLeader_SeesThem_EvenWhenTheirRoleIdNamesAPlainRole()
    {
        var roles = Roles();
        Assert.True(ActivityMappers.AnswersJoinRequests(LinkshellId, LinkshellRanks.Leader, IdOf(roles, LinkshellRanks.Member), roles, adminOverrideActive: false));
    }

    private sealed class CapturingLogger : ILogger
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Entries.Add((logLevel, formatter(state, exception)));
    }
}
