using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LinkshellManagerDiscordApp.Data;
using LinkshellManagerDiscordApp.Models;
using LinkshellManagerDiscordApp.Services;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace LinkshellManager.PostgresTests;

// The profile save on real Postgres: CharacterNameValidator reads other members' names from
// Characters (lower() on the server, ToLowerInvariant in the app), refuses a new main or alt that
// another real member of the linkshell uses, lets an old clash save when its name did not change,
// and also checks the main on the save that keeps the alts as they are.
[Collection("postgres")]
public class CharacterNameValidatorPostgresTests : IAsyncLifetime
{
    private readonly PostgresFixture _pg;

    public CharacterNameValidatorPostgresTests(PostgresFixture pg) => _pg = pg;

    public Task InitializeAsync() => _pg.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task ProfileSave_RefusesANewClash_AndKeepsAnOldOne()
    {
        await using (var db = _pg.CreateContext())
        {
            var linkshell = new Linkshell { LinkshellName = "LS" };
            db.Linkshells.Add(linkshell);
            db.Users.AddRange(
                // "me" already shares "Bob" with Bob (from before the check existed).
                new AppUser { Id = "me", UserName = "me", CharacterName = "Mine", AltCharacterName1 = "Bob" },
                new AppUser { Id = "bob", UserName = "bob", CharacterName = "Bob", AltCharacterName2 = "Ébène" },
                new AppUser { Id = "pat", UserName = "pat", CharacterName = "Pat", IsPlaceholder = true });
            db.AppUserLinkshells.AddRange(
                new AppUserLinkshell { Linkshell = linkshell, AppUserId = "me", CharacterName = "Mine" },
                new AppUserLinkshell { Linkshell = linkshell, AppUserId = "bob", CharacterName = "Bob" },
                new AppUserLinkshell { Linkshell = linkshell, AppUserId = "pat", CharacterName = "Pat" });
            await db.SaveChangesAsync();
        }

        // A new alt that is Bob's (non-ASCII, other capitals): refused, nothing saved.
        var refused = await SaveAsync((service, me) => service.UpdateProfileAsync(
            me, "Mine", "UTC", "Bob", " éBÈNE ", preserveExistingAlts: false, CancellationToken.None));
        Assert.False(refused.Succeeded);
        Assert.Equal("CharacterNameConflict", refused.Errors.Single().Code);
        Assert.Equal("\"éBÈNE\" is already used by another member of your linkshell. Please choose a different character name.",
            refused.Errors.Single().Description);
        Assert.Null(await AltTwoAsync());

        // The old clash ("Bob") is kept and a placeholder's name is free to take: saved and copied.
        var saved = await SaveAsync((service, me) => service.UpdateProfileAsync(
            me, "Mine", "UTC", "Bob", "Pat", preserveExistingAlts: false, CancellationToken.None));
        Assert.True(saved.Succeeded, string.Join("; ", saved.Errors.Select(e => e.Description)));
        Assert.Equal("Pat", await AltTwoAsync());
        await using (var check = _pg.CreateContext())
        {
            Assert.Equal(new[] { "Mine", "Bob", "Pat" },
                (await check.Characters.AsNoTracking().Where(c => c.AppUserId == "me").OrderBy(c => c.Slot).ToListAsync()).Select(c => c.Name));
        }

        // The save that keeps the alts still checks the main.
        var mainRefused = await SaveAsync((service, me) => service.UpdateProfileAsync(me, "ÉBÈNE", "UTC", CancellationToken.None));
        Assert.False(mainRefused.Succeeded);
        Assert.Contains("\"ÉBÈNE\" is already used", mainRefused.Errors.Single().Description);
    }

    // One profile save in its own context, as one request would make it.
    private async Task<IdentityResult> SaveAsync(Func<AppUserProfileService, AppUser, Task<IdentityResult>> save)
    {
        await using var db = _pg.CreateContext();
        var me = await db.Users.SingleAsync(u => u.Id == "me");
        return await save(new AppUserProfileService(db, NewUserManager(db), new CharacterNameValidator(db)), me);
    }

    private async Task<string?> AltTwoAsync()
    {
        await using var db = _pg.CreateContext();
        return await db.Characters.AsNoTracking()
            .Where(c => c.AppUserId == "me" && c.Slot == Character.Alt2Slot).Select(c => c.Name).SingleOrDefaultAsync();
    }

    private static UserManager<AppUser> NewUserManager(ApplicationDbContext db) => new(
        new UserStore<AppUser>(db),
        Options.Create(new IdentityOptions()),
        new PasswordHasher<AppUser>(),
        Array.Empty<IUserValidator<AppUser>>(),
        Array.Empty<IPasswordValidator<AppUser>>(),
        new UpperInvariantLookupNormalizer(),
        new IdentityErrorDescriber(),
        null!,
        NullLogger<UserManager<AppUser>>.Instance);
}
