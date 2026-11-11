using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using LinkshellManagerDiscordApp.Data;
using LinkshellManagerDiscordApp.Models;
using LinkshellManagerDiscordApp.Options;
using LinkshellManagerDiscordApp.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LinkshellManager.Tests;

// Event comments are mirrored to the linkshell's discussion channel. Any member (anonymous too)
// writes the text, so the mirror must never let "@everyone" or a role mention ping the server,
// and deleting a comment has to take its Discord copy down with it.
public class EventCommentDiscordTests
{
    private const int LinkshellId = 1;
    private const int HistoryId = 7;
    private const string Channel = "555";

    // Records each request (method, url, body) and answers with `respond`.
    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _respond;
        public List<(HttpMethod Method, string Url, string? Body)> Requests { get; } = new();

        public RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) => _respond = respond;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add((request.Method, request.RequestUri!.ToString(), body));
            return _respond(request);
        }
    }

    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private static ApplicationDbContext NewDb() =>
        new(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static ApplicationDbContext Seeded()
    {
        var db = NewDb();
        db.Linkshells.Add(new Linkshell { Id = LinkshellId, LinkshellName = "LS", DiscussionChannelId = Channel });
        db.EventHistories.Add(new EventHistory { Id = HistoryId, LinkshellId = LinkshellId, EventName = "Dynamis" });
        db.SaveChanges();
        return db;
    }

    private static EventCommentService Service(ApplicationDbContext db, RecordingHandler handler) =>
        new(db, new DiscordBotClient(
            new Factory(handler),
            Microsoft.Extensions.Options.Options.Create(new DiscordOAuthOptions { BotToken = "test-token" }),
            new MemoryCache(new MemoryCacheOptions()),
            NullLogger<DiscordBotClient>.Instance));

    private static HttpResponseMessage Posted(string id) => new(HttpStatusCode.OK)
    {
        Content = new StringContent($"{{\"id\":\"{id}\"}}", Encoding.UTF8, "application/json"),
    };

    [Fact]
    public async Task MirroredComment_ParsesNoMentions()
    {
        using var db = Seeded();
        var handler = new RecordingHandler(_ => Posted("900"));

        var comment = await Service(db, handler).AddAsync(
            HistoryId, "user-1", "Alice", "@everyone come look", isAnonymous: true, CancellationToken.None);

        Assert.NotNull(comment);
        Assert.Equal("900", comment!.DiscordMessageId);
        var post = Assert.Single(handler.Requests);
        using var json = JsonDocument.Parse(post.Body!);
        var parse = json.RootElement.GetProperty("allowed_mentions").GetProperty("parse");
        Assert.Equal(JsonValueKind.Array, parse.ValueKind);
        Assert.Equal(0, parse.GetArrayLength());
    }

    [Fact]
    public async Task DeletingAMirroredComment_DeletesItsDiscordMessage()
    {
        using var db = Seeded();
        db.EventComments.Add(new EventComment
        {
            Id = 3, EventHistoryId = HistoryId, LinkshellId = LinkshellId, AppUserId = "user-1",
            Body = "hi", CreatedAt = DateTime.UtcNow, DiscordMessageId = "900",
        });
        db.SaveChanges();
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.NoContent));

        var ok = await Service(db, handler).DeleteAsync(3, "user-1", canManage: false, CancellationToken.None);

        Assert.True(ok);
        Assert.Empty(db.EventComments);
        var delete = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Delete, delete.Method);
        Assert.EndsWith($"/channels/{Channel}/messages/900", delete.Url);
    }

    // The app delete stands even when Discord refuses (bot kicked, no Manage Messages, ...).
    [Fact]
    public async Task DiscordDeleteFailing_StillDeletesTheComment()
    {
        using var db = Seeded();
        db.EventComments.Add(new EventComment
        {
            Id = 3, EventHistoryId = HistoryId, LinkshellId = LinkshellId, AppUserId = "user-1",
            Body = "hi", CreatedAt = DateTime.UtcNow, DiscordMessageId = "900",
        });
        db.SaveChanges();
        var handler = new RecordingHandler(_ => throw new HttpRequestException("discord down"));

        var ok = await Service(db, handler).DeleteAsync(3, "user-1", canManage: false, CancellationToken.None);

        Assert.True(ok);
        Assert.Empty(db.EventComments);
    }

    [Fact]
    public async Task DeletingAnUnmirroredComment_DoesNotCallDiscord()
    {
        using var db = Seeded();
        db.EventComments.Add(new EventComment
        {
            Id = 3, EventHistoryId = HistoryId, LinkshellId = LinkshellId, AppUserId = "user-1",
            Body = "hi", CreatedAt = DateTime.UtcNow,
        });
        db.SaveChanges();
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.NoContent));

        var ok = await Service(db, handler).DeleteAsync(3, "user-1", canManage: false, CancellationToken.None);

        Assert.True(ok);
        Assert.Empty(handler.Requests);
    }
}
