using LinkshellManagerDiscordApp.Data;
using LinkshellManagerDiscordApp.Models;
using LinkshellManagerDiscordApp.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace LinkshellManager.Tests;

// Covers the input-validation branches of alt-character validation that run
// before any database query. The cross-member ILike conflict check is a
// PostgreSQL-only path and is exercised by integration tests, not here.
public class AltCharacterValidatorTests
{
    private static ApplicationDbContext NewInMemoryContext() =>
        new(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static AppUser NewUser() => new() { Id = "user-1", UserName = "tester" };

    [Fact]
    public async Task TwoIdenticalAlts_AreRejected()
    {
        using var db = NewInMemoryContext();
        var validator = new AltCharacterValidator(db);

        var (ok, error) = await validator.ValidateAsync(NewUser(), "Main", "Alt", "alt");

        Assert.False(ok);
        Assert.Contains("cannot be the same", error);
    }

    [Fact]
    public async Task AltMatchingMain_IsRejected()
    {
        using var db = NewInMemoryContext();
        var validator = new AltCharacterValidator(db);

        var (ok, error) = await validator.ValidateAsync(NewUser(), "Mainchar", "mainchar", null);

        Assert.False(ok);
        Assert.Contains("matches your main", error);
    }

    [Fact]
    public async Task NoAlts_AreAccepted()
    {
        using var db = NewInMemoryContext();
        var validator = new AltCharacterValidator(db);

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

        var validator = new AltCharacterValidator(db);

        // No AppUserLinkshell rows -> nothing to collide with -> valid.
        var (ok, error) = await validator.ValidateAsync(user, "Main", "UniqueAlt", null);

        Assert.True(ok);
        Assert.Null(error);
    }
}
