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

    // UserManager is only reached after Discord accepts a token, which these tests never do.
    private static DiscordIdentityService NewService(ApplicationDbContext db, CountingHandler handler, IMemoryCache cache) => new(
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
}
