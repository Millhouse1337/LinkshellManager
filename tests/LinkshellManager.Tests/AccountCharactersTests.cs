using LinkshellManagerDiscordApp.Data;
using LinkshellManagerDiscordApp.Models;
using LinkshellManagerDiscordApp.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace LinkshellManager.Tests;

// The Round 5 read helpers: an account's Characters rows (slot 0 the main, 1 and 2 the alts) and
// the main's name for snapshot columns. The InMemory save copies the account columns into
// Characters the same way Postgres does (CharacterSync), so seeding the columns is enough.
public class AccountCharactersTests
{
    private static ApplicationDbContext NewDb() =>
        new(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static async Task<ApplicationDbContext> SeededAsync()
    {
        var db = NewDb();
        db.Users.AddRange(
            new AppUser
            {
                Id = "u1", UserName = "one", CharacterName = " Main ", AltCharacterName1 = "Altone", AltCharacterName2 = "Alttwo",
                JobLevels = new[] { 75 }, Alt2JobLevels = new[] { 40 },
            },
            new AppUser { Id = "u2", UserName = "two", AltCharacterName2 = "Onlyalt" },
            new AppUser { Id = "u3", UserName = "three" });
        db.AppUserLinkshells.AddRange(
            new AppUserLinkshell { Id = 1, LinkshellId = 7, AppUserId = "u1" },
            new AppUserLinkshell { Id = 2, LinkshellId = 7, AppUserId = "u2" },
            new AppUserLinkshell { Id = 3, LinkshellId = 8, AppUserId = "u3" });
        await db.SaveChangesAsync();
        return db;
    }

    [Fact]
    public async Task LoadForAccount_GivesTheRowsInSlotOrder_Trimmed()
    {
        await using var db = await SeededAsync();

        var rows = await AccountCharacters.LoadForAccountAsync(db, "u1", CancellationToken.None);

        Assert.Equal(new short[] { 0, 1, 2 }, rows.Select(row => row.Slot));
        Assert.Equal("Main", AccountCharacters.MainCharacter(rows)?.Name);
        Assert.Equal("Altone", AccountCharacters.Alt1(rows)?.Name);
        Assert.Equal("Alttwo", AccountCharacters.Alt2(rows)?.Name);
        Assert.Equal(new[] { 75 }, AccountCharacters.MainCharacter(rows)?.JobLevels);
        Assert.Equal(new[] { 40 }, AccountCharacters.Alt2(rows)?.JobLevels);
        Assert.Empty(await AccountCharacters.LoadForAccountAsync(db, null, CancellationToken.None));
        Assert.Empty(await AccountCharacters.LoadForAccountAsync(db, "u3", CancellationToken.None));
    }

    [Fact]
    public async Task ABlankSlot_HasNoRow()
    {
        await using var db = await SeededAsync();

        var rows = await AccountCharacters.LoadForAccountAsync(db, "u2", CancellationToken.None);

        Assert.Null(AccountCharacters.MainCharacter(rows));
        Assert.Null(AccountCharacters.Alt1(rows));
        Assert.Equal("Onlyalt", AccountCharacters.Alt2(rows)?.Name);
    }

    [Fact]
    public async Task LoadForAccountsAndForLinkshell_GroupByAccount()
    {
        await using var db = await SeededAsync();

        var several = await AccountCharacters.LoadForAccountsAsync(db, new[] { "u1", "u2", null, "u1", "nobody" }, CancellationToken.None);
        var linkshell = await AccountCharacters.LoadForLinkshellAsync(db, 7, CancellationToken.None);

        Assert.Equal(new[] { "u1", "u2" }, several.Keys.OrderBy(id => id, StringComparer.Ordinal));
        Assert.Equal(new[] { "u1", "u2" }, linkshell.Keys.OrderBy(id => id, StringComparer.Ordinal));
        Assert.Equal(3, AccountCharacters.For(several, "u1")!.Length);
        Assert.Null(AccountCharacters.For(several, "nobody"));
        Assert.Null(AccountCharacters.For(several, null));
        Assert.Empty(await AccountCharacters.LoadForAccountsAsync(db, Array.Empty<string?>(), CancellationToken.None));
    }

    [Fact]
    public async Task MainName_ReadsTheLoadedAccount_ElseTheDatabase()
    {
        await using var db = await SeededAsync();
        var loaded = new AppUserLinkshell { AppUserId = "u1", AppUser = new AppUser { Id = "u1", CharacterName = "Loaded" } };
        var notLoaded = await db.AppUserLinkshells.AsNoTracking().SingleAsync(link => link.Id == 1);

        Assert.Equal("Loaded", await AccountCharacters.MainNameAsync(db, loaded, CancellationToken.None));
        // The account's main name is a copy of its (trimmed) main character since R5.5.
        Assert.Equal("Main", await AccountCharacters.MainNameAsync(db, notLoaded, CancellationToken.None));
        Assert.Null(await AccountCharacters.MainNameAsync(db, new AppUserLinkshell(), CancellationToken.None));
        Assert.Null(await AccountCharacters.MainNameAsync(db, null, CancellationToken.None));
    }

    [Fact]
    public void DisplayName_IsTheMain_ElseTheLoginName()
    {
        Assert.Equal("Main", AccountCharacters.DisplayName(new AppUser { UserName = "login", CharacterName = "Main" }));
        Assert.Equal("login", AccountCharacters.DisplayName(new AppUser { UserName = "login" }));
        Assert.Equal("Unknown", AccountCharacters.DisplayName(new AppUser()));
    }
}
