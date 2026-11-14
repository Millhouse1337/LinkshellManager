using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text.Encodings.Web;
using LinkshellManagerDiscordApp.Models;
using LinkshellManagerDiscordApp.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;

namespace LinkshellManagerDiscordApp.Authorization;

// Signs in a request that carries "Authorization: Bearer <Discord access token>" (the Discord
// Activity). The one place that token is checked: DiscordIdentityService asks Discord who it
// belongs to (with its 90-second "accepted" and 60-second "rejected" memories, so a repeated bad
// token is not sent to Discord again) and finds or links the local AppUser.
//
//   no Bearer header          -> NoResult (this handler has nothing to say)
//   Discord rejects the token -> Fail     (the caller is signed out)
//   Discord accepts it        -> Success: a principal whose NameIdentifier is the AppUser id, with
//                                the token kept in the ticket for the guild-lock check
//
// It is NOT the app's default scheme, so nothing runs it unless asked: ApiSignIn.ResolveCallerAsync
// does, once per request (ASP.NET keeps the first answer for the rest of the request). Nothing
// challenges with it either; a refused caller gets the action's own 401 body, as before.
public sealed class DiscordBearerAuthenticationHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    public const string AccessTokenName = "discord_access_token";

    private readonly DiscordIdentityService _discordIdentityService;

    public DiscordBearerAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder,
        DiscordIdentityService discordIdentityService)
        : base(options, logger, encoder)
    {
        _discordIdentityService = discordIdentityService;
    }

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!ApiSignIn.TryGetBearerToken(Request, out var accessToken))
        {
            return AuthenticateResult.NoResult();
        }

        string? appUserId;
        try
        {
            var localUser = await _discordIdentityService.GetCurrentLocalUserAsync(accessToken, Context.RequestAborted);
            appUserId = localUser.AppUser?.Id;
        }
        catch (DiscordApiException)
        {
            // Never echo Discord's reason: it may hint at whether a token is valid.
            return AuthenticateResult.Fail("Discord did not accept the bearer token.");
        }

        if (string.IsNullOrWhiteSpace(appUserId))
        {
            return AuthenticateResult.Fail("The Discord account has no LinkshellManager user.");
        }

        var identity = new ClaimsIdentity(new[] { new Claim(ClaimTypes.NameIdentifier, appUserId) }, Scheme.Name);
        var properties = new AuthenticationProperties();
        properties.StoreTokens(new[] { new AuthenticationToken { Name = AccessTokenName, Value = accessToken } });
        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), properties, Scheme.Name));
    }
}

// Who is calling an Activity endpoint or an addon management endpoint. Both used to carry their
// own copy of this; it lives here once now.
public static class ApiSignIn
{
    public const string DiscordBearerScheme = "DiscordBearer";

    private const string AddonTokenPrefix = "att_";

    public static AuthenticationBuilder AddDiscordBearer(this AuthenticationBuilder builder) =>
        builder.AddScheme<AuthenticationSchemeOptions, DiscordBearerAuthenticationHandler>(DiscordBearerScheme, displayName: null, configureOptions: null);

    // A request that carries a bearer is decided by the DiscordBearer handler alone. When Discord
    // turns the token down the caller is signed out, even if a website cookie rides along: never
    // fall back to the cookie. Falling back would let anyone who can break our calls to discord.com
    // (or who adds a junk bearer to skip the browser's CSRF preflight) push a request onto the
    // cookie path.
    //
    // Without a bearer the website cookie decides. UseAuthentication already read it into
    // HttpContext.User (the cookie is the app's default scheme).
    //
    // ignoreAddonTokens: the addon management endpoints treat "Bearer att_..." (an addon token,
    // which [AddonApiAuth] checks on other endpoints) as no bearer at all, so the cookie decides.
    public static async Task<ApiCaller> ResolveCallerAsync(
        HttpContext context,
        UserManager<AppUser> userManager,
        bool ignoreAddonTokens = false)
    {
        if (TryGetBearerToken(context.Request, out var accessToken))
        {
            if (!(ignoreAddonTokens && accessToken.StartsWith(AddonTokenPrefix, StringComparison.Ordinal)))
            {
                var bearer = await context.AuthenticateAsync(DiscordBearerScheme);
                if (!bearer.Succeeded)
                {
                    return ApiCaller.SignedOut;
                }

                return new ApiCaller(
                    await userManager.GetUserAsync(bearer.Principal),
                    bearer.Properties.GetTokenValue(DiscordBearerAuthenticationHandler.AccessTokenName));
            }
        }

        if (context.User.Identity?.IsAuthenticated == true)
        {
            return new ApiCaller(await userManager.GetUserAsync(context.User), DiscordAccessToken: null);
        }

        return ApiCaller.SignedOut;
    }

    // "Authorization: Bearer <something>". Any other scheme, or an empty token, is not a bearer.
    public static bool TryGetBearerToken(HttpRequest request, out string accessToken)
    {
        accessToken = string.Empty;

        if (!AuthenticationHeaderValue.TryParse(request.Headers.Authorization, out var headerValue))
        {
            return false;
        }

        if (!"Bearer".Equals(headerValue.Scheme, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(headerValue.Parameter))
        {
            return false;
        }

        accessToken = headerValue.Parameter;
        return true;
    }
}

// AppUser: null when signed out (or the account is gone). DiscordAccessToken: the bearer Discord
// accepted, needed by the guild-lock check; null on the website cookie.
public sealed record ApiCaller(AppUser? AppUser, string? DiscordAccessToken)
{
    public static readonly ApiCaller SignedOut = new(AppUser: null, DiscordAccessToken: null);
}
