using System.Net;
using System.Text;
using LinkshellManagerDiscordApp.Data;
using LinkshellManagerDiscordApp.Models;
using LinkshellManagerDiscordApp.Options;
using LinkshellManagerDiscordApp.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LinkshellManager.Tests;

// The live DKP sheet post runs on into several messages once the roster passes ~95 members
// (DkpSheetMessageBuilder). These pin how the publisher keeps that set of messages in step with
// the roster against a fake Discord: edit what's there, add pages at the bottom when it grows,
// delete spare pages when it shrinks, and repost the whole set (in order) if one was deleted.
public class DiscordDkpSheetPublisherTests
{
    private const string Channel = "chan";

    // Answers like Discord: a post gets a new id, an edit succeeds unless that message was
    // deleted (404), a delete succeeds. Records every call as "VERB id".
    private sealed class FakeDiscord : HttpMessageHandler
    {
        public readonly List<string> Calls = new();
        public readonly HashSet<string> Deleted = new();
        private int _next;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var segments = request.RequestUri!.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
            var messageId = segments[^1] == "messages" ? null : segments[^1];
            if (request.Method == HttpMethod.Post)
            {
                var id = $"new{++_next}";
                Calls.Add($"POST {id}");
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent($"{{\"id\":\"{id}\"}}", Encoding.UTF8, "application/json")
                });
            }
            Calls.Add($"{request.Method.Method} {messageId}");
            if (request.Method == HttpMethod.Patch && Deleted.Contains(messageId!))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)
                {
                    Content = new StringContent("{\"message\":\"Unknown Message\",\"code\":10008}")
                });
            }
            return Task.FromResult(new HttpResponseMessage(
                request.Method == HttpMethod.Delete ? HttpStatusCode.NoContent : HttpStatusCode.OK)
            {
                Content = new StringContent("{}", Encoding.UTF8, "application/json")
            });
        }
    }

    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private static (ApplicationDbContext Db, DiscordDkpSheetPublisher Publisher, FakeDiscord Discord) Setup(
        int members, string? firstId = null, string? extraIds = null)
    {
        var db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);
        db.Linkshells.Add(new Linkshell { Id = 1, LinkshellName = "Sky", LootStructure = "Dkp" });
        for (var i = 1; i <= members; i++)
        {
            db.AppUserLinkshells.Add(new AppUserLinkshell
            {
                Id = i, LinkshellId = 1, CharacterName = $"Member{i:000}", LinkshellDkp = 10
            });
        }
        db.LinkshellChannelRoutes.Add(new LinkshellChannelRoute
        {
            Id = 1, LinkshellId = 1, ChannelId = Channel, PostDkpSheet = true,
            DkpSheetMessageId = firstId, DkpSheetExtraMessageIds = extraIds,
        });
        db.SaveChanges();

        var discord = new FakeDiscord();
        var bot = new DiscordBotClient(
            new Factory(discord),
            Microsoft.Extensions.Options.Options.Create(new DiscordOAuthOptions { BotToken = "test-token" }),
            new MemoryCache(new MemoryCacheOptions()),
            NullLogger<DiscordBotClient>.Instance);
        var publisher = new DiscordDkpSheetPublisher(
            db,
            new DkpSheetService(db, new DkpPoolResolver(db, new DkpPoolProvisioner(db))),
            new ChannelRouteResolver(db),
            bot,
            NullLogger<DiscordDkpSheetPublisher>.Instance);
        return (db, publisher, discord);
    }

    // 200 members of this size (short names, small balances) fill exactly two messages.
    private static LinkshellChannelRoute Route(ApplicationDbContext db) => db.LinkshellChannelRoutes.Single();

    [Fact]
    public async Task FirstPost_PostsEveryMessage_AndRemembersThem()
    {
        var (db, publisher, discord) = Setup(members: 200);

        await publisher.PublishAsync(1, CancellationToken.None);

        Assert.Equal(new[] { "POST new1", "POST new2" }, discord.Calls);
        Assert.Equal("new1", Route(db).DkpSheetMessageId);
        Assert.Equal("new2", Route(db).DkpSheetExtraMessageIds);
    }

    [Fact]
    public async Task ARosterThatGrew_EditsWhatsThere_AndAddsPagesAtTheBottom()
    {
        var (db, publisher, discord) = Setup(members: 200, firstId: "a");

        await publisher.PublishAsync(1, CancellationToken.None);

        Assert.Equal(new[] { "PATCH a", "POST new1" }, discord.Calls);
        Assert.Equal("a", Route(db).DkpSheetMessageId);
        Assert.Equal("new1", Route(db).DkpSheetExtraMessageIds);
    }

    [Fact]
    public async Task ARosterThatShrank_DeletesTheSparePages()
    {
        var (db, publisher, discord) = Setup(members: 3, firstId: "a", extraIds: "b,c");

        await publisher.PublishAsync(1, CancellationToken.None);

        Assert.Equal(new[] { "PATCH a", "DELETE b", "DELETE c" }, discord.Calls);
        Assert.Equal("a", Route(db).DkpSheetMessageId);
        Assert.Null(Route(db).DkpSheetExtraMessageIds);
    }

    // Someone deleted a page. Posting just that one again would land it at the bottom, out of
    // order, so the whole post goes up again.
    [Fact]
    public async Task ADeletedPage_RepostsTheWholePostInOrder()
    {
        var (db, publisher, discord) = Setup(members: 200, firstId: "a", extraIds: "b,c");
        discord.Deleted.Add("b");

        await publisher.PublishAsync(1, CancellationToken.None);

        Assert.Equal(
            new[] { "PATCH a", "PATCH b", "DELETE a", "DELETE b", "DELETE c", "POST new1", "POST new2" },
            discord.Calls);
        Assert.Equal("new1", Route(db).DkpSheetMessageId);
        Assert.Equal("new2", Route(db).DkpSheetExtraMessageIds);
    }
}
