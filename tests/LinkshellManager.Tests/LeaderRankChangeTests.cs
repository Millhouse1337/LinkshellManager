using LinkshellManagerDiscordApp.Controllers;
using Xunit;

namespace LinkshellManager.Tests;

// Pins the Leader-rank rail on the member role change endpoint. "Manage roles" alone
// used to let an officer crown a second Leader and then demote the real one; only a
// Leader (or an active admin override) may now touch the Leader rank.
public class LeaderRankChangeTests
{
    [Theory]
    [InlineData("Officer", "Member", "Leader")]   // officer crowns someone
    [InlineData("Officer", "Officer", "Leader")]  // officer crowns another officer (or an alt)
    [InlineData("Officer", "Leader", "Officer")]  // officer demotes the Leader
    [InlineData("Officer", "Leader", "Member")]
    [InlineData("Member", "Leader", "Trial")]     // a custom role with Manage roles is no different
    [InlineData(null, "member", "leader")]        // case doesn't open a gap
    public void NonLeader_CannotGrantOrChangeLeader(string? actor, string target, string newRank)
        => Assert.False(ActivityDataController.CanChangeRankTouchingLeader(actor, false, target, newRank));

    [Theory]
    [InlineData("Member", "Leader")]
    [InlineData("Leader", "Officer")]   // a co-Leader can be stepped down
    public void Leader_CanGrantOrChangeLeader(string target, string newRank)
        => Assert.True(ActivityDataController.CanChangeRankTouchingLeader("Leader", false, target, newRank));

    [Theory]
    [InlineData("Member", "Leader")]
    [InlineData("Leader", "Officer")]
    public void AdminOverride_CountsAsLeader(string target, string newRank)
        => Assert.True(ActivityDataController.CanChangeRankTouchingLeader("Officer", true, target, newRank));

    [Theory]
    [InlineData("Member", "Officer")]
    [InlineData("Officer", "Member")]
    [InlineData("Trial", "Member")]
    [InlineData(null, "Officer")]
    public void OrdinaryRoleChanges_AreLeftToManageRoles(string? target, string newRank)
        => Assert.True(ActivityDataController.CanChangeRankTouchingLeader("Officer", false, target, newRank));
}
