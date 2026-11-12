using LinkshellManagerDiscordApp.Models;
using Xunit;

namespace LinkshellManager.Tests;

// Pins the Leader rank predicate (the owner always passes permission checks).
// A regression here silently grants or denies access.
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
}
