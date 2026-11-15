using LinkshellManagerDiscordApp.Data;
using LinkshellManagerDiscordApp.Models;
using LinkshellManagerDiscordApp.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace LinkshellManager.Tests;

// CharacterNameValidator (formerly AltCharacterValidator): the account's own names must differ, and
// a NEW name (main or alt) must not be one another real member of a shared linkshell already uses.
// The InMemory database keeps Characters in step in C# (CharacterSync.DesiredRows), so the
// cross-member check runs here too; LinkshellManager.PostgresTests repeats the main cases on
// Postgres.
public class CharacterNameValidatorTests
{
    private const int Ls = 1;
    private const int OtherLs = 2;

    private static ApplicationDbContext NewInMemoryContext() =>
        new(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static AppUser NewUser() => new() { Id = "user-1", UserName = "tester" };

    // ---- the account's own names (kept from AltCharacterValidator) ---------------------------------

    [Fact]
    public async Task TwoIdenticalAlts_AreRejected()
    {
        using var db = NewInMemoryContext();
        var validator = new CharacterNameValidator(db);

        var (ok, error) = await validator.ValidateAsync(NewUser(), "Main", "Alt", "alt");

        Assert.False(ok);
        Assert.Contains("cannot be the same", error);
    }

    [Fact]
    public async Task AltMatchingMain_IsRejected()
    {
        using var db = NewInMemoryContext();
        var validator = new CharacterNameValidator(db);

        var (ok, error) = await validator.ValidateAsync(NewUser(), "Mainchar", "mainchar", null);

        Assert.False(ok);
        Assert.Contains("matches your main", error);
    }

    [Fact]
    public async Task NoAlts_AreAccepted()
    {
        using var db = NewInMemoryContext();
        var validator = new CharacterNameValidator(db);

        var (ok, error) = await validator.ValidateAsync(NewUser(), "Main", null, "   ");

        Assert.True(ok);
        Assert.Null(error);
    }

    [Fact]
    public async Task AltWithNoLinkshellMembership_IsAccepted()
    {
        using var db = NewInMemoryContext();
        var user = NewUser();
        db.Users.Add(user);
        await db.SaveChangesAsync();

        var validator = new CharacterNameValidator(db);

        // No AppUserLinkshell rows -> nothing to collide with -> valid.
        var (ok, error) = await validator.ValidateAsync(user, "Main", "UniqueAlt", null);

        Assert.True(ok);
        Assert.Null(error);
    }

    // An account that already repeats a name (saved before this check, or by Data Admin) can still
    // save when that repeat is not what changed; making a new repeat is refused.
    [Fact]
    public async Task AnOldRepeatInsideTheAccount_StillSaves_ButANewOneIsRefused()
    {
        using var db = NewInMemoryContext();
        var user = new AppUser { Id = "user-1", UserName = "tester", CharacterName = "Twin", AltCharacterName1 = "Twin" };
        var validator = new CharacterNameValidator(db);

        Assert.Equal((true, (string?)null), await validator.ValidateAsync(user, "Twin", "twin", "Third"));
        var (ok, error) = await validator.ValidateAsync(user, "Twin", "Twin", "TWIN");
        Assert.False(ok);
        Assert.Contains("cannot be the same", error);
    }

    [Fact]
    public async Task ANameLongerThanTheCharactersTableHolds_IsRefused_OnlyWhenItChanged()
    {
        using var db = NewInMemoryContext();
        var tooLong = new string('x', Character.MaxNameLength + 1);
        var validator = new CharacterNameValidator(db);

        var (ok, error) = await validator.ValidateAsync(NewUser(), "Main", tooLong, null);
        Assert.False(ok);
        Assert.Contains("at most 256 characters", error);

        var old = new AppUser { Id = "user-1", UserName = "tester", CharacterName = tooLong };
        Assert.Equal((true, (string?)null), await validator.ValidateAsync(old, tooLong, "Alt", null));
    }

    // ---- names other members use ----------------------------------------------------------------

    // Bob and Ann are real members of the linkshell; Pat is a placeholder there; Zed is a real
    // member of another linkshell only.
    private static async Task<(ApplicationDbContext Db, AppUser User)> SeedAsync()
    {
        var db = NewInMemoryContext();
        db.Linkshells.AddRange(new Linkshell { Id = Ls, LinkshellName = "LS" }, new Linkshell { Id = OtherLs, LinkshellName = "Other" });
        var user = new AppUser { Id = "me", UserName = "me", CharacterName = "Mine", AltCharacterName1 = "Bob" };
        db.Users.AddRange(
            user,
            new AppUser { Id = "bob", UserName = "bob", CharacterName = "Bob", AltCharacterName1 = "Bobalt" },
            new AppUser { Id = "ann", UserName = "ann", CharacterName = "Ann", AltCharacterName2 = " Annalt " },
            new AppUser { Id = "pat", UserName = "pat", CharacterName = "Pat", AltCharacterName1 = "Patalt", IsPlaceholder = true },
            new AppUser { Id = "zed", UserName = "zed", CharacterName = "Zed" });
        db.AppUserLinkshells.AddRange(
            new AppUserLinkshell { LinkshellId = Ls, AppUserId = "me", CharacterName = "Mine" },
            new AppUserLinkshell { LinkshellId = Ls, AppUserId = "bob", CharacterName = "Bob" },
            new AppUserLinkshell { LinkshellId = Ls, AppUserId = "ann", CharacterName = "Annie" },
            new AppUserLinkshell { LinkshellId = Ls, AppUserId = "pat", CharacterName = "Pat" },
            new AppUserLinkshell { LinkshellId = Ls, CharacterName = "Nobody" },
            new AppUserLinkshell { LinkshellId = OtherLs, AppUserId = "zed", CharacterName = "Zed" });
        await db.SaveChangesAsync();
        return (db, user);
    }

    [Theory]
    [InlineData("ann", "Bob", null, "ann")]          // a new main that is another member's main
    [InlineData("Mine", "Bob", "  bobalt ", "bobalt")] // a new alt that is another member's alt
    [InlineData("Mine", "Bob", "Annalt", "Annalt")]  // ... their alt 2
    [InlineData("ANNIE", "Bob", null, "ANNIE")]      // ... their roster name
    public async Task ANewNameAnotherRealMemberUses_IsRefused_WithAFriendlyMessage(
        string main, string alt1, string? alt2, string taken)
    {
        var (db, user) = await SeedAsync();
        using var _ = db;

        var (ok, error) = await new CharacterNameValidator(db).ValidateAsync(user, main, alt1, alt2);

        Assert.False(ok);
        Assert.Equal($"\"{taken.Trim()}\" is already used by another member of your linkshell. Please choose a different character name.", error);
    }

    [Theory]
    [InlineData("Pat", "Bob", "Patalt")] // a placeholder's names: the player claims them later
    [InlineData("Zed", "Bob", null)]     // a member of a linkshell this account is not in
    [InlineData("Nobody", "Bob", null)]  // a roster name with no account behind it
    [InlineData("Mine", "BOB", null)]    // the alt it already shares with Bob, in other capitals
    [InlineData("Bob", "Mine", null)]    // main and alt swapped: no new name
    [InlineData("Mine", "Bob", "Fresh")] // a name nobody uses
    public async Task OtherNames_AreAccepted(string main, string alt1, string? alt2)
    {
        var (db, user) = await SeedAsync();
        using var _ = db;

        Assert.Equal((true, (string?)null), await new CharacterNameValidator(db).ValidateAsync(user, main, alt1, alt2));
    }
}
