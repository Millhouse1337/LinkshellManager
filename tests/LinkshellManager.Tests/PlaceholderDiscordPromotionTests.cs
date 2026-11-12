using System.Net;
using LinkshellManagerDiscordApp.Data;
using LinkshellManagerDiscordApp.Models;
using LinkshellManagerDiscordApp.Options;
using LinkshellManagerDiscordApp.Services;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LinkshellManager.Tests;

// A first LSM launch takes over a placeholder carrying this person's Discord id
// (DiscordIdentityService), but only a CONFIRMED one: an officer set it, or the sign-up form made
// the placeholder for that Discord user. A Discord id that reached an imported member any other way
// must not hand over that member's DKP.
public class PlaceholderDiscordPromotionTests
{
    private const string Token = "6qrZcUqja7812RVdnEKjpzOL4CvHBF";

    private static ApplicationDbContext NewDb() => new(
        new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    // Discord accepts the token and says it is user 42 (no avatar, so nothing else is fetched).
    private sealed class DiscordSays42 : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "{\"application\":{\"id\":\"app\"},\"user\":{\"id\":\"42\",\"username\":\"bob\"}}",
                    System.Text.Encoding.UTF8,
                    "application/json"),
            });
    }

    private sealed class Factory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new DiscordSays42(), disposeHandler: true);
    }

    private static DiscordIdentityService NewService(ApplicationDbContext db) =>
        new(
            new Factory(),
            db,
            new UserManager<AppUser>(
                new UserStore<AppUser>(db),
                Microsoft.Extensions.Options.Options.Create(new IdentityOptions()),
                new PasswordHasher<AppUser>(),
                new[] { new UserValidator<AppUser>() },
                Array.Empty<IPasswordValidator<AppUser>>(),
                new UpperInvariantLookupNormalizer(),
                new IdentityErrorDescriber(),
                null!,
                NullLogger<UserManager<AppUser>>.Instance),
            Microsoft.Extensions.Options.Options.Create(new DiscordOAuthOptions { ClientId = "app", ClientSecret = "secret" }),
            new MemoryCache(new MemoryCacheOptions()),
            NullLogger<DiscordIdentityService>.Instance);

    private static void SeedPlaceholder(ApplicationDbContext db, bool confirmed)
    {
        db.Linkshells.Add(new Linkshell { Id = 1, LinkshellName = "LS" });
        db.Users.Add(new AppUser { Id = "ph", UserName = "placeholder-ph", CharacterName = "Bobalt", IsPlaceholder = true });
        db.AppUserLinkshells.Add(new AppUserLinkshell
        {
            Id = 1, LinkshellId = 1, AppUserId = "ph", CharacterName = "Bobalt", LinkshellDkp = 100,
            DiscordUserId = "42", DiscordIdConfirmed = confirmed,
        });
        db.SaveChanges();
    }

    [Fact]
    public async Task AConfirmedDiscordId_TakesThePlaceholderOverInPlace()
    {
        await using var db = NewDb();
        SeedPlaceholder(db, confirmed: true);

        var signedIn = await NewService(db).GetCurrentLocalUserAsync(Token, CancellationToken.None);

        Assert.Equal("ph", signedIn.AppUser?.Id);
        var user = await db.Users.AsNoTracking().SingleAsync();
        Assert.False(user.IsPlaceholder);
        var member = await db.AppUserLinkshells.AsNoTracking().SingleAsync();
        Assert.Null(member.DiscordUserId);
        Assert.False(member.DiscordIdConfirmed);
    }

    [Fact]
    public async Task AnUnconfirmedDiscordId_GetsANewAccount_AndLeavesThePlaceholderAlone()
    {
        await using var db = NewDb();
        SeedPlaceholder(db, confirmed: false);

        var signedIn = await NewService(db).GetCurrentLocalUserAsync(Token, CancellationToken.None);

        Assert.NotEqual("ph", signedIn.AppUser?.Id);
        Assert.True((await db.Users.AsNoTracking().SingleAsync(u => u.Id == "ph")).IsPlaceholder);
        Assert.Equal("ph", (await db.AppUserLinkshells.AsNoTracking().SingleAsync()).AppUserId);
    }
}
