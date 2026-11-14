using System.Net;
using System.Net.Http;
using LinkshellManagerDiscordApp.Models;
using Xunit;
using static LinkshellManager.Tests.ApiPipelineRequests;

namespace LinkshellManager.Tests;

// Who-is-calling, end to end through the real app: every way a request can arrive signed in or
// not, and the exact status and body each one gets back. The Activity and the website read these
// bodies (activity-http.client.ts shows the "error" text; a 401 sends the website to its sign-in
// screen), and the addon reads its own, so a change to any of them is a change users see.
//
// Endpoints used:
// - GET api/activity/linkshells/{id}: a member-only read (401 signed out, 403 not a member or
//   blocked by the guild lock, 200 for a member).
// - GET api/addon/me: the addon's own token scheme (AddonApiAuthAttribute), not Discord's.
public sealed class ApiSignInPipelineTests : IClassFixture<ApiSignInPipelineTests.Host>
{
    public sealed class Host : IAsyncLifetime
    {
        public ApiPipelineHost App { get; } = new();

        public async Task InitializeAsync()
        {
            await App.SeedUserAsync(MemberId, MemberDiscordId);
            await App.SeedUserAsync(OutsiderId, OutsiderDiscordId);
            await App.SeedAsync(db =>
            {
                db.Linkshells.Add(new Linkshell { Id = OpenLinkshellId, LinkshellName = "Open LS", AppUserId = MemberId });
                db.Linkshells.Add(new Linkshell
                {
                    Id = LockedLinkshellId,
                    LinkshellName = "Locked LS",
                    AppUserId = MemberId,
                    DiscordGuildId = LockedGuildId,
                    LockToDiscordGuild = true,
                });
                db.AppUserLinkshells.Add(new AppUserLinkshell
                {
                    AppUserId = MemberId, LinkshellId = OpenLinkshellId, CharacterName = "Member", Rank = LinkshellRanks.Leader, Status = "Active",
                });
                db.AppUserLinkshells.Add(new AppUserLinkshell
                {
                    AppUserId = MemberId, LinkshellId = LockedLinkshellId, CharacterName = "Member", Rank = LinkshellRanks.Leader, Status = "Active",
                });
                db.AddonApiTokens.Add(new AddonApiToken
                {
                    LinkshellId = OpenLinkshellId,
                    IssuedToAppUserId = MemberId,
                    TokenHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(AddonToken))),
                    TokenPrefix = AddonToken[..8],
                    CreatedAt = DateTime.UtcNow,
                });
                return Task.CompletedTask;
            });

            App.Discord.Accept(MemberToken, MemberDiscordId);
            App.Discord.Accept(OutsiderToken, OutsiderDiscordId);
            App.Discord.Accept(MemberInLockedGuildToken, MemberDiscordId);
            App.Discord.AddGuildMember(MemberInLockedGuildToken, LockedGuildId);
            MemberCookie = await App.SignInCookieAsync(MemberId);
            OutsiderCookie = await App.SignInCookieAsync(OutsiderId);
        }

        public string MemberCookie { get; private set; } = string.Empty;
        public string OutsiderCookie { get; private set; } = string.Empty;

        public Task DisposeAsync()
        {
            App.Dispose();
            return Task.CompletedTask;
        }
    }

    private const string MemberId = "member-user";
    private const string MemberDiscordId = "200000000000000001";
    private const string OutsiderId = "outsider-user";
    private const string OutsiderDiscordId = "200000000000000002";
    private const int OpenLinkshellId = 501;
    private const int LockedLinkshellId = 502;
    private const string LockedGuildId = "300000000000000001";

    // Shaped like Discord tokens (DiscordIdentityService.LooksLikeDiscordToken), so they reach FakeDiscord.
    private const string MemberToken = "MemberTokenAAAAAAAAAAAAAAAAAAAA";
    private const string MemberInLockedGuildToken = "MemberGuildTokenBBBBBBBBBBBBBBB";
    private const string OutsiderToken = "OutsiderTokenCCCCCCCCCCCCCCCCCC";
    private const string AddonToken = "att_pipelinetesttoken000000000000000000";

    private const string LinkshellDetailSignedOut =
        "{\"error\":\"Sign in with ASP.NET Identity or provide a Discord bearer token to load linkshell details.\"}";
    private const string GenericForbidden =
        "{\"error\":\"You don't have permission to do that. If you think you should, ask a linkshell leader to update your role.\"}";
    private const string SessionExpired =
        "{\"error\":\"Your session has expired. Reload and sign in again.\"}";

    private readonly Host _host;

    public ApiSignInPipelineTests(Host host) => _host = host;

    private ApiPipelineHost App => _host.App;

    private async Task<(HttpStatusCode Status, string Body)> SendAsync(HttpRequestMessage request)
    {
        using var client = App.NewClient();
        using var response = await client.SendAsync(request);
        return (response.StatusCode, await response.Content.ReadAsStringAsync());
    }

    private static string Detail(int linkshellId) => $"/api/activity/linkshells/{linkshellId}";

    // ---- Signed out ----

    [Fact]
    public async Task NoCredentials_GetsTheActionsOwn401Body()
    {
        var (status, body) = await SendAsync(Get(Detail(OpenLinkshellId)));

        Assert.Equal(HttpStatusCode.Unauthorized, status);
        Assert.Equal(LinkshellDetailSignedOut, body);
    }

    [Fact]
    public async Task ATokenDiscordRejects_GetsTheSame401_AndIsOnlySentToDiscordOnce()
    {
        const string rejected = "RejectedTokenDDDDDDDDDDDDDDDDDD";

        var first = await SendAsync(Get(Detail(OpenLinkshellId), bearer: rejected));
        var second = await SendAsync(Get(Detail(OpenLinkshellId), bearer: rejected));

        Assert.Equal((HttpStatusCode.Unauthorized, LinkshellDetailSignedOut), first);
        Assert.Equal((HttpStatusCode.Unauthorized, LinkshellDetailSignedOut), second);
        // The 60-second "Discord said no" memory: the second request never reached Discord.
        Assert.Equal(1, App.Discord.TokenLookups(rejected));
    }

    [Fact]
    public async Task ATokenThatCannotBeADiscordToken_GetsTheSame401_WithoutAskingDiscord()
    {
        const string junk = "short";

        var result = await SendAsync(Get(Detail(OpenLinkshellId), bearer: junk));

        Assert.Equal((HttpStatusCode.Unauthorized, LinkshellDetailSignedOut), result);
        Assert.Equal(0, App.Discord.TokenLookups(junk));
    }

    // A bad bearer is never "rescued" by the website cookie riding along on the same request.
    [Fact]
    public async Task ARejectedTokenWithAGoodCookie_IsStillSignedOut()
    {
        var result = await SendAsync(Get(Detail(OpenLinkshellId), bearer: "RejectedTokenEEEEEEEEEEEEEEEEEE", cookie: _host.MemberCookie));

        Assert.Equal((HttpStatusCode.Unauthorized, LinkshellDetailSignedOut), result);
    }

    // An Authorization header that is not "Bearer <token>" is not a bearer at all: the cookie decides.
    [Fact]
    public async Task ANonBearerAuthorizationHeader_FallsBackToTheCookie()
    {
        var request = Get(Detail(OpenLinkshellId), cookie: _host.MemberCookie);
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Basic", "dXNlcjpwYXNz");

        var (status, _) = await SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, status);
    }

    // ---- Signed in with the Activity's Discord bearer ----

    [Fact]
    public async Task AGoodBearer_ReadsItsOwnLinkshell()
    {
        var (status, body) = await SendAsync(Get(Detail(OpenLinkshellId), bearer: MemberToken));

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Contains("Open LS", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AGoodBearer_ForSomeoneElsesLinkshell_GetsTheGeneric403()
    {
        var result = await SendAsync(Get(Detail(OpenLinkshellId), bearer: OutsiderToken));

        Assert.Equal((HttpStatusCode.Forbidden, GenericForbidden), result);
    }

    // ---- Signed in with the website cookie ----

    [Fact]
    public async Task AGoodCookie_ReadsItsOwnLinkshell()
    {
        var (status, body) = await SendAsync(Get(Detail(OpenLinkshellId), cookie: _host.MemberCookie));

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Contains("Open LS", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AGoodCookie_ForSomeoneElsesLinkshell_GetsTheGeneric403()
    {
        var result = await SendAsync(Get(Detail(OpenLinkshellId), cookie: _host.OutsiderCookie));

        Assert.Equal((HttpStatusCode.Forbidden, GenericForbidden), result);
    }

    // A cookie mutation without the antiforgery header is refused before the action runs.
    [Fact]
    public async Task ACookieMutationWithoutAntiforgery_IsRefused()
    {
        var result = await SendAsync(Post($"/api/activity/linkshells/{OpenLinkshellId}/primary", cookie: _host.MemberCookie));

        Assert.Equal((HttpStatusCode.BadRequest, "{\"error\":\"Antiforgery validation failed.\"}"), result);
    }

    // ---- The per-linkshell Discord guild lock ----

    [Fact]
    public async Task GuildLock_ABearerWhoseDiscordAccountIsInTheServer_GetsIn()
    {
        var (status, _) = await SendAsync(Get(Detail(LockedLinkshellId), bearer: MemberInLockedGuildToken, guildId: LockedGuildId));

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Contains($"GET guild-member {LockedGuildId} token={MemberInLockedGuildToken}", App.Discord.Calls);
    }

    [Fact]
    public async Task GuildLock_ABearerNotInTheServer_GetsTheGeneric403()
    {
        // MemberToken is the same member, but Discord does not list them in the locked server.
        var result = await SendAsync(Get(Detail(LockedLinkshellId), bearer: MemberToken, guildId: LockedGuildId));

        Assert.Equal((HttpStatusCode.Forbidden, GenericForbidden), result);
    }

    [Fact]
    public async Task GuildLock_DoesNotApplyToTheWebsiteCookie()
    {
        var (status, _) = await SendAsync(Get(Detail(LockedLinkshellId), cookie: _host.MemberCookie));

        Assert.Equal(HttpStatusCode.OK, status);
    }

    // ---- The addon's own token (AddonApiAuthAttribute) ----

    [Fact]
    public async Task Addon_NoToken_GetsItsOwn401()
    {
        var result = await SendAsync(Get("/api/addon/me"));

        Assert.Equal(
            (HttpStatusCode.Unauthorized, "{\"error\":\"Missing addon bearer token. Send Authorization: Bearer att_<token>.\"}"),
            result);
    }

    [Fact]
    public async Task Addon_AWrongToken_GetsItsOwn401_AndNeverAsksDiscord()
    {
        const string wrong = "att_notarealtoken00000000000000000000";

        var result = await SendAsync(Get("/api/addon/me", bearer: wrong));

        Assert.Equal((HttpStatusCode.Unauthorized, "{\"error\":\"Addon token is invalid or revoked.\"}"), result);
        Assert.Equal(0, App.Discord.TokenLookups(wrong));
    }

    [Fact]
    public async Task Addon_AGoodToken_IsServed_AndNeverAsksDiscord()
    {
        var (status, body) = await SendAsync(Get("/api/addon/me", bearer: AddonToken));

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Contains("Open LS", body, StringComparison.Ordinal);
        Assert.Equal(0, App.Discord.TokenLookups(AddonToken));
    }

    // A Discord bearer is not an addon token.
    [Fact]
    public async Task Addon_ADiscordBearer_IsRefused()
    {
        var result = await SendAsync(Get("/api/addon/me", bearer: MemberToken));

        Assert.Equal((HttpStatusCode.Unauthorized, "{\"error\":\"Addon token is invalid or revoked.\"}"), result);
    }

    // ---- The addon's management endpoints: the Activity's bearer or the website cookie ----

    private const string ManagementSignedOut =
        "{\"error\":\"Not signed in. Open /Identity/Account/Login on this same host first, or launch the activity inside Discord.\"}";

    private static string ManagementTokens(int linkshellId) => $"/api/addon/management/tokens?linkshellId={linkshellId}";

    [Fact]
    public async Task AddonManagement_NoCredentials_GetsItsOwn401()
    {
        var result = await SendAsync(Get(ManagementTokens(OpenLinkshellId)));

        Assert.Equal((HttpStatusCode.Unauthorized, ManagementSignedOut), result);
    }

    [Fact]
    public async Task AddonManagement_AGoodBearerOrCookie_IsServed()
    {
        var bearer = await SendAsync(Get(ManagementTokens(OpenLinkshellId), bearer: MemberToken));
        var cookie = await SendAsync(Get(ManagementTokens(OpenLinkshellId), cookie: _host.MemberCookie));

        Assert.Equal(HttpStatusCode.OK, bearer.Status);
        Assert.Equal(HttpStatusCode.OK, cookie.Status);
    }

    [Fact]
    public async Task AddonManagement_ARejectedTokenWithAGoodCookie_IsStillSignedOut()
    {
        var result = await SendAsync(Get(ManagementTokens(OpenLinkshellId), bearer: "RejectedTokenGGGGGGGGGGGGGGGGGG", cookie: _host.MemberCookie));

        Assert.Equal((HttpStatusCode.Unauthorized, ManagementSignedOut), result);
    }

    // An addon token (att_...) is not a Discord bearer here: it is ignored, so the cookie decides
    // and Discord is never asked about it.
    [Fact]
    public async Task AddonManagement_AnAddonTokenIsIgnored_SoTheCookieDecides()
    {
        var withCookie = await SendAsync(Get(ManagementTokens(OpenLinkshellId), bearer: AddonToken, cookie: _host.MemberCookie));
        var alone = await SendAsync(Get(ManagementTokens(OpenLinkshellId), bearer: AddonToken));

        Assert.Equal(HttpStatusCode.OK, withCookie.Status);
        Assert.Equal((HttpStatusCode.Unauthorized, ManagementSignedOut), alone);
        Assert.Equal(0, App.Discord.TokenLookups(AddonToken));
    }

    [Fact]
    public async Task AddonManagement_NotAMember_GetsTheGeneric403()
    {
        var result = await SendAsync(Get(ManagementTokens(OpenLinkshellId), bearer: OutsiderToken));

        Assert.Equal((HttpStatusCode.Forbidden, GenericForbidden), result);
    }

    // ---- Other signed-in surfaces ----

    [Fact]
    public async Task ApiMe_NoToken_GetsItsOwn401()
    {
        var result = await SendAsync(Get("/api/me"));

        Assert.Equal(
            (HttpStatusCode.Unauthorized, "{\"error\":\"Missing Discord bearer token. Send Authorization: Bearer <access_token>.\"}"),
            result);
    }

    [Fact]
    public async Task ApiMe_AGoodBearer_ReturnsTheLinkedUser()
    {
        var (status, body) = await SendAsync(Get("/api/me", bearer: MemberToken));

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Contains(MemberId, body, StringComparison.Ordinal);
    }

    // [Authorize] pages under /api answer the cookie's JSON 401, not a redirect to the login page.
    [Fact]
    public async Task WebAdmin_SignedOut_GetsTheCookies401Body()
    {
        var result = await SendAsync(Get("/api/web/admin/settings"));

        Assert.Equal((HttpStatusCode.Unauthorized, SessionExpired), result);
    }

    [Fact]
    public async Task WebAdmin_NotASuperAdmin_GetsTheGeneric403()
    {
        var result = await SendAsync(Get("/api/web/admin/settings", cookie: _host.MemberCookie));

        Assert.Equal((HttpStatusCode.Forbidden, GenericForbidden), result);
    }

    [Fact]
    public async Task WebSession_ReportsTheCookieUser()
    {
        var signedOut = await SendAsync(Get("/api/web/session"));
        var signedIn = await SendAsync(Get("/api/web/session", cookie: _host.MemberCookie));

        Assert.Equal(HttpStatusCode.OK, signedOut.Status);
        Assert.Equal(HttpStatusCode.OK, signedIn.Status);
        Assert.DoesNotContain(MemberId, signedOut.Body, StringComparison.Ordinal);
        Assert.Contains(MemberId, signedIn.Body, StringComparison.Ordinal);
    }

    // ---- Open to everyone ----

    [Theory]
    [InlineData("GET", "/healthz", HttpStatusCode.OK)]
    [InlineData("GET", "/readyz", HttpStatusCode.OK)]
    [InlineData("GET", "/api/activity/antiforgery", HttpStatusCode.OK)]
    [InlineData("GET", "/api/web/session", HttpStatusCode.OK)]
    [InlineData("GET", "/features", HttpStatusCode.OK)]
    [InlineData("GET", "/addon", HttpStatusCode.OK)]
    [InlineData("GET", "/privacy", HttpStatusCode.OK)]
    [InlineData("GET", "/terms", HttpStatusCode.OK)]
    [InlineData("GET", "/", HttpStatusCode.OK)]
    [InlineData("GET", "/Identity/Account/Login", HttpStatusCode.OK)]
    // Sign-in starts with a redirect to Discord (through the OAuth handler's /signin-discord).
    [InlineData("GET", "/auth/discord/login", HttpStatusCode.Redirect)]
    // The Activity's code exchange is reached signed out; an empty body is refused by the action.
    [InlineData("POST", "/auth/discord/exchange", HttpStatusCode.BadRequest)]
    // The interactions webhook is reached signed out and checks Discord's signature itself
    // (no public key is configured here, so every request is refused by the controller).
    [InlineData("POST", "/api/discord/interactions", HttpStatusCode.Unauthorized)]
    public async Task AnonymousEndpoints_AreReachedSignedOut(string method, string path, HttpStatusCode expected)
    {
        var request = method == "GET" ? Get(path) : Post(path);

        var (status, _) = await SendAsync(request);

        Assert.Equal(expected, status);
    }

    [Fact]
    public async Task TheSignedOutRequestsAboveNeverReachedDiscord()
    {
        await SendAsync(Get(Detail(OpenLinkshellId)));
        await SendAsync(Get("/api/web/session"));

        Assert.Empty(App.Discord.UnexpectedCalls);
    }
}

// The global /api rate limiter still answers 429 with its JSON body once a bucket is spent.
public sealed class ApiRateLimitPipelineTests : IDisposable
{
    private readonly ApiPipelineHost _app = new(permitPerMinute: 3);

    [Fact]
    public async Task TheFourthSignedOutRequestInAMinute_Gets429()
    {
        using var client = _app.NewClient();
        var statuses = new List<HttpStatusCode>();
        string lastBody = string.Empty;
        for (var i = 0; i < 4; i++)
        {
            using var response = await client.SendAsync(Get("/api/activity/linkshells/1"));
            statuses.Add(response.StatusCode);
            lastBody = await response.Content.ReadAsStringAsync();
        }

        Assert.Equal(
            new[] { HttpStatusCode.Unauthorized, HttpStatusCode.Unauthorized, HttpStatusCode.Unauthorized, HttpStatusCode.TooManyRequests },
            statuses);
        Assert.Equal("{\"error\":\"Too many requests. Please wait a minute and try again.\"}", lastBody);
    }

    public void Dispose() => _app.Dispose();
}
