using System.Reflection;
using LinkshellManagerDiscordApp.Data;
using LinkshellManagerDiscordApp.Models;
using LinkshellManagerDiscordApp.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace LinkshellManager.Tests;

// What is left of CharacterSync since Round 5, and the save hook's two guards. Characters is the only
// source of names, jobs and crafts; the account keeps only CharacterName, a copy of the main's name
// that the save writes. A change made straight to an existing account's CharacterName is undone,
// and only a NEW account saved with a CharacterName and no main row has it taken as its main.
// (These tests used to check the copy from the old account columns, which Round5Contract dropped.)
public class CharacterSyncTests
{
    // Round5Contract (R5.7) dropped the account's old alt, job and craft columns. The account's only
    // name column left is CharacterName, the main's name the save writes; a new one must not appear
    // without going through Characters.
    [Fact]
    public void TheAccountHasNoNameJobOrCraftColumn_ButTheMainName()
    {
        var names = typeof(AppUser)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Select(property => property.Name)
            .Where(name => name.Contains("CharacterName", StringComparison.Ordinal)
                || name.EndsWith("JobLevels", StringComparison.Ordinal)
                || name.EndsWith("StrongJobs", StringComparison.Ordinal)
                || name.EndsWith("MeritJobs", StringComparison.Ordinal)
                || name.EndsWith("CraftLevels", StringComparison.Ordinal));

        Assert.Equal(new[] { nameof(AppUser.CharacterName) }, names);
        Assert.DoesNotContain(typeof(AppUserLinkshell).GetProperties(), property => property.Name == "CharacterName");
    }

    // A new account saved with a CharacterName and no main row (as tests seed members) gets it as its
    // main, trimmed; a blank name and a name too long (CharacterWriter.MaxNameLength) give no row.
    // Alt rows saved with it are kept, and a main row saved with it wins over the CharacterName.
    // CharacterName then follows the main's row.
    [Fact]
    public async Task ANewAccountsCharacterName_BecomesItsMain_AndTheMainNameFollowsIt()
    {
        var options = NewOptions();
        var tooLong = new string('x', CharacterWriter.MaxNameLength + 1);
        await using (var db = new ApplicationDbContext(options))
        {
            db.Users.Add(new AppUser { Id = "u1", UserName = "u1", CharacterName = " Bob " });
            db.Users.Add(new AppUser { Id = "u2", UserName = "u2", CharacterName = "Ann" }.WithAlts(null, "Annalt"));
            db.Users.Add(new AppUser { Id = "u3", UserName = "u3", CharacterName = tooLong });
            db.Users.Add(new AppUser { Id = "u4", UserName = "u4", CharacterName = "Old" });
            TestCharacters.Add(db, TestCharacters.Row("u4", Character.MainSlot, "New", jobLevels: new[] { 75 }));
            db.Users.Add(new AppUser { Id = "u5", UserName = "u5", CharacterName = "  " });
            await db.SaveChangesAsync();
        }

        Assert.Equal(
            new[] { ("u1", (short)0, "Bob", "bob"), ("u2", (short)0, "Ann", "ann"), ("u2", (short)2, "Annalt", "annalt"), ("u4", (short)0, "New", "new") },
            await RowsAsync(options));
        await using var check = new ApplicationDbContext(options);
        var names = await check.Users.AsNoTracking().OrderBy(u => u.Id).Select(u => u.CharacterName).ToListAsync();
        Assert.Equal(new[] { "Bob", "Ann", tooLong, "New", "  " }, names);
    }

    // An existing account's CharacterName changed straight (not through CharacterWriter): the change
    // is undone, so it never changes by hand. Other columns save as usual.
    [Fact]
    public async Task ChangingAnExistingAccountsMainNameStraight_IsUndone_AndTheRestSaves()
    {
        var options = NewOptions();
        await using (var db = new ApplicationDbContext(options))
        {
            db.Users.Add(new AppUser { Id = "u1", UserName = "u1", CharacterName = "Bob" }.WithAlts("Bobalt"));
            await db.SaveChangesAsync();
        }
        await using (var db = new ApplicationDbContext(options))
        {
            var user = await db.Users.SingleAsync(u => u.Id == "u1");
            user.CharacterName = "Robert";
            user.TimeZone = "Europe/London";
            db.SaveChanges();
        }

        await using var check = new ApplicationDbContext(options);
        var saved = await check.Users.AsNoTracking().SingleAsync(u => u.Id == "u1");
        Assert.Equal(("Bob", "Europe/London"), (saved.CharacterName, saved.TimeZone));
        Assert.Equal(new[] { ("u1", (short)0, "Bob", "bob"), ("u1", (short)1, "Bobalt", "bobalt") }, await RowsAsync(options));
    }

    // The InMemory database has no foreign keys: a deleted account's rows are removed by the save.
    [Fact]
    public async Task ADeletedAccount_TakesItsCharactersWithIt()
    {
        var options = NewOptions();
        await using (var db = new ApplicationDbContext(options))
        {
            db.Users.Add(new AppUser { Id = "u1", UserName = "u1", CharacterName = "Bob" });
            db.Users.Add(new AppUser { Id = "u2", UserName = "u2", CharacterName = "Ann" });
            await db.SaveChangesAsync();
        }
        await using (var db = new ApplicationDbContext(options))
        {
            db.Users.Remove(await db.Users.SingleAsync(u => u.Id == "u2"));
            await db.SaveChangesAsync();
        }

        Assert.Equal(new[] { ("u1", (short)0, "Bob", "bob") }, await RowsAsync(options));
    }

    private static DbContextOptions<ApplicationDbContext> NewOptions() =>
        new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

    private static async Task<List<(string, short, string, string)>> RowsAsync(DbContextOptions<ApplicationDbContext> options)
    {
        await using var db = new ApplicationDbContext(options);
        return (await db.Characters.AsNoTracking().ToListAsync())
            .OrderBy(c => c.AppUserId, StringComparer.Ordinal).ThenBy(c => c.Slot)
            .Select(c => (c.AppUserId, c.Slot, c.Name, c.NormalizedName))
            .ToList();
    }
}
