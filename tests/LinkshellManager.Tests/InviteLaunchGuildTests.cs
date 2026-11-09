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

// The invite card's "From My Discord Server" lists the server the app was opened in. That
// server's id arrives as a request header, so the server only trusts it once the caller's
// own Discord account is confirmed to be a member — otherwise anyone could list the members
// of any server the bot is in. Discord itself is faked: one server, G1, with one member.
public class InviteLaunchGuildTests
{
    private const string GuildId = "G1";
    private const string MemberDiscordId = "111";

    private sealed class FakeDiscord : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            var json = path switch
            {
                "/api/guilds/G1/members" => $$"""[{ "user": { "id": "{{MemberDiscordId}}", "username": "member" } }]""",
                "/api/users/@me/guilds" => """[{ "id": "G1", "name": "Mill's Server" }]""",
                _ => null
            };
            return Task.FromResult(json is null
                ? new HttpResponseMessage(HttpStatusCode.NotFound)
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") });
        }
    }

    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private static async Task<InviteCandidateService> NewServiceAsync()
    {
        var db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);
        db.DiscordActivityUsers.Add(new DiscordActivityUser { DiscordUserId = MemberDiscordId, Username = "member", IdentityUserId = "in-server" });
        db.DiscordActivityUsers.Add(new DiscordActivityUser { DiscordUserId = "999", Username = "outsider", IdentityUserId = "outsider" });
        await db.SaveChangesAsync();

        var discord = new DiscordIdentityService(
            new Factory(new FakeDiscord()),
            db,
            null!, // UserManager: not reached by the roster or server-list lookups
            Microsoft.Extensions.Options.Options.Create(new DiscordOAuthOptions { BotToken = "bot-token" }),
            new MemoryCache(new MemoryCacheOptions()),
            NullLogger<DiscordIdentityService>.Instance);
        return new InviteCandidateService(db, discord);
    }

    [Fact]
    public async Task ACallerInTheServer_IsTrusted()
    {
        var service = await NewServiceAsync();

        Assert.True(await service.IsAppUserInGuildAsync(GuildId, "in-server", CancellationToken.None));
    }

    [Fact]
    public async Task ACallerOutsideTheServer_IsNot()
    {
        var service = await NewServiceAsync();

        Assert.False(await service.IsAppUserInGuildAsync(GuildId, "outsider", CancellationToken.None));
    }

    [Fact]
    public async Task AServerTheBotCannotRead_IsUnknown_NotTrusted()
    {
        var service = await NewServiceAsync();

        Assert.Null(await service.IsAppUserInGuildAsync("G-unknown", "in-server", CancellationToken.None));
    }

    [Fact]
    public async Task TheServersName_ComesFromTheBotsServerList()
    {
        var service = await NewServiceAsync();

        Assert.Equal("Mill's Server", await service.GetGuildNameAsync(GuildId, CancellationToken.None));
        Assert.Null(await service.GetGuildNameAsync("G-unknown", CancellationToken.None));
    }
}
