namespace LinkshellManagerDiscordApp.Authorization;

public enum GuildLockDecision
{
    Allow,
    Deny,
    VerifyWithDiscord
}

// What the per-linkshell Discord server lock (Linkshell.LockToDiscordGuild) means for one
// request. The lock governs the Discord Activity: there, a locked linkshell is only reachable
// by someone who proves with their Discord token that they are in its server.
//
// The website has no Discord token and was never subject to the lock, so a cookie request
// sees every linkshell the user already belongs to. The one exception is a request that
// reaches *into* a linkshell the user is not yet part of (Claim: taking over a placeholder
// membership). That stays closed on the website, or anyone with a matching character name
// could join a locked linkshell without being in its server.
//
// Pure, so the matrix is pinned by GuildLockPolicyTests without a controller.
public static class GuildLockPolicy
{
    public static GuildLockDecision Decide(
        string? lockedGuildId,
        bool hasBearerToken,
        bool bearerTokenResolved,
        bool lockAppliesOnWebsite)
    {
        if (string.IsNullOrWhiteSpace(lockedGuildId))
        {
            return GuildLockDecision.Allow;
        }

        // No bearer header = the website (cookie login).
        if (!hasBearerToken)
        {
            return lockAppliesOnWebsite ? GuildLockDecision.Deny : GuildLockDecision.Allow;
        }

        // A bearer that did not resolve to a user never falls back to "website" — fail closed.
        return bearerTokenResolved ? GuildLockDecision.VerifyWithDiscord : GuildLockDecision.Deny;
    }
}
