using LinkshellManagerDiscordApp.Authorization;
using LinkshellManagerDiscordApp.Data;
using LinkshellManagerDiscordApp.Models;
using LinkshellManagerDiscordApp.Services;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;

namespace LinkshellManagerDiscordApp.Controllers;

// What every Activity controller shares: who is asking (ResolveAppUserAsync), their membership of a
// linkshell with the Discord guild lock applied (GetMembershipAsync), and the permission check
// (CanAsync). Every Activity controller inherits this, so these rules stay in one place. See
// "Activity API controllers" in CONTRIBUTING.md for which controller owns what.
//
// Only what every controller needs lives here. A helper used by one controller stays in it.
// Nothing here is public: a public method on a controller base class would become an endpoint.
// The routing attributes ([ApiController], [Route("api/activity")]) stay on each controller.
public abstract class ActivityControllerBase : ControllerBase
{
    private protected const string PendingInviteStatus = "PendingInvite";
    private protected const string PendingJoinRequestStatus = "PendingJoinRequest";

    private protected readonly ApplicationDbContext _dbContext;
    private protected readonly DiscordIdentityService _discordIdentityService;
    private protected readonly UserManager<AppUser> _userManager;
    private protected readonly ILogger _logger;
    private protected readonly LinkshellPermissions _permissions;

    private protected ActivityControllerBase(
        ApplicationDbContext dbContext,
        DiscordIdentityService discordIdentityService,
        UserManager<AppUser> userManager,
        ILogger logger,
        LinkshellPermissions permissions)
    {
        _dbContext = dbContext;
        _discordIdentityService = discordIdentityService;
        _userManager = userManager;
        _logger = logger;
        _permissions = permissions;
    }

    // The Discord bearer token of the current request, captured once the bearer
    // path validates. Only the Activity (bearer) path has a Discord token; the
    // cookie/web path leaves this null. Used by the per-linkshell guild lock to
    // verify membership against discord.com. The controller is request-scoped,
    // so a field is safe here.
    private string? _resolvedDiscordAccessToken;

    // Who is asking, read from the signed-in principal: the DiscordBearer authentication handler's
    // for a bearer, the website cookie's otherwise. A bearer Discord turns down is signed out, never
    // downgraded to the cookie (ApiSignIn.ResolveCallerAsync explains why). Null = signed out; each
    // action answers that with its own 401 message. cancellationToken is not needed any more: the
    // handler uses the request's own abort token, the same one MVC binds to it.
    private protected async Task<AppUser?> ResolveAppUserAsync(CancellationToken cancellationToken)
    {
        var caller = await ApiSignIn.ResolveCallerAsync(HttpContext, _userManager);
        if (caller.DiscordAccessToken is not null)
        {
            _resolvedDiscordAccessToken = caller.DiscordAccessToken;
        }

        return caller.AppUser;
    }

    private protected bool TryGetBearerToken(out string accessToken) =>
        ApiSignIn.TryGetBearerToken(Request, out accessToken);

    // The Discord guild (server) this request came from, sent by the Activity
    // as X-Discord-Guild-Id. Null on the website (no Discord context) and when
    // launched outside a server. Used to enforce per-linkshell guild locks.
    private protected string? GetRequestGuildId()
    {
        var value = Request.Headers["X-Discord-Guild-Id"].ToString();
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    // A linkshell locked to a guild is only accessible from the Activity when
    // launched from that guild. Returns true when the lock blocks this request.
    // The website (no guild header) is intentionally unaffected — the lock only
    // governs the Discord Activity, so a request with no guild id passes.
    private protected bool IsBlockedByGuildLock(Linkshell? linkshell)
    {
        // Only an explicitly *locked* linkshell restricts access. Merely having a
        // server set (LockToDiscordGuild == false) never blocks anyone.
        if (linkshell is null || !linkshell.LockToDiscordGuild || string.IsNullOrWhiteSpace(linkshell.DiscordGuildId))
        {
            return false;
        }

        var requestGuildId = GetRequestGuildId();
        // No guild context (website / non-Discord) → not blocked. Activity
        // requests always carry the header, so a mismatch here means the
        // Activity is open in a different server than the linkshell is tied to.
        if (requestGuildId is null)
        {
            return false;
        }

        return !string.Equals(requestGuildId, linkshell.DiscordGuildId, StringComparison.Ordinal);
    }

    private protected async Task<AppUserLinkshell?> GetMembershipAsync(string appUserId, int linkshellId, CancellationToken cancellationToken)
    {
        var membership = await _permissions.GetMembershipAsync(appUserId, linkshellId, cancellationToken);

        if (membership is null)
        {
            return null;
        }

        // Enforce the per-linkshell Discord guild lock for every endpoint that
        // resolves access through GetMembershipAsync. Only applies when the
        // linkshell is *locked* (a set-but-unlocked server doesn't gate access),
        // and only inside the Activity — the website sees all its memberships.
        // A locked linkshell the user can't prove membership in is treated as
        // "no access" (the caller maps a null membership to Forbid()/403).
        var guildId = membership.Linkshell?.LockToDiscordGuild == true ? membership.Linkshell?.DiscordGuildId : null;
        if (!string.IsNullOrWhiteSpace(guildId))
        {
            var allowed = await FilterAccessibleLinkshellIdsAsync(
                new[] { (linkshellId, (string?)guildId) }, cancellationToken);
            if (!allowed.Contains(linkshellId))
            {
                return null;
            }
        }

        return membership;
    }

    // Given candidate linkshells (id + the guild they're locked to), returns the
    // subset the current request is allowed to access (see GuildLockPolicy):
    //   * no DiscordGuildId  -> always allowed (unlocked; never calls Discord)
    //   * locked + website (cookie, no bearer) -> allowed, unless
    //     lockAppliesOnWebsite (the request would JOIN the linkshell)
    //   * locked + bearer token + verified member -> allowed
    //   * locked + unresolved bearer or non-member -> excluded
    // Fail-closed: if the membership check can't reach Discord, the locked
    // linkshell is excluded rather than 500-ing the whole request, so unlocked
    // linkshells keep working during a Discord outage.
    private protected async Task<HashSet<int>> FilterAccessibleLinkshellIdsAsync(
        IReadOnlyCollection<(int LinkshellId, string? DiscordGuildId)> candidates,
        CancellationToken cancellationToken,
        bool lockAppliesOnWebsite = false)
    {
        var hasBearerToken = TryGetBearerToken(out _);
        var bearerTokenResolved = !string.IsNullOrWhiteSpace(_resolvedDiscordAccessToken);
        var allowed = new HashSet<int>();
        foreach (var candidate in candidates)
        {
            var decision = GuildLockPolicy.Decide(
                candidate.DiscordGuildId, hasBearerToken, bearerTokenResolved, lockAppliesOnWebsite);
            if (decision == GuildLockDecision.Allow)
            {
                allowed.Add(candidate.LinkshellId);
                continue;
            }

            if (decision == GuildLockDecision.Deny)
            {
                continue;
            }

            try
            {
                // VerifyWithDiscord is only returned once the bearer token resolved.
                if (await _discordIdentityService.VerifyGuildMembershipAsync(
                        _resolvedDiscordAccessToken!, candidate.DiscordGuildId!, cancellationToken))
                {
                    allowed.Add(candidate.LinkshellId);
                }
            }
            catch (DiscordApiException ex)
            {
                _logger.LogWarning(
                    ex,
                    "Guild membership check failed for linkshell {LinkshellId}; denying access (fail-closed).",
                    candidate.LinkshellId);
            }
        }

        return allowed;
    }

    // The permission rule itself lives in LinkshellPermissions (one copy, so the addon and
    // Discord paths can share it). These wrappers keep the controller's call sites as they
    // were. The guild lock is NOT in the service: GetMembershipAsync above applies it first.
    private protected Task<bool> CanAsync(
        AppUserLinkshell? membership,
        Func<LinkshellRole, bool> selector,
        CancellationToken cancellationToken)
        => _permissions.CanAsync(membership, selector, cancellationToken);

    private protected Task<List<LinkshellRole>> EnsureDefaultRolesAsync(int linkshellId, CancellationToken cancellationToken)
        => _permissions.EnsureDefaultRolesAsync(linkshellId, cancellationToken);

    private protected Task<Dictionary<int, List<LinkshellRole>>> EnsureDefaultRolesForLinkshellsAsync(
        IReadOnlyCollection<int> linkshellIds,
        CancellationToken cancellationToken)
        => _permissions.EnsureDefaultRolesForLinkshellsAsync(linkshellIds, cancellationToken);

    private protected static bool IsLeader(AppUserLinkshell? membership)
    {
        return membership?.Rank?.Equals(LinkshellRanks.Leader, StringComparison.OrdinalIgnoreCase) == true;
    }
}
