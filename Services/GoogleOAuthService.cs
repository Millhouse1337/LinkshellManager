using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using LinkshellManagerDiscordApp.Data;
using LinkshellManagerDiscordApp.Options;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace LinkshellManagerDiscordApp.Services;

public sealed class GoogleOAuthException : Exception
{
    public GoogleOAuthException(string message) : base(message) { }
    public GoogleOAuthException(string message, Exception inner) : base(message, inner) { }
}

public sealed class GoogleOAuthService
{
    private const string AuthEndpoint = "https://accounts.google.com/o/oauth2/v2/auth";
    private const string TokenEndpoint = "https://oauth2.googleapis.com/token";
    private const string UserInfoEndpoint = "https://openidconnect.googleapis.com/v1/userinfo";
    // Per-file Drive scope: the app can only touch spreadsheets it CREATED (or
    // that the user explicitly picked) — never the user's other spreadsheets.
    // This is what limits LSManager to the single "LSM DKP" sheet it makes, and
    // it's a non-sensitive scope (avoids Google's restricted-scope verification).
    private const string DriveFileScope = "https://www.googleapis.com/auth/drive.file";
    private const string OpenIdScope = "openid";
    private const string EmailScope = "email";
    internal const string StateProtectorPurpose = "Linkshell.GoogleOAuth.State";
    private static readonly TimeSpan StateLifetime = TimeSpan.FromMinutes(10);

    private readonly GoogleSheetsOptions _options;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ApplicationDbContext _db;
    private readonly GoogleSheetsSyncService _sheets;
    private readonly IDataProtector _stateProtector;
    private readonly ILogger<GoogleOAuthService> _logger;

    public GoogleOAuthService(
        IOptions<GoogleSheetsOptions> options,
        IHttpClientFactory httpClientFactory,
        ApplicationDbContext db,
        GoogleSheetsSyncService sheets,
        IDataProtectionProvider protectionProvider,
        ILogger<GoogleOAuthService> logger)
    {
        _options = options.Value;
        _httpClientFactory = httpClientFactory;
        _db = db;
        _sheets = sheets;
        _stateProtector = protectionProvider.CreateProtector(StateProtectorPurpose);
        _logger = logger;
    }

    public bool IsConfigured => _sheets.IsConfigured;

    public string BuildAuthorizationUrl(int linkshellId, string appUserId, string redirectUri)
    {
        if (!IsConfigured) throw new GoogleOAuthException("Google OAuth is not configured (set GoogleSheets:ClientId and ClientSecret).");

        var state = ProtectState(linkshellId, appUserId);

        var scope = string.Join(" ", new[] { DriveFileScope, OpenIdScope, EmailScope });
        var query = new Dictionary<string, string?>
        {
            ["client_id"] = _options.ClientId,
            ["redirect_uri"] = redirectUri,
            ["response_type"] = "code",
            ["scope"] = scope,
            ["access_type"] = "offline",
            ["prompt"] = "consent",
            // We're REDUCING scope (drive.file only). Incremental auth would union
            // this with any previously-granted broad scope and carry it forward on
            // reconnect, so keep each authorization independent.
            ["include_granted_scopes"] = "false",
            ["state"] = state,
        };

        var qs = string.Join("&", query
            .Where(kvp => !string.IsNullOrEmpty(kvp.Value))
            .Select(kvp => $"{Uri.EscapeDataString(kvp.Key)}={Uri.EscapeDataString(kvp.Value!)}"));
        return $"{AuthEndpoint}?{qs}";
    }

    // Resolve the linkshell id from the (DataProtector-signed) OAuth state and
    // confirm the state was issued to the current user, WITHOUT exchanging the
    // code or persisting anything. Lets the caller run its manage-permission
    // check before any token is stored on the shared linkshell row.
    public int ResolveLinkshellFromState(string state, string currentAppUserId)
    {
        var (linkshellId, appUserId) = UnprotectState(state);
        if (!string.Equals(appUserId, currentAppUserId, StringComparison.Ordinal))
        {
            throw new GoogleOAuthException("OAuth state was issued for a different user.");
        }

        return linkshellId;
    }

    public async Task<int> HandleCallbackAsync(string code, string state, string currentAppUserId, string redirectUri, CancellationToken cancellationToken)
    {
        if (!IsConfigured) throw new GoogleOAuthException("Google OAuth is not configured.");

        var linkshellId = ResolveLinkshellFromState(state, currentAppUserId);

        var token = await ExchangeCodeAsync(code, redirectUri, cancellationToken);
        if (string.IsNullOrWhiteSpace(token.RefreshToken))
        {
            throw new GoogleOAuthException("Google did not return a refresh token. Revoke the app in your Google account and try again.");
        }

        var email = await GetEmailAsync(token.AccessToken, cancellationToken);

        var linkshell = await _db.Linkshells.FirstOrDefaultAsync(l => l.Id == linkshellId, cancellationToken)
            ?? throw new GoogleOAuthException("Linkshell not found.");

        linkshell.GoogleOAuthRefreshTokenEnc = _sheets.ProtectRefreshToken(token.RefreshToken);
        linkshell.GoogleOAuthUserEmail = email;
        linkshell.GoogleOAuthConnectedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(cancellationToken);

        _sheets.InvalidateCache(linkshellId);
        return linkshellId;
    }

    public async Task MarkDisconnectedAsync(int linkshellId, CancellationToken cancellationToken)
    {
        var linkshell = await _db.Linkshells.FirstOrDefaultAsync(l => l.Id == linkshellId, cancellationToken);
        if (linkshell is null) return;
        linkshell.GoogleOAuthRefreshTokenEnc = null;
        linkshell.GoogleOAuthUserEmail = null;
        linkshell.GoogleOAuthConnectedAt = null;
        await _db.SaveChangesAsync(cancellationToken);
        _sheets.InvalidateCache(linkshellId);
    }

    private string ProtectState(int linkshellId, string appUserId)
    {
        var nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        var issuedTicks = DateTime.UtcNow.Ticks.ToString();
        var payload = $"{linkshellId}|{appUserId}|{nonce}|{issuedTicks}";
        return _stateProtector.Protect(payload);
    }

    private (int linkshellId, string appUserId) UnprotectState(string state)
    {
        string payload;
        try
        {
            payload = _stateProtector.Unprotect(state);
        }
        catch (CryptographicException ex)
        {
            throw new GoogleOAuthException("Invalid OAuth state.", ex);
        }

        var parts = payload.Split('|');
        if (parts.Length != 4) throw new GoogleOAuthException("Malformed OAuth state.");
        if (!int.TryParse(parts[0], out var linkshellId)) throw new GoogleOAuthException("Malformed OAuth state (linkshell).");
        if (!long.TryParse(parts[3], out var issuedTicks)) throw new GoogleOAuthException("Malformed OAuth state (timestamp).");

        var age = DateTime.UtcNow - new DateTime(issuedTicks, DateTimeKind.Utc);
        if (age > StateLifetime || age < TimeSpan.Zero)
        {
            throw new GoogleOAuthException("OAuth state has expired. Restart the connect flow.");
        }

        return (linkshellId, parts[1]);
    }

    private async Task<GoogleTokenPayload> ExchangeCodeAsync(string code, string redirectUri, CancellationToken cancellationToken)
    {
        var client = _httpClientFactory.CreateClient();
        var form = new Dictionary<string, string>
        {
            ["client_id"] = _options.ClientId!,
            ["client_secret"] = _options.ClientSecret!,
            ["code"] = code,
            ["grant_type"] = "authorization_code",
            ["redirect_uri"] = redirectUri,
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, TokenEndpoint)
        {
            Content = new FormUrlEncodedContent(form)
        };
        request.Headers.Accept.ParseAdd("application/json");

        using var response = await client.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("Google token exchange failed with status {Status}.", response.StatusCode);
            throw new GoogleOAuthException($"Google token exchange failed ({(int)response.StatusCode}).");
        }

        var payload = await response.Content.ReadFromJsonAsync<GoogleTokenPayload>(cancellationToken: cancellationToken);
        if (payload is null || string.IsNullOrWhiteSpace(payload.AccessToken))
        {
            throw new GoogleOAuthException("Google returned an invalid token payload.");
        }
        return payload;
    }

    private async Task<string?> GetEmailAsync(string accessToken, CancellationToken cancellationToken)
    {
        try
        {
            var client = _httpClientFactory.CreateClient();
            using var request = new HttpRequestMessage(HttpMethod.Get, UserInfoEndpoint);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            request.Headers.Accept.ParseAdd("application/json");

            using var response = await client.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogDebug("userinfo lookup failed with status {Status}; storing without email.", response.StatusCode);
                return null;
            }
            var payload = await response.Content.ReadFromJsonAsync<GoogleUserInfo>(cancellationToken: cancellationToken);
            return payload?.Email;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "userinfo lookup threw; storing without email.");
            return null;
        }
    }

    private sealed record GoogleTokenPayload(
        [property: JsonPropertyName("access_token")] string AccessToken,
        [property: JsonPropertyName("refresh_token")] string? RefreshToken,
        [property: JsonPropertyName("expires_in")] int ExpiresIn,
        [property: JsonPropertyName("token_type")] string TokenType,
        [property: JsonPropertyName("scope")] string? Scope);

    private sealed record GoogleUserInfo(
        [property: JsonPropertyName("email")] string? Email,
        [property: JsonPropertyName("email_verified")] bool? EmailVerified);
}
