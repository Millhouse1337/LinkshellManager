using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using LinkshellManagerDiscordApp.Data;
using LinkshellManagerDiscordApp.Options;
using LinkshellManagerDiscordApp.Services;
using LinkshellManagerDiscordApp.Utils;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LinkshellManager.Tests;

// Every failed token check we send to Discord counts toward Discord's invalid-request limit;
// tripping it bans this server's IP (bot boards and every sign-in stop). So a bearer that can't
// be a Discord token is refused without asking, a token Discord turned down is remembered for a
// minute, and the /api rate limiter only gives a token its own bucket once Discord accepted it.
public class DiscordBearerPrecheckTests
{
    private const string WellShapedToken = "6qrZcUqja7812RVdnEKjpzOL4CvHBF";

    private sealed class CountingHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _status;
        public int Calls { get; private set; }
        public CountingHandler(HttpStatusCode status) => _status = status;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(new HttpResponseMessage(_status));
        }
    }

    private sealed class Factory : IHttpClientFactory
    {
        private readonly HttpMessageHandler _handler;
        public Factory(HttpMessageHandler handler) => _handler = handler;
        public HttpClient CreateClient(string name) => new(_handler, disposeHandler: false);
    }

    private static ApplicationDbContext NewDb() =>
        new(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    // UserManager is only reached after Discord accepts a token (see SignInIgnoringLocalUpsert).
    private static DiscordIdentityService NewService(ApplicationDbContext db, HttpMessageHandler handler, IMemoryCache cache) => new(
        new Factory(handler),
        db,
        null!,
        Microsoft.Extensions.Options.Options.Create(new DiscordOAuthOptions { ClientId = "app", ClientSecret = "secret" }),
        cache,
        NullLogger<DiscordIdentityService>.Instance);

    [Theory]
    [InlineData("x")]
    [InlineData("short")]
    [InlineData("has spaces in the token value")]
    [InlineData("<script>alert(1)</script>aaaaaaaaaa")]
    [InlineData("ünïcödéünïcödéünïcödéünïcödé")]
    public async Task ATokenThatCantBeADiscordToken_IsRefusedWithoutAskingDiscord(string token)
    {
        using var db = NewDb();
        var handler = new CountingHandler(HttpStatusCode.Unauthorized);
        var service = NewService(db, handler, new MemoryCache(new MemoryCacheOptions()));

        var ex = await Assert.ThrowsAsync<DiscordApiException>(
            () => service.GetCurrentLocalUserAsync(token, CancellationToken.None));

        Assert.Equal(401, ex.StatusCode);
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task ATokenDiscordRejected_IsNotSentToDiscordAgainForAMinute()
    {
        using var db = NewDb();
        var handler = new CountingHandler(HttpStatusCode.Unauthorized);
        var cache = new MemoryCache(new MemoryCacheOptions());
        var service = NewService(db, handler, cache);

        for (var i = 0; i < 5; i++)
        {
            var ex = await Assert.ThrowsAsync<DiscordApiException>(
                () => service.GetCurrentLocalUserAsync(WellShapedToken, CancellationToken.None));
            Assert.Equal(401, ex.StatusCode);
        }

        Assert.Equal(1, handler.Calls);
        // A rejected token never earns its own rate-limit bucket.
        Assert.Null(DiscordIdentityService.RecentlyValidatedTokenKey(cache, WellShapedToken));
    }

    [Fact]
    public async Task ADiscordOutage_IsNotRemembered_SoTheNextTryAsksAgain()
    {
        using var db = NewDb();
        var handler = new CountingHandler(HttpStatusCode.ServiceUnavailable);
        var service = NewService(db, handler, new MemoryCache(new MemoryCacheOptions()));

        await Assert.ThrowsAsync<DiscordApiException>(() => service.GetCurrentLocalUserAsync(WellShapedToken, CancellationToken.None));
        await Assert.ThrowsAsync<DiscordApiException>(() => service.GetCurrentLocalUserAsync(WellShapedToken, CancellationToken.None));

        Assert.Equal(2, handler.Calls);
    }

    [Theory]
    [InlineData("6qrZcUqja7812RVdnEKjpzOL4CvHBF")]
    [InlineData("MTIzNDU2Nzg5MDEyMzQ1Njc4.abc-def_ghi")]
    public void RealLookingTokens_PassTheShapeCheck(string token)
    {
        Assert.True(DiscordIdentityService.LooksLikeDiscordToken(token));
    }

    // ---- /api rate-limit buckets ----

    private static HttpRequest Request(string path, string? authorization = null)
    {
        var context = new DefaultHttpContext();
        context.Request.Path = path;
        if (authorization is not null) { context.Request.Headers.Authorization = authorization; }
        return context.Request;
    }

    private static readonly IPAddress Ip = IPAddress.Parse("203.0.113.7");

    [Fact]
    public void PagesOutsideApi_AreNotLimitedHere()
    {
        Assert.Null(ApiRateLimitPartition.ResolveKey(Request("/discord-activity/home"), Ip, _ => null));
    }

    [Fact]
    public void DiscordInteractions_AreNotLimited_BecauseTheyAllComeFromDiscord()
    {
        Assert.Null(ApiRateLimitPartition.ResolveKey(Request("/api/discord/interactions"), Ip, _ => null));
    }

    [Fact]
    public void AMadeUpBearer_SharesTheCallersIpBucket()
    {
        var key = ApiRateLimitPartition.ResolveKey(Request("/api/activity/overview", "Bearer " + WellShapedToken), Ip, _ => null);

        Assert.Equal("ip:203.0.113.7", key);
    }

    [Fact]
    public void ABearerDiscordAccepted_GetsItsOwnBucket()
    {
        var key = ApiRateLimitPartition.ResolveKey(
            Request("/api/activity/overview", "Bearer " + WellShapedToken),
            Ip,
            token => token == WellShapedToken ? "known" : null);

        Assert.Equal("token:known", key);
    }

    [Fact]
    public void AddonAndCookieCalls_UseTheIpBucket()
    {
        Assert.Equal("ip:203.0.113.7", ApiRateLimitPartition.ResolveKey(Request("/api/addon/events", "Bearer att_abc"), Ip, _ => null));
        Assert.Equal("ip:203.0.113.7", ApiRateLimitPartition.ResolveKey(Request("/api/web/me"), Ip, _ => null));
        Assert.Equal("ip:unknown", ApiRateLimitPartition.ResolveKey(Request("/API/web/me"), null, _ => null));
    }

    [Fact]
    public void ToDScreenshots_GetTheirOwnIpBucket_SoImagesCantStarveApiCalls()
    {
        Assert.Equal("uploads:203.0.113.7",
            ApiRateLimitPartition.ResolveKey(Request("/api/activity/uploads/tods/abc.png"), Ip, _ => null));
    }

    // ---- a token Discord accepted keeps its bucket past the 90 s validation ----

    private sealed class FakeClock : Microsoft.Extensions.Internal.ISystemClock
    {
        public DateTimeOffset UtcNow { get; set; } = DateTimeOffset.UtcNow;
    }

    // Answers each call with the next queued status; a 200 carries an /oauth2/@me body for our app.
    private sealed class ScriptedHandler : HttpMessageHandler
    {
        private readonly System.Collections.Generic.Queue<HttpStatusCode> _statuses;
        public int Calls { get; private set; }
        public ScriptedHandler(params HttpStatusCode[] statuses) => _statuses = new(statuses);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            var status = _statuses.Dequeue();
            var response = new HttpResponseMessage(status);
            if (status == HttpStatusCode.OK)
            {
                response.Content = new StringContent(
                    "{\"application\":{\"id\":\"app\"},\"user\":{\"id\":\"42\",\"username\":\"bob\"}}",
                    System.Text.Encoding.UTF8,
                    "application/json");
            }
            return Task.FromResult(response);
        }
    }

    private static async Task SignInIgnoringLocalUpsert(DiscordIdentityService service, string token)
    {
        // The local-user upsert needs a UserManager these tests don't build; the Discord
        // check and its cache writes happen before it, which is all these tests look at.
        try { await service.GetCurrentLocalUserAsync(token, CancellationToken.None); }
        catch (Exception ex) when (ex is not DiscordApiException) { }
    }

    [Fact]
    public async Task AnAcceptedToken_KeepsItsOwnBucket_AfterItsValidationExpires_UntilDiscordRejectsIt()
    {
        using var db = NewDb();
        var clock = new FakeClock();
        var cache = new MemoryCache(new MemoryCacheOptions { Clock = clock });
        var handler = new ScriptedHandler(HttpStatusCode.OK, HttpStatusCode.Unauthorized);
        var service = NewService(db, handler, cache);

        await SignInIgnoringLocalUpsert(service, WellShapedToken);
        var bucket = DiscordIdentityService.RecentlyValidatedTokenKey(cache, WellShapedToken);
        Assert.NotNull(bucket);

        // Past the 90 s validation: the token still has its bucket, so the Activity user
        // doesn't fall into the shared proxy-IP bucket for their next request.
        clock.UtcNow += TimeSpan.FromMinutes(5);
        Assert.Equal(bucket, DiscordIdentityService.RecentlyValidatedTokenKey(cache, WellShapedToken));

        // Discord now says no (revoked): the token loses its bucket.
        await Assert.ThrowsAsync<DiscordApiException>(() => service.GetCurrentLocalUserAsync(WellShapedToken, CancellationToken.None));
        Assert.Equal(2, handler.Calls);
        Assert.Null(DiscordIdentityService.RecentlyValidatedTokenKey(cache, WellShapedToken));
    }

    [Fact]
    public async Task AGuildCheckDiscordRefuses_IsRememberedAsNotAMember_InsteadOfAskedEveryPoll()
    {
        using var db = NewDb();
        var handler = new ScriptedHandler(HttpStatusCode.Forbidden);
        var service = NewService(db, handler, new MemoryCache(new MemoryCacheOptions()));

        // First time: Discord's 403 is surfaced (the caller denies and logs it).
        await Assert.ThrowsAsync<DiscordApiException>(
            () => service.VerifyGuildMembershipAsync(WellShapedToken, "123", CancellationToken.None));

        // Then: denied from memory, without another call that counts against our IP.
        Assert.False(await service.VerifyGuildMembershipAsync(WellShapedToken, "123", CancellationToken.None));
        Assert.Equal(1, handler.Calls);
    }
}
