using LinkshellManagerDiscordApp.Data;
using LinkshellManagerDiscordApp.Models;
using LinkshellManagerDiscordApp.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace LinkshellManager.Tests;

// Startup super-admin seeding. It used to promote anyone whose main CharacterName (or
// email) matched appsettings.json, and CharacterName is user-editable. These tests pin that
// only the configured Discord user id, through the DiscordActivityUsers link, promotes.
public class SuperAdminSeedingTests
{
    private const string OwnerDiscordId = "123456789012345678";

    private static ApplicationDbContext NewDb() =>
        new(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static void AddLinkedUser(ApplicationDbContext db, string appUserId, string characterName, string discordUserId)
    {
        db.Users.Add(new AppUser { Id = appUserId, UserName = appUserId, CharacterName = characterName });
        db.DiscordActivityUsers.Add(new DiscordActivityUser
        {
            DiscordUserId = discordUserId,
            Username = appUserId,
            IdentityUserId = appUserId,
        });
    }

    [Fact]
    public async Task AUserNamedLikeTheOwner_IsNotPromoted()
    {
        using var db = NewDb();
        AddLinkedUser(db, "owner", "SomeMain", OwnerDiscordId);
        AddLinkedUser(db, "impostor", "Millhouse", "999999999999999999");
        await db.SaveChangesAsync();

        await SuperAdminSeeder.SeedAsync(db, OwnerDiscordId);

        Assert.False((await db.Users.SingleAsync(u => u.Id == "impostor")).IsSuperAdmin);
    }

    [Fact]
    public async Task TheConfiguredDiscordId_IsPromoted()
    {
        using var db = NewDb();
        AddLinkedUser(db, "owner", "SomeMain", OwnerDiscordId);
        await db.SaveChangesAsync();

        var result = await SuperAdminSeeder.SeedAsync(db, $"  {OwnerDiscordId}  ");

        Assert.Equal(SuperAdminSeeder.Outcome.Matched, result.Outcome);
        Assert.Equal(1, result.Promoted);
        Assert.True((await db.Users.SingleAsync(u => u.Id == "owner")).IsSuperAdmin);
    }

    [Fact]
    public async Task AlreadyPromoted_ReportsNothingNew()
    {
        using var db = NewDb();
        AddLinkedUser(db, "owner", "SomeMain", OwnerDiscordId);
        await db.SaveChangesAsync();

        await SuperAdminSeeder.SeedAsync(db, OwnerDiscordId);
        var second = await SuperAdminSeeder.SeedAsync(db, OwnerDiscordId);

        Assert.Equal(SuperAdminSeeder.Outcome.Matched, second.Outcome);
        Assert.Equal(0, second.Promoted);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Millhouse")]
    [InlineData("12345 678")]
    public async Task MissingOrNonNumericId_PromotesNobody(string? configured)
    {
        using var db = NewDb();
        AddLinkedUser(db, "owner", "Millhouse", OwnerDiscordId);
        await db.SaveChangesAsync();

        var result = await SuperAdminSeeder.SeedAsync(db, configured);

        Assert.Equal(SuperAdminSeeder.Outcome.NotConfigured, result.Outcome);
        Assert.False(await db.Users.AnyAsync(u => u.IsSuperAdmin));
    }

    [Fact]
    public async Task IdWithNoLinkedAccount_IsNoMatch()
    {
        using var db = NewDb();
        db.Users.Add(new AppUser { Id = "owner", UserName = "owner", CharacterName = "Millhouse" });
        // Discord row exists but was never linked to an account.
        db.DiscordActivityUsers.Add(new DiscordActivityUser { DiscordUserId = OwnerDiscordId, Username = "owner" });
        await db.SaveChangesAsync();

        var result = await SuperAdminSeeder.SeedAsync(db, OwnerDiscordId);

        Assert.Equal(SuperAdminSeeder.Outcome.NoMatch, result.Outcome);
        Assert.False(await db.Users.AnyAsync(u => u.IsSuperAdmin));
    }
}
