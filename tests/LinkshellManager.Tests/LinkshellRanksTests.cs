using LinkshellManagerDiscordApp.Models;
using Xunit;

namespace LinkshellManager.Tests;

// Pins the rank predicates that gate "can manage this linkshell" across the
// controllers. A regression here silently grants or denies access.
public class LinkshellRanksTests
{
    [Theory]
    [InlineData("Leader", true)]
    [InlineData("leader", true)]   // case-insensitive
    [InlineData("Officer", false)]
    [InlineData("Member", false)]
    [InlineData(null, false)]
    [InlineData("", false)]
    public void IsLeader(string? rank, bool expected)
        => Assert.Equal(expected, LinkshellRanks.IsLeader(rank));

    [Theory]
    [InlineData("Leader", true)]
    [InlineData("Officer", true)]
    [InlineData("officer", true)]  // case-insensitive
    [InlineData("Member", false)]
    [InlineData(null, false)]
    [InlineData("   ", false)]
    [InlineData("Leadership", false)]  // not a prefix match
    public void IsLeaderOrOfficer(string? rank, bool expected)
        => Assert.Equal(expected, LinkshellRanks.IsLeaderOrOfficer(rank));
}
