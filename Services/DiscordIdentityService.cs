using System.ComponentModel.DataAnnotations;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using LinkshellManagerDiscordApp.Data;
using LinkshellManagerDiscordApp.Models;
using LinkshellManagerDiscordApp.Options;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql;

namespace LinkshellManagerDiscordApp.Services;

public sealed class DiscordIdentityService
{
    private static readonly Uri TokenEndpoint = new("https://discord.com/api/oauth2/token");
    private static readonly Uri UsersMeEndpoint = new("https://discord.com/api/users/@me");
    private static readonly Uri DiscordCdnBaseUri = new("https://cdn.discordapp.com/");

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ApplicationDbContext _dbContext;
    private readonly UserManager<AppUser> _userManager;
    private readonly DiscordOAuthOptions _options;
    private readonly ILogger<DiscordIdentityService> _logger;

    public DiscordIdentityService(
        IHttpClientFactory httpClientFactory,
        ApplicationDbContext dbContext,
        UserManager<AppUser> userManager,
        IOptions<DiscordOAuthOptions> options,
        ILogger<DiscordIdentityService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _dbContext = dbContext;
        _userManager = userManager;
        _options = options.Value;
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
            var details = await response.Content.ReadAsStringAsync(cancellationToken);
            _logger.LogWarning("Discord token exchange failed with status {Status}: {Body}", response.StatusCode, details);
            throw new DiscordApiException((int)response.StatusCode, "Discord token exchange failed.", details);
        }

        var payload = await response.Content.ReadFromJsonAsync<DiscordTokenPayload>(cancellationToken: cancellationToken);
        if (payload is null || string.IsNullOrWhiteSpace(payload.AccessToken))
        {
            throw new DiscordApiException(502, "Discord returned an invalid token payload.");
        }

        return payload;
    }

    private async Task<DiscordIdentityProfile> GetCurrentDiscordUserAsync(string accessToken, CancellationToken cancellationToken)
    {
        var client = _httpClientFactory.CreateClient();

        using var request = new HttpRequestMessage(HttpMethod.Get, UsersMeEndpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Headers.Accept.ParseAdd("application/json");

        using var response = await client.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var details = await response.Content.ReadAsStringAsync(cancellationToken);
            _logger.LogWarning("Discord user lookup failed with status {Status}: {Body}", response.StatusCode, details);
            throw new DiscordApiException((int)response.StatusCode, "Discord user lookup failed.", details);
        }

        var user = await response.Content.ReadFromJsonAsync<DiscordIdentityProfilePayload>(cancellationToken: cancellationToken);
        if (user is null || string.IsNullOrWhiteSpace(user.Id))
        {
            throw new DiscordApiException(502, "Discord returned an invalid user payload.");
        }

        return new DiscordIdentityProfile(
            user.Id,
            user.Username,
            user.Discriminator ?? "0",
            user.GlobalName,
            user.Avatar);
    }

    private async Task<DiscordActivityUserDto> UpsertLocalUserAsync(
        DiscordIdentityProfile discordUser,
        AppUser? preferredAppUser,
        CancellationToken cancellationToken)
    {
        var resolution = await ResolveOrLinkAppUserAsync(discordUser, preferredAppUser, cancellationToken);
        var appUser = resolution.AppUser;
        var localDiscordUser = resolution.LocalDiscordUser;

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

                var isNewAppUser = false;
                if (appUser is null)
                {
                    isNewAppUser = true;
                    appUser = new AppUser
                    {
                        UserName = $"discord-{discordUser.Id}",
                        CharacterName = discordUser.GlobalName ?? discordUser.Username,
                        TimeZone = "UTC"
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

                if (string.IsNullOrWhiteSpace(appUser.TimeZone))
                {
                    appUser.TimeZone = "UTC";
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
}

public sealed record DiscordIdentityProfile(
    string Id,
    string Username,
    string Discriminator,
    string? GlobalName,
    string? Avatar);

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
