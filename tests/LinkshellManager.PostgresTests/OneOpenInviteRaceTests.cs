using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using LinkshellManagerDiscordApp.Data;
using LinkshellManagerDiscordApp.Models;
using LinkshellManagerDiscordApp.Options;
using LinkshellManagerDiscordApp.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LinkshellManager.PostgresTests;

// One open invite per Discord user per linkshell (IX_Invites_OneOpenDiscordInvite). Two officers
// inviting the same person at the same moment (or one double click) both pass the "already
// invited?" check; the index refuses the later save, and the officer gets the same friendly
// "already invited" the check gives instead of an error page. Either way there is ONE invite.
[Collection("postgres")]
public class OneOpenInviteRaceTests : IAsyncLifetime
{
    private const string GuildId = "900000000000000001";
    private const string InvitedDiscordId = "900000000000000002";
    private const string AlreadyInvited = "That person has already been invited.";

    private readonly PostgresFixture _pg;

    public OneOpenInviteRaceTests(PostgresFixture pg) => _pg = pg;

    public Task InitializeAsync() => _pg.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    private async Task<int> SeedLinkshellAsync()
    {
        await using var db = _pg.CreateContext();
        var linkshell = new Linkshell { LinkshellName = "Invite LS" };
        db.Linkshells.Add(linkshell);
        await db.SaveChangesAsync();
        return linkshell.Id;
    }

    [Fact]
    public async Task An_invite_saved_between_the_check_and_the_save_answers_already_invited()
    {
        var linkshellId = await SeedLinkshellAsync();
        await using (var probe = _pg.CreateContext())
        {
            await PostgresFixture.AssertIndexExistsAsync(probe, UniqueRaceRetry.OneOpenDiscordInviteIndex);
        }

        // The other officer's invite lands after this request checked, just before it saves.
        var otherOfficer = new BeforeFirstSave(async () =>
        {
            await using var other = _pg.CreateContext();
            other.Invites.Add(new Invite
            {
                LinkshellId = linkshellId, DiscordUserId = InvitedDiscordId, Status = "PendingInvite",
            });
            await other.SaveChangesAsync();
        });

        await using (var db = _pg.CreateContext(otherOfficer))
        {
            var result = await Service(db).AddDiscordMemberAsync(linkshellId, GuildId, InvitedDiscordId, CancellationToken.None);

            Assert.True(otherOfficer.Fired);
            Assert.False(result.Success);
            Assert.Equal(AlreadyInvited, result.Error);
        }

        await AssertOneInviteAsync(linkshellId);
    }

    [Fact]
    public async Task A_double_click_makes_one_invite()
    {
        var linkshellId = await SeedLinkshellAsync();

        await using var first = _pg.CreateContext();
        await using var second = _pg.CreateContext();
        var results = await Task.WhenAll(
            Service(first).AddDiscordMemberAsync(linkshellId, GuildId, InvitedDiscordId, CancellationToken.None),
            Service(second).AddDiscordMemberAsync(linkshellId, GuildId, InvitedDiscordId, CancellationToken.None));

        // Whichever way the two interleave: one invited them, the other was told they already are.
        Assert.Single(results, r => r.Success);
        Assert.Single(results, r => !r.Success && r.Error == AlreadyInvited);
        await AssertOneInviteAsync(linkshellId);
    }

    private async Task AssertOneInviteAsync(int linkshellId)
    {
        await using var check = _pg.CreateContext();
        var invite = await check.Invites.AsNoTracking().SingleAsync(i => i.LinkshellId == linkshellId);
        Assert.Equal(InvitedDiscordId, invite.DiscordUserId);
        Assert.Equal("PendingInvite", invite.Status);
        Assert.Null(invite.AppUserId);
    }

    // The service with a Discord that says the invited person is in the linkshell's server.
    private static InviteCandidateService Service(ApplicationDbContext db) =>
        new(db, new DiscordIdentityService(
            new GuildFactory(),
            db,
            null!,
            Microsoft.Extensions.Options.Options.Create(new DiscordOAuthOptions
            {
                ClientId = "app", ClientSecret = "secret", BotToken = "bot",
            }),
            new MemoryCache(new MemoryCacheOptions()),
            NullLogger<DiscordIdentityService>.Instance));

    private sealed class GuildHasTheInvitedPerson : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    $"[{{\"user\":{{\"id\":\"{InvitedDiscordId}\",\"username\":\"invited\",\"global_name\":\"Invited\"}}}}]",
                    System.Text.Encoding.UTF8,
                    "application/json"),
            });
    }

    private sealed class GuildFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new GuildHasTheInvitedPerson(), disposeHandler: true);
    }

    // Runs `then` once, just before this context's first save -- after it has read, before it writes.
    private sealed class BeforeFirstSave : SaveChangesInterceptor
    {
        private readonly Func<Task> _then;

        public BeforeFirstSave(Func<Task> then) => _then = then;

        public bool Fired { get; private set; }

        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (!Fired)
            {
                Fired = true;
                await _then();
            }
            return result;
        }
    }
}
