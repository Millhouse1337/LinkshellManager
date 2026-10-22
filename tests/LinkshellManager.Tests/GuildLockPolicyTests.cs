using LinkshellManagerDiscordApp.Authorization;
using Xunit;

namespace LinkshellManager.Tests;

// The Discord server lock only governs the Activity. The website (no bearer token) sees
// every linkshell the user belongs to, except where a request would let them JOIN a locked
// linkshell (lockAppliesOnWebsite), and a bearer that failed to resolve never sneaks through.
public class GuildLockPolicyTests
{
    private const string Guild = "123456789012345678";

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void UnlockedLinkshell_IsAlwaysAllowed(string? lockedGuildId)
    {
        Assert.Equal(GuildLockDecision.Allow, GuildLockPolicy.Decide(lockedGuildId, hasBearerToken: false, bearerTokenResolved: false, lockAppliesOnWebsite: true));
        Assert.Equal(GuildLockDecision.Allow, GuildLockPolicy.Decide(lockedGuildId, hasBearerToken: true, bearerTokenResolved: false, lockAppliesOnWebsite: false));
    }

    [Fact]
    public void Website_IgnoresTheLock()
    {
        Assert.Equal(GuildLockDecision.Allow, GuildLockPolicy.Decide(Guild, hasBearerToken: false, bearerTokenResolved: false, lockAppliesOnWebsite: false));
    }

    [Fact]
    public void Website_KeepsTheLock_WhenJoining()
    {
        Assert.Equal(GuildLockDecision.Deny, GuildLockPolicy.Decide(Guild, hasBearerToken: false, bearerTokenResolved: false, lockAppliesOnWebsite: true));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Activity_WithResolvedToken_VerifiesWithDiscord(bool lockAppliesOnWebsite)
    {
        Assert.Equal(GuildLockDecision.VerifyWithDiscord, GuildLockPolicy.Decide(Guild, hasBearerToken: true, bearerTokenResolved: true, lockAppliesOnWebsite));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Activity_WithUnresolvedToken_FailsClosed(bool lockAppliesOnWebsite)
    {
        Assert.Equal(GuildLockDecision.Deny, GuildLockPolicy.Decide(Guild, hasBearerToken: true, bearerTokenResolved: false, lockAppliesOnWebsite));
    }
}
