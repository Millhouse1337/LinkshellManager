using System;
using System.Net;
using System.Net.Http;
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

// P2.7 review: every Discord Activity request signs the caller in first, and that sign-in SAVES
// (a changed Discord name, say), inside the action ConcurrencyRetry wraps (Post, End Event, auction close, DKP
// audit all call ResolveAppUserAsync first). ConcurrencyRetry never repeats an action whose attempt
// already committed a save, so that one save turned the re-run off for everything sent from the
// Activity: a lost balance race always came back as a 409. The sign-in save is safe to repeat, so
// it is no longer counted -- but only when nothing else was staged before it.
public class ConcurrencyRetrySignInTests
{
    private const string Token = "6qrZcUqja7812RVdnEKjpzOL4CvHBF";

    private static ApplicationDbContext NewDb() => new(
        new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static DbUpdateConcurrencyException Conflict() => new("someone else saved this row");

    // Discord accepts the token and says it is user 42, who has renamed himself "bobby" since.
    private sealed class DiscordSaysBob : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "{\"application\":{\"id\":\"app\"},\"user\":{\"id\":\"42\",\"username\":\"bobby\"}}",
                    System.Text.Encoding.UTF8,
                    "application/json"),
            });
    }

    private sealed class Factory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new DiscordSaysBob(), disposeHandler: true);
    }

    // Bob already signed in once, linked and named, so the upsert only stores his new Discord name
    // and saves (no UserManager call, which these tests do not build).
    private static DiscordIdentityService SeedSignedInBob(ApplicationDbContext db)
    {
        db.Users.Add(new AppUser { Id = "bob", UserName = "bob", CharacterName = "Bob" });
        db.DiscordActivityUsers.Add(new DiscordActivityUser
        {
            DiscordUserId = "42", Username = "bob", IdentityUserId = "bob",
        });
        db.SaveChanges();
        db.ChangeTracker.Clear();

        return new DiscordIdentityService(
            new Factory(),
            db,
            null!,
            Microsoft.Extensions.Options.Options.Create(new DiscordOAuthOptions { ClientId = "app", ClientSecret = "secret" }),
            new MemoryCache(new MemoryCacheOptions()),
            NullLogger<DiscordIdentityService>.Instance);
    }

    [Fact]
    public async Task ActivitySignInSave_DoesNotStopTheRerun()
    {
        await using var db = NewDb();
        var identity = SeedSignedInBob(db);
        var attempts = 0;

        var result = await ConcurrencyRetry.RunAsync(db, async () =>
        {
            attempts++;
            var before = db.CommittedSaveCount;
            var signedIn = await identity.GetCurrentLocalUserAsync(Token, CancellationToken.None);
            Assert.Equal("bob", signedIn.AppUser?.Id);
            Assert.Equal(before, db.CommittedSaveCount); // it did save, but it is not counted
            if (attempts == 1)
            {
                throw Conflict(); // the action's own money save lost a race
            }
            return "paid";
        });

        Assert.Equal("paid", result);
        Assert.Equal(2, attempts);
        // The sign-in really saved: this is the save that used to turn the re-run off.
        var stored = await db.DiscordActivityUsers.AsNoTracking().SingleAsync();
        Assert.Equal("bobby", stored.Username);
    }

    // Anything staged before the scope opens would be committed by the first save inside it, and
    // that is not known to be safe to repeat: then the save still counts and the re-run stays off.
    [Fact]
    public async Task RepeatSafeScope_OverStagedWork_StillCountsTheSave()
    {
        await using var db = NewDb();
        db.Events.Add(new Event { LinkshellId = 1, EventName = "Faf" });

        var before = db.CommittedSaveCount;
        using (db.RepeatSafeSaves())
        {
            await db.SaveChangesAsync();
        }

        Assert.Equal(before + 1, db.CommittedSaveCount);
    }

    [Fact]
    public async Task RepeatSafeScope_EndsWithItsUsingBlock()
    {
        await using var db = NewDb();

        using (db.RepeatSafeSaves())
        {
            db.Events.Add(new Event { LinkshellId = 1, EventName = "Faf" });
            await db.SaveChangesAsync();
        }
        Assert.Equal(0, db.CommittedSaveCount);

        db.Events.Add(new Event { LinkshellId = 1, EventName = "Nid" });
        await db.SaveChangesAsync();
        Assert.Equal(1, db.CommittedSaveCount);
    }
}
