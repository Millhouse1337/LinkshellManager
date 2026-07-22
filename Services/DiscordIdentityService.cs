using System.ComponentModel.DataAnnotations;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using LinkshellManagerDiscordApp.Data;
using LinkshellManagerDiscordApp.Models;
using LinkshellManagerDiscordApp.Options;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Npgsql;

namespace LinkshellManagerDiscordApp.Services;

public sealed class DiscordIdentityService
{
    private static readonly Uri TokenEndpoint = new("https://discord.com/api/oauth2/token");
    private static readonly Uri OAuth2MeEndpoint = new("https://discord.com/api/oauth2/@me");
    private static readonly Uri DiscordCdnBaseUri = new("https://cdn.discordapp.com/");

    // How long a validated bearer-token -> local-user resolution is trusted
    // before we re-validate against discord.com. Short enough that a revoked
    // token stops working quickly; long enough to spare the per-request round
    // trip + LastSeen write during normal Activity polling.
    private static readonly TimeSpan TokenValidationTtl = TimeSpan.FromSeconds(90);

    // How long a guild-membership verdict (member / not-member) is trusted
    // before we re-check against discord.com. Short, because the Activity
    // read path can ask about several locked linkshells per poll — caching
    // both the positive and negative result keeps us well clear of Discord's
    // rate limits while a freshly joined member only waits up to a minute.
    private static readonly TimeSpan GuildMembershipTtl = TimeSpan.FromSeconds(60);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ApplicationDbContext _dbContext;
    private readonly UserManager<AppUser> _userManager;
    private readonly DiscordOAuthOptions _options;
    private readonly IMemoryCache _cache;
    private readonly ILogger<DiscordIdentityService> _logger;

    public DiscordIdentityService(
        IHttpClientFactory httpClientFactory,
        ApplicationDbContext dbContext,
        UserManager<AppUser> userManager,
        IOptions<DiscordOAuthOptions> options,
        IMemoryCache cache,
        ILogger<DiscordIdentityService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _dbContext = dbContext;
        _userManager = userManager;
        _options = options.Value;
        _cache = cache;
        _logger = logger;
    }

    public bool IsConfigured(out string issues)
    {
        var context = new ValidationContext(_options);
        var results = new List<ValidationResult>();
        var valid = Validator.TryValidateObject(_options, context, results, validateAllProperties: true);
        issues = string.Join("; ", results.Select(result => result.ErrorMessage));
        return valid;
    }

    public async Task<DiscordExchangeResult> ExchangeCodeAsync(string code, CancellationToken cancellationToken)
    {
        EnsureConfigured();

        var token = await ExchangeForTokenAsync(code, cancellationToken);
        var discordUser = await GetCurrentDiscordUserAsync(token.AccessToken, cancellationToken);
        var localUser = await UpsertLocalUserAsync(discordUser, preferredAppUser: null, cancellationToken);

        return new DiscordExchangeResult(
            token.AccessToken,
            token.TokenType,
            token.ExpiresIn,
            token.Scope,
            localUser);
    }

    public async Task<DiscordActivityUserDto> GetCurrentLocalUserAsync(string accessToken, CancellationToken cancellationToken)
    {
        EnsureConfigured();

        var discordUser = await GetCurrentDiscordUserAsync(accessToken, cancellationToken);
        return await UpsertLocalUserAsync(discordUser, preferredAppUser: null, cancellationToken);
    }

    private static string BuildTokenCacheKey(string accessToken)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(accessToken));
        return "discord-token-profile:" + Convert.ToHexString(hash);
    }

    public async Task<DiscordWebsiteSignInResult> ResolveWebsiteSignInAsync(
        DiscordIdentityProfile discordUser,
        AppUser? preferredAppUser,
        CancellationToken cancellationToken)
    {
        EnsureConfigured();

        var resolution = await ResolveOrLinkAppUserAsync(discordUser, preferredAppUser, cancellationToken);
        return new DiscordWebsiteSignInResult(
            resolution.AppUser,
            resolution.LocalDiscordUser,
            resolution.IsNewDiscordUser,
            resolution.IsNewAppUser);
    }

    private void EnsureConfigured()
    {
        if (!IsConfigured(out var issues))
        {
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(issues)
                ? "Discord OAuth configuration is incomplete."
                : issues);
        }
    }

    private async Task<DiscordTokenPayload> ExchangeForTokenAsync(string code, CancellationToken cancellationToken)
    {
        var client = _httpClientFactory.CreateClient();
        var form = new Dictionary<string, string>
        {
            ["client_id"] = _options.ClientId,
            ["client_secret"] = _options.ClientSecret,
            ["grant_type"] = "authorization_code",
            ["code"] = code
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, TokenEndpoint)
        {
            Content = new FormUrlEncodedContent(form)
        };
        request.Headers.Accept.ParseAdd("application/json");

        using var response = await client.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            // Don't log Discord's response body — it may include the submitted
            // authorization code, which is a credential.
            _logger.LogWarning("Discord token exchange failed with status {Status}.", response.StatusCode);
            throw new DiscordApiException((int)response.StatusCode, "Discord token exchange failed.");
        }

        var payload = await response.Content.ReadFromJsonAsync<DiscordTokenPayload>(cancellationToken: cancellationToken);
        if (payload is null || string.IsNullOrWhiteSpace(payload.AccessToken))
        {
            throw new DiscordApiException(502, "Discord returned an invalid token payload.");
        }

        return payload;
    }

    // Calls Discord's /oauth2/@me endpoint, which returns the application the
    // token was issued to *and* the authorizing user. We require the token to
    // belong to OUR ClientId before trusting the embedded user identity —
    // otherwise any Discord OAuth token with the `identify` scope (issued to
    // any other Discord application the user has authorized) could be replayed
    // here to impersonate them.
    private async Task<DiscordIdentityProfile> GetCurrentDiscordUserAsync(string accessToken, CancellationToken cancellationToken)
    {
        // Cache the validated Discord identity (the user + the application-id
        // check) keyed by a hash of the token, so repeated authenticated calls
        // reuse it instead of hitting discord.com every time — which under
        // raid-time polling risks rate-limiting the whole linkshell. Local data
        // and LastSeenAtUtc still refresh every request via the upsert below;
        // only successful validations are cached, so a bad token always re-checks.
        var cacheKey = BuildTokenCacheKey(accessToken);
        if (_cache.TryGetValue<DiscordIdentityProfile>(cacheKey, out var cachedProfile) && cachedProfile is not null)
        {
            return cachedProfile;
        }

        var client = _httpClientFactory.CreateClient();

        using var request = new HttpRequestMessage(HttpMethod.Get, OAuth2MeEndpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Headers.Accept.ParseAdd("application/json");

        using var response = await client.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            // Don't log Discord's response body — it may echo token validity hints.
            _logger.LogWarning("Discord oauth2/@me lookup failed with status {Status}.", response.StatusCode);
            throw new DiscordApiException((int)response.StatusCode, "Discord token validation failed.");
        }

        var envelope = await response.Content.ReadFromJsonAsync<DiscordOAuth2MePayload>(cancellationToken: cancellationToken);
        if (envelope is null || envelope.Application is null || envelope.User is null || string.IsNullOrWhiteSpace(envelope.User.Id))
        {
            throw new DiscordApiException(502, "Discord returned an invalid token validation payload.");
        }

        if (!string.Equals(envelope.Application.Id, _options.ClientId, StringComparison.Ordinal))
        {
            _logger.LogWarning(
                "Rejecting Discord access token issued to application {ApplicationId} (expected {ExpectedId}).",
                envelope.Application.Id,
                _options.ClientId);
            throw new DiscordApiException(401, "Access token was issued to a different Discord application.");
        }

        var user = envelope.User;
        var profile = new DiscordIdentityProfile(
            user.Id,
            user.Username,
            user.Discriminator ?? "0",
            user.GlobalName,
            user.Avatar);

        _cache.Set(cacheKey, profile, TokenValidationTtl);
        return profile;
    }

    // Authoritative guild-lock gate. The SDK-provided guildId from the Activity
    // client is untrusted (it's just a value the browser hands us), so we verify
    // membership server-side with the user's OWN bearer token:
    //   GET /users/@me/guilds/{guildId}/member  ->  200 = member, 404 = not.
    // Requires the OAuth `guilds.members.read` scope on the token. Both verdicts
    // are cached (see GuildMembershipTtl); transient/other failures throw so the
    // caller can fail closed without poisoning the cache.
    public async Task<bool> VerifyGuildMembershipAsync(
        string accessToken,
        string guildId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(accessToken) || string.IsNullOrWhiteSpace(guildId))
        {
            return false;
        }

        var cacheKey = BuildGuildMembershipCacheKey(accessToken, guildId);
        if (_cache.TryGetValue<bool>(cacheKey, out var cachedVerdict))
        {
            return cachedVerdict;
        }

        var client = _httpClientFactory.CreateClient();
        var uri = new Uri($"https://discord.com/api/users/@me/guilds/{Uri.EscapeDataString(guildId)}/member");

        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Headers.Accept.ParseAdd("application/json");

        using var response = await client.SendAsync(request, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            _cache.Set(cacheKey, false, GuildMembershipTtl);
            return false;
        }

        if (!response.IsSuccessStatusCode)
        {
            // Don't cache transient failures — re-check on the next request.
            _logger.LogWarning("Discord guild-member lookup failed with status {Status}.", response.StatusCode);
            throw new DiscordApiException((int)response.StatusCode, "Discord guild membership check failed.");
        }

        _cache.Set(cacheKey, true, GuildMembershipTtl);
        return true;
    }

    private static string BuildGuildMembershipCacheKey(string accessToken, string guildId)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(accessToken));
        return $"discord-guild-member:{guildId}:{Convert.ToHexString(hash)}";
    }

    private static readonly TimeSpan BotGuildsTtl = TimeSpan.FromMinutes(5);

    // Lists the guilds the LSM bot itself is in (GET /users/@me/guilds with the
    // bot token). Used by the website's server-lock UI to offer a pick-list of
    // servers the bot can actually gate. Returns null when no bot token is
    // configured or the call fails; cached briefly so an officer page load
    // doesn't hit Discord every time.
    public async Task<IReadOnlyList<DiscordGuildSummary>?> ListBotGuildsAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_options.BotToken))
        {
            return null;
        }

        const string cacheKey = "discord-bot-guilds";
        if (_cache.TryGetValue<IReadOnlyList<DiscordGuildSummary>>(cacheKey, out var cached) && cached is not null)
        {
            return cached;
        }

        try
        {
            var client = _httpClientFactory.CreateClient();
            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri("https://discord.com/api/users/@me/guilds"));
            request.Headers.Authorization = new AuthenticationHeaderValue("Bot", _options.BotToken);
            request.Headers.Accept.ParseAdd("application/json");

            using var response = await client.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Discord bot-guild listing failed with status {Status}.", response.StatusCode);
                return null;
            }

            var payload = await response.Content.ReadFromJsonAsync<List<DiscordGuildSummaryPayload>>(
                cancellationToken: cancellationToken);
            var guilds = (payload ?? new List<DiscordGuildSummaryPayload>())
                .Where(guild => !string.IsNullOrWhiteSpace(guild.Id))
                .Select(guild => new DiscordGuildSummary(guild.Id, guild.Name ?? guild.Id))
                .ToList();
            _cache.Set(cacheKey, (IReadOnlyList<DiscordGuildSummary>)guilds, BotGuildsTtl);
            return guilds;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Unable to list the bot's Discord guilds.");
            return null;
        }
    }

    // Verifies via the BOT token whether a given Discord user is a member of a
    // guild: GET /guilds/{guildId}/members/{userId} -> 200 member, 404 not.
    // The website lock flow uses this so a leader can only lock to a server they
    // are actually in (a successful check also proves the bot is in that guild,
    // since the bot token can't read members of a guild it isn't in). Returns
    // false when unverifiable (no bot token, bot not in guild, transient error).
    public async Task<bool> IsUserInGuildAsync(string guildId, string discordUserId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(guildId)
            || string.IsNullOrWhiteSpace(discordUserId)
            || string.IsNullOrWhiteSpace(_options.BotToken))
        {
            return false;
        }

        var cacheKey = $"discord-bot-guild-member:{guildId}:{discordUserId}";
        if (_cache.TryGetValue<bool>(cacheKey, out var cachedVerdict))
        {
            return cachedVerdict;
        }

        try
        {
            var client = _httpClientFactory.CreateClient();
            var uri = new Uri(
                $"https://discord.com/api/guilds/{Uri.EscapeDataString(guildId)}/members/{Uri.EscapeDataString(discordUserId)}");
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bot", _options.BotToken);
            request.Headers.Accept.ParseAdd("application/json");

            using var response = await client.SendAsync(request, cancellationToken);
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                _cache.Set(cacheKey, false, GuildMembershipTtl);
                return false;
            }
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Discord bot guild-member lookup failed with status {Status}.", response.StatusCode);
                return false;
            }
            _cache.Set(cacheKey, true, GuildMembershipTtl);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Unable to verify guild membership for the website lock flow.");
            return false;
        }
    }

    // How long a successful guild-member roster is reused before we re-fetch,
    // and a much shorter window for a failed fetch (bot not in the server,
    // missing intent, transient error) so a misconfigured server doesn't get
    // hammered yet recovers quickly once fixed.
    private static readonly TimeSpan GuildRosterTtl = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan GuildRosterFailureTtl = TimeSpan.FromSeconds(60);

    // Bot-token roster lookup used by the invite flows to know who is in a
    // linkshell's locked Discord server. Returns the guild's (non-bot) members
    // with display info, or null when the roster can't be determined (no bot
    // token configured, bot not in the server, missing Server Members intent, or
    // an API error) — callers fail open. The authoritative access-time lock
    // (VerifyGuildMembershipAsync) still blocks non-members from seeing data.
    public async Task<IReadOnlyList<DiscordGuildMemberInfo>?> TryGetGuildMembersAsync(
        string guildId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(guildId) || string.IsNullOrWhiteSpace(_options.BotToken))
        {
            return null;
        }

        var cacheKey = $"discord-guild-roster:{guildId}";
        if (_cache.TryGetValue<GuildRoster>(cacheKey, out var cached) && cached is not null)
        {
            return cached.Available ? cached.Members : null;
        }

        try
        {
            var members = await FetchGuildMembersAsync(guildId, _options.BotToken!, cancellationToken);
            _cache.Set(cacheKey, new GuildRoster(true, members), GuildRosterTtl);
            return members;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(
                ex,
                "Unable to read the member roster for guild {GuildId}; invite search will not be guild-filtered.",
                guildId);
            _cache.Set(cacheKey, new GuildRoster(false, EmptyRoster), GuildRosterFailureTtl);
            return null;
        }
    }

    // Convenience wrapper for callers that only need the set of member IDs
    // (e.g. filtering the existing-LSM-user invite search).
    public async Task<IReadOnlySet<string>?> TryGetGuildMemberDiscordIdsAsync(
        string guildId,
        CancellationToken cancellationToken)
    {
        var members = await TryGetGuildMembersAsync(guildId, cancellationToken);
        return members?.Select(member => member.Id).ToHashSet(StringComparer.Ordinal);
    }

    private static readonly IReadOnlyList<DiscordGuildMemberInfo> EmptyRoster = Array.Empty<DiscordGuildMemberInfo>();

    private async Task<IReadOnlyList<DiscordGuildMemberInfo>> FetchGuildMembersAsync(
        string guildId,
        string botToken,
        CancellationToken cancellationToken)
    {
        var client = _httpClientFactory.CreateClient();
        var members = new List<DiscordGuildMemberInfo>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var after = "0";
        const int pageSize = 1000;

        // Discord returns members ordered by user id ascending; page with the
        // highest id seen so far until a short page signals the end.
        while (true)
        {
            var uri = new Uri(
                $"https://discord.com/api/guilds/{Uri.EscapeDataString(guildId)}/members?limit={pageSize}&after={after}");

            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bot", botToken);
            request.Headers.Accept.ParseAdd("application/json");

            using var response = await client.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                throw new DiscordApiException((int)response.StatusCode, "Discord guild member listing failed.");
            }

            var page = await response.Content.ReadFromJsonAsync<List<DiscordGuildMemberPayload>>(
                cancellationToken: cancellationToken);
            if (page is null || page.Count == 0)
            {
                break;
            }

            foreach (var member in page)
            {
                var user = member.User;
                if (user is null || string.IsNullOrWhiteSpace(user.Id) || user.Bot == true)
                {
                    continue;
                }
                if (!seen.Add(user.Id))
                {
                    continue;
                }
                members.Add(new DiscordGuildMemberInfo(
                    user.Id,
                    user.Username ?? user.Id,
                    user.GlobalName,
                    user.Avatar));
            }

            if (page.Count < pageSize)
            {
                break;
            }

            // Advance the cursor to the highest id on this page.
            after = page[^1].User?.Id ?? after;
        }

        return members;
    }

    // Builds the CDN URL for a Discord member's avatar (or their default avatar
    // when they have none), so the invite roster UI can render a thumbnail.
    public static string BuildAvatarUrl(string discordUserId, string? avatarHash)
    {
        if (!string.IsNullOrWhiteSpace(avatarHash))
        {
            return new Uri(DiscordCdnBaseUri, $"avatars/{discordUserId}/{avatarHash}.png?size=64").ToString();
        }

        var index = ulong.TryParse(discordUserId, out var id) ? (int)((id >> 22) % 6) : 0;
        return new Uri(DiscordCdnBaseUri, $"embed/avatars/{index}.png").ToString();
    }

    private sealed record GuildRoster(bool Available, IReadOnlyList<DiscordGuildMemberInfo> Members);

    // First-login resolver for Discord-roster invites: when a Discord account
    // that was invited (before it had an LSM account) signs in, attach it to the
    // pending invite(s) and auto-join the linkshell(s). Mirrors the membership
    // creation in AcceptInviteAsync, then removes the resolved invite. Runs on
    // the authenticated path; the lookup is indexed and returns nothing in the
    // common case, so it's cheap.
    private async Task AutoJoinPendingDiscordInvitesAsync(
        AppUser appUser,
        string discordUserId,
        CancellationToken cancellationToken)
    {
        const string pendingInviteStatus = "PendingInvite";

        var pending = await _dbContext.Invites
            .Include(invite => invite.Linkshell)
            .Where(invite =>
                invite.DiscordUserId == discordUserId &&
                invite.AppUserId == null &&
                invite.Status == pendingInviteStatus)
            .ToListAsync(cancellationToken);

        if (pending.Count == 0)
        {
            return;
        }

        var changed = false;
        foreach (var invite in pending)
        {
            var alreadyMember = await _dbContext.AppUserLinkshells
                .AnyAsync(link => link.LinkshellId == invite.LinkshellId && link.AppUserId == appUser.Id, cancellationToken);

            if (!alreadyMember)
            {
                _dbContext.AppUserLinkshells.Add(new AppUserLinkshell
                {
                    AppUserId = appUser.Id,
                    LinkshellId = invite.LinkshellId,
                    LinkshellDkp = 0,
                    DateJoined = DateTime.UtcNow,
                    CharacterName = appUser.CharacterName ?? appUser.UserName,
                    Rank = LinkshellRanks.Member,
                    Status = "Active"
                });
            }

            appUser.PrimaryLinkshellId ??= invite.LinkshellId;
            appUser.PrimaryLinkshellName ??= invite.Linkshell?.LinkshellName;

            _dbContext.Invites.Remove(invite);
            changed = true;
        }

        if (changed)
        {
            await _dbContext.SaveChangesAsync(cancellationToken);
            await _userManager.UpdateAsync(appUser);
        }
    }

    private async Task<DiscordActivityUserDto> UpsertLocalUserAsync(
        DiscordIdentityProfile discordUser,
        AppUser? preferredAppUser,
        CancellationToken cancellationToken)
    {
        var resolution = await ResolveOrLinkAppUserAsync(discordUser, preferredAppUser, cancellationToken);
        var appUser = resolution.AppUser;
        var localDiscordUser = resolution.LocalDiscordUser;

        // Convert any Discord-roster invites awaiting this account into actual
        // memberships now that we know who they are.
        await AutoJoinPendingDiscordInvitesAsync(appUser, discordUser.Id, cancellationToken);

        return new DiscordActivityUserDto(
            localDiscordUser.Id,
            localDiscordUser.DiscordUserId,
            localDiscordUser.Username,
            localDiscordUser.Discriminator,
            localDiscordUser.GlobalName,
            localDiscordUser.Avatar,
            localDiscordUser.IdentityUserId,
            localDiscordUser.CreatedAtUtc,
            localDiscordUser.LastSeenAtUtc,
            resolution.IsNewDiscordUser,
            new LinkedAppUserDto(
                appUser.Id,
                appUser.UserName ?? string.Empty,
                appUser.CharacterName,
                appUser.TimeZone,
                appUser.PrimaryLinkshellId,
                appUser.PrimaryLinkshellName,
                resolution.IsNewAppUser));
    }

    private async Task<DiscordLinkResolution> ResolveOrLinkAppUserAsync(
        DiscordIdentityProfile discordUser,
        AppUser? preferredAppUser,
        CancellationToken cancellationToken)
    {
        const int maxAttempts = 3;

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            if (attempt > 1)
            {
                ResetTrackedState();
            }

            try
            {
                var existingUser = await _dbContext.DiscordActivityUsers
                    .Include(user => user.IdentityUser)
                    .SingleOrDefaultAsync(user => user.DiscordUserId == discordUser.Id, cancellationToken);

                var utcNow = DateTimeOffset.UtcNow;
                var isNewDiscordUser = existingUser is null;
                var localDiscordUser = existingUser ?? new DiscordActivityUser
                {
                    DiscordUserId = discordUser.Id,
                    CreatedAtUtc = utcNow
                };

                var appUser = localDiscordUser.IdentityUser;
                if (appUser is null && !string.IsNullOrWhiteSpace(localDiscordUser.IdentityUserId))
                {
                    appUser = await _userManager.FindByIdAsync(localDiscordUser.IdentityUserId);
                }

                if (appUser is not null &&
                    preferredAppUser is not null &&
                    !string.Equals(appUser.Id, preferredAppUser.Id, StringComparison.Ordinal))
                {
                    throw new DiscordLinkConflictException(
                        "This Discord account is already linked to a different website account.");
                }

                if (appUser is null && preferredAppUser is not null)
                {
                    var existingLinkForPreferredUser = await _dbContext.DiscordActivityUsers
                        .AnyAsync(
                            user => user.IdentityUserId == preferredAppUser.Id && user.DiscordUserId != discordUser.Id,
                            cancellationToken);

                    if (existingLinkForPreferredUser)
                    {
                        throw new DiscordLinkConflictException(
                            "The current website account is already linked to a different Discord account.");
                    }

                    appUser = preferredAppUser;
                }

                // First launch with no linked website account: adopt an imported /
                // onboarded PLACEHOLDER keyed to this Discord id (set by the DKP
                // import's optional Discord mapping, or by the board's self-service
                // onboarding) so the person inherits their seeded DKP in place
                // instead of getting a brand-new empty account. Promoting the
                // placeholder (AppUserId never changes) means no ledger/membership
                // repointing is needed.
                if (appUser is null && preferredAppUser is null)
                {
                    var placeholderMemberships = await _dbContext.AppUserLinkshells
                        .Include(membership => membership.AppUser)
                        .Where(membership => membership.DiscordUserId == discordUser.Id
                                             && membership.AppUser != null
                                             && membership.AppUser.IsPlaceholder)
                        .ToListAsync(cancellationToken);
                    var placeholderUser = placeholderMemberships.FirstOrDefault()?.AppUser;
                    if (placeholderUser is not null)
                    {
                        appUser = placeholderUser;
                        appUser.IsPlaceholder = false;
                        var promotedUserName = $"discord-{discordUser.Id}";
                        appUser.UserName = promotedUserName;
                        appUser.NormalizedUserName = _userManager.NormalizeName(promotedUserName);
                        if (string.IsNullOrWhiteSpace(appUser.CharacterName))
                        {
                            appUser.CharacterName = discordUser.GlobalName ?? discordUser.Username;
                        }
                        // The real account now owns identity — clear the Discord-id
                        // shortcut on the rows that pointed here.
                        foreach (var membership in placeholderMemberships.Where(m => m.AppUserId == placeholderUser.Id))
                        {
                            membership.DiscordUserId = null;
                        }
                        var promoteResult = await _userManager.UpdateAsync(appUser);
                        if (!promoteResult.Succeeded)
                        {
                            throw new InvalidOperationException(string.Join("; ", promoteResult.Errors.Select(error => error.Description)));
                        }
                    }
                }

                var isNewAppUser = false;
                if (appUser is null)
                {
                    isNewAppUser = true;
                    // TimeZone is left null intentionally. The clients auto-detect the
                    // browser zone on first page load via /api/activity/profile/detect-time-zone
                    // and persist it there, so we don't lock new users into UTC.
                    appUser = new AppUser
                    {
                        UserName = $"discord-{discordUser.Id}",
                        CharacterName = discordUser.GlobalName ?? discordUser.Username
                    };

                    var createResult = await _userManager.CreateAsync(appUser);
                    if (!createResult.Succeeded)
                    {
                        throw new InvalidOperationException(string.Join("; ", createResult.Errors.Select(error => error.Description)));
                    }
                }

                var shouldUpdateAppUser = false;
                if (string.IsNullOrWhiteSpace(appUser.CharacterName))
                {
                    appUser.CharacterName = discordUser.GlobalName ?? discordUser.Username;
                    shouldUpdateAppUser = true;
                }

                var avatarChanged = !string.Equals(localDiscordUser.Avatar, discordUser.Avatar, StringComparison.Ordinal);
                if (appUser.ProfileImage is null || avatarChanged)
                {
                    var syncedAvatar = await TryDownloadDiscordAvatarAsync(discordUser, cancellationToken);
                    if (syncedAvatar is not null &&
                        (appUser.ProfileImage is null || !syncedAvatar.AsSpan().SequenceEqual(appUser.ProfileImage)))
                    {
                        appUser.ProfileImage = syncedAvatar;
                        shouldUpdateAppUser = true;
                    }
                }

                if (shouldUpdateAppUser)
                {
                    var updateResult = await _userManager.UpdateAsync(appUser);
                    if (!updateResult.Succeeded)
                    {
                        if (HasConcurrencyFailure(updateResult) && attempt < maxAttempts)
                        {
                            _logger.LogInformation(
                                "Retrying Discord app-user link for Discord user {DiscordUserId} after Identity concurrency conflict on attempt {Attempt}.",
                                discordUser.Id,
                                attempt);
                            continue;
                        }

                        throw new InvalidOperationException(string.Join("; ", updateResult.Errors.Select(error => error.Description)));
                    }
                }

                localDiscordUser.Username = discordUser.Username;
                localDiscordUser.Discriminator = discordUser.Discriminator;
                localDiscordUser.GlobalName = discordUser.GlobalName;
                localDiscordUser.Avatar = discordUser.Avatar;
                localDiscordUser.IdentityUserId = appUser.Id;
                localDiscordUser.LastSeenAtUtc = utcNow;

                if (isNewDiscordUser)
                {
                    _dbContext.DiscordActivityUsers.Add(localDiscordUser);
                }

                await _dbContext.SaveChangesAsync(cancellationToken);
                return new DiscordLinkResolution(appUser, localDiscordUser, isNewDiscordUser, isNewAppUser);
            }
            catch (DbUpdateConcurrencyException) when (attempt < maxAttempts)
            {
                _logger.LogInformation(
                    "Retrying Discord app-user link for Discord user {DiscordUserId} after EF concurrency conflict on attempt {Attempt}.",
                    discordUser.Id,
                    attempt);
            }
            catch (DbUpdateException ex) when (IsRetryableLinkRace(ex) && attempt < maxAttempts)
            {
                _logger.LogInformation(
                    ex,
                    "Retrying Discord app-user link for Discord user {DiscordUserId} after duplicate link race on attempt {Attempt}.",
                    discordUser.Id,
                    attempt);
            }
        }

        throw new InvalidOperationException("Unable to resolve the Discord-linked app user after multiple retries.");
    }

    private async Task<byte[]?> TryDownloadDiscordAvatarAsync(
        DiscordIdentityProfile discordUser,
        CancellationToken cancellationToken)
    {
        var avatarUri = BuildDiscordAvatarUri(discordUser);
        if (avatarUri is null)
        {
            return null;
        }

        try
        {
            var client = _httpClientFactory.CreateClient();
            return await client.GetByteArrayAsync(avatarUri, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to sync Discord avatar for Discord user {DiscordUserId}.", discordUser.Id);
            return null;
        }
    }

    private static Uri? BuildDiscordAvatarUri(DiscordIdentityProfile discordUser)
    {
        if (!string.IsNullOrWhiteSpace(discordUser.Avatar))
        {
            return new Uri(DiscordCdnBaseUri, $"avatars/{discordUser.Id}/{discordUser.Avatar}.png?size=256");
        }

        var defaultAvatarIndex = ResolveDefaultAvatarIndex(discordUser);
        return new Uri(DiscordCdnBaseUri, $"embed/avatars/{defaultAvatarIndex}.png");
    }

    private static int ResolveDefaultAvatarIndex(DiscordIdentityProfile discordUser)
    {
        if (discordUser.Discriminator != "0" && int.TryParse(discordUser.Discriminator, out var discriminator))
        {
            return Math.Abs(discriminator % 5);
        }

        return ulong.TryParse(discordUser.Id, out var userId)
            ? (int)((userId >> 22) % 6)
            : 0;
    }

    private void ResetTrackedState()
    {
        foreach (var entry in _dbContext.ChangeTracker.Entries().ToList())
        {
            entry.State = EntityState.Detached;
        }
    }

    private static bool HasConcurrencyFailure(IdentityResult updateResult)
        => updateResult.Errors.Any(error => string.Equals(error.Code, "ConcurrencyFailure", StringComparison.OrdinalIgnoreCase));

    private static bool IsRetryableLinkRace(DbUpdateException ex)
        => ex.InnerException is PostgresException postgresException &&
           postgresException.SqlState == PostgresErrorCodes.UniqueViolation;

    private sealed record DiscordLinkResolution(
        AppUser AppUser,
        DiscordActivityUser LocalDiscordUser,
        bool IsNewDiscordUser,
        bool IsNewAppUser);

    private sealed record DiscordTokenPayload(
        [property: JsonPropertyName("access_token")] string AccessToken,
        [property: JsonPropertyName("token_type")] string TokenType,
        [property: JsonPropertyName("expires_in")] int ExpiresIn,
        [property: JsonPropertyName("refresh_token")] string RefreshToken,
        [property: JsonPropertyName("scope")] string Scope);

    private sealed record DiscordIdentityProfilePayload(
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("username")] string Username,
        [property: JsonPropertyName("discriminator")] string? Discriminator,
        [property: JsonPropertyName("global_name")] string? GlobalName,
        [property: JsonPropertyName("avatar")] string? Avatar);

    private sealed record DiscordGuildMemberPayload(
        [property: JsonPropertyName("user")] DiscordGuildMemberUserPayload? User);

    private sealed record DiscordGuildMemberUserPayload(
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("username")] string? Username,
        [property: JsonPropertyName("global_name")] string? GlobalName,
        [property: JsonPropertyName("avatar")] string? Avatar,
        [property: JsonPropertyName("bot")] bool? Bot);

    private sealed record DiscordGuildSummaryPayload(
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("name")] string? Name);

    private sealed record DiscordOAuth2MeApplication(
        [property: JsonPropertyName("id")] string Id);

    private sealed record DiscordOAuth2MePayload(
        [property: JsonPropertyName("application")] DiscordOAuth2MeApplication? Application,
        [property: JsonPropertyName("user")] DiscordIdentityProfilePayload? User,
        [property: JsonPropertyName("scopes")] string[]? Scopes,
        [property: JsonPropertyName("expires")] string? Expires);
}

public sealed record DiscordIdentityProfile(
    string Id,
    string Username,
    string Discriminator,
    string? GlobalName,
    string? Avatar);

// A non-bot member of a Discord guild, as read via the bot roster lookup.
public sealed record DiscordGuildMemberInfo(
    string Id,
    string Username,
    string? GlobalName,
    string? Avatar);

// A Discord guild the bot is in (id + name), as read via the bot-token guild
// listing. Used by the website server-lock pick-list.
public sealed record DiscordGuildSummary(
    string Id,
    string Name);

public sealed record DiscordExchangeResult(
    string AccessToken,
    string TokenType,
    int ExpiresIn,
    string Scope,
    DiscordActivityUserDto LocalUser);

public sealed record LinkedAppUserDto(
    string Id,
    string UserName,
    string? CharacterName,
    string? TimeZone,
    int? PrimaryLinkshellId,
    string? PrimaryLinkshellName,
    bool IsNewUser);

public sealed record DiscordActivityUserDto(
    Guid Id,
    string DiscordUserId,
    string Username,
    string Discriminator,
    string? GlobalName,
    string? Avatar,
    string? IdentityUserId,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset LastSeenAtUtc,
    bool IsNewUser,
    LinkedAppUserDto? AppUser);

public sealed record DiscordWebsiteSignInResult(
    AppUser AppUser,
    DiscordActivityUser LocalDiscordUser,
    bool IsNewDiscordUser,
    bool IsNewAppUser);

public sealed class DiscordApiException : Exception
{
    public DiscordApiException(int statusCode, string message, string? details = null)
        : base(message)
    {
        StatusCode = statusCode;
        Details = details;
    }

    public int StatusCode { get; }

    public string? Details { get; }
}

public sealed class DiscordLinkConflictException : Exception
{
    public DiscordLinkConflictException(string message) : base(message)
    {
    }
}
