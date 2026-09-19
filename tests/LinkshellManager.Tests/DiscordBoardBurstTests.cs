using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using LinkshellManagerDiscordApp.Options;
using LinkshellManagerDiscordApp.Services;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LinkshellManager.Tests;

// Ending a camp from the addon left a wide sign-up board as its first alliance alone. Two things
// together did it: the board was rendered mid-way through End Event (the "defeated" note, which
// deletes every alliance message after the first), and the render that followed lost the
// re-posts of those alliances to a rate limit that posts, unlike edits, never retried.
public class DiscordBoardBurstTests
{
    // Answers each request in turn from `responses`, recording what was asked.
    private sealed class ScriptedHandler : HttpMessageHandler
    {
        private readonly Queue<Func<HttpResponseMessage>> _responses;
        public List<HttpMethod> Methods { get; } = new();

        public ScriptedHandler(params Func<HttpResponseMessage>[] responses)
            => _responses = new Queue<Func<HttpResponseMessage>>(responses);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Methods.Add(request.Method);
            return Task.FromResult(_responses.Dequeue()());
        }
    }

    private sealed class Factory : IHttpClientFactory
    {
        private readonly HttpMessageHandler _handler;
        public Factory(HttpMessageHandler handler) => _handler = handler;
        public HttpClient CreateClient(string name) => new(_handler, disposeHandler: false);
    }

    private static DiscordBotClient Client(ScriptedHandler handler) => new(
        new Factory(handler),
        Microsoft.Extensions.Options.Options.Create(new DiscordOAuthOptions
        {
            ClientId = "x", ClientSecret = "x", BotToken = "token",
        }),
        new MemoryCache(new MemoryCacheOptions()),
        NullLogger<DiscordBotClient>.Instance);

    // The smallest back-off Discord can ask for; ResolveRetryAfter clamps it to half a second.
    private static HttpResponseMessage RateLimited()
    {
        var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        response.Headers.Add("X-RateLimit-Reset-After", "0.01");
        return response;
    }

    private static HttpResponseMessage Posted(string id) => new(HttpStatusCode.OK)
    {
        Content = new StringContent($"{{\"id\":\"{id}\"}}", Encoding.UTF8, "application/json"),
    };

    [Fact]
    public async Task APostThatIsRateLimited_IsRetriedOnceAndLands()
    {
        var handler = new ScriptedHandler(RateLimited, () => Posted("222"));

        var id = await Client(handler).PostMessageAsync("1", new { content = "a2" }, CancellationToken.None);

        Assert.Equal("222", id);
        Assert.Equal(2, handler.Methods.Count);
    }

    // Once, not forever: a channel that stays limited must not stall the render loop.
    [Fact]
    public async Task APostThatStaysRateLimited_GivesUpAfterOneRetry()
    {
        var handler = new ScriptedHandler(RateLimited, RateLimited);

        var id = await Client(handler).PostMessageAsync("1", new { content = "a2" }, CancellationToken.None);

        Assert.Null(id);
        Assert.Equal(2, handler.Methods.Count);
    }

    [Fact]
    public async Task ADeleteThatIsRateLimited_IsRetriedOnceAndLands()
    {
        var handler = new ScriptedHandler(RateLimited, () => new HttpResponseMessage(HttpStatusCode.NoContent));

        var deleted = await Client(handler).DeleteMessageAsync("1", "333", CancellationToken.None);

        Assert.True(deleted);
        Assert.Equal(new[] { HttpMethod.Delete, HttpMethod.Delete }, handler.Methods);
    }

    // End Event queues the same camp twice (parked, then revived). It must render ONCE, from
    // wherever it settled -- that is what keeps the board edited in place.
    [Fact]
    public void ABurstForOneBoard_RendersItOnce()
    {
        var channel = Channel.CreateUnbounded<int>();
        channel.Writer.TryWrite(7);
        channel.Writer.TryWrite(9);
        channel.Writer.TryWrite(7);

        var order = DiscordEventChannelBackgroundService.DrainDistinct(7, channel.Reader);

        Assert.Equal(new[] { 7, 9 }, order);
    }
}
