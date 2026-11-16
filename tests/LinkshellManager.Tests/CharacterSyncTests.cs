using System.Reflection;
using LinkshellManagerDiscordApp.Data;
using LinkshellManagerDiscordApp.Models;
using LinkshellManagerDiscordApp.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace LinkshellManager.Tests;

// What is left of CharacterSync since Round 5 (R5.5), and the save hook's two guards. Characters is
// the only written source of names; the account columns are a mirror the save writes from it. The
// copy the other way (account columns into Characters, after every save) is gone: a change made
// straight on an existing account's columns is undone, and only a NEW account saved with names in its
// old columns has them taken as its characters. (These tests used to check that copy.)
public class CharacterSyncTests
{
    // The save writes every one of these from Characters, so a name, job or craft column added to the
    // account later must be added to the list too.
    [Fact]
    public void SourceProperties_AreExactlyTheAccountsNameJobAndCraftColumns()
    {
        var expected = typeof(AppUser)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Select(property => property.Name)
            .Where(name => name.Contains("CharacterName", StringComparison.Ordinal)
                || name.EndsWith("JobLevels", StringComparison.Ordinal)
                || name.EndsWith("StrongJobs", StringComparison.Ordinal)
                || name.EndsWith("MeritJobs", StringComparison.Ordinal)
                || name.EndsWith("CraftLevels", StringComparison.Ordinal))
            .OrderBy(name => name, StringComparer.Ordinal);

        Assert.Equal(expected, CharacterSync.SourceProperties.OrderBy(name => name, StringComparer.Ordinal));
        Assert.Equal(15, CharacterSync.SourceProperties.Count);
    }

    // A new account saved with only its old columns (as tests seed members) gets them as its
    // characters, trimmed, a repeated name and a name too long (CharacterWriter.MaxNameLength) left
    // out; CharacterName then follows the main's row. Since R5.6 the old columns are not rewritten:
    // they keep what the new account was saved with.
    [Fact]
    public async Task ANewAccountsOldColumns_BecomeItsCharacters_AndTheMainNameFollowsThem()
    {
        var options = NewOptions();
        await using (var db = new ApplicationDbContext(options))
        {
            db.Users.Add(new AppUser
            {
                Id = "u1", UserName = "u1", CharacterName = " Bob ", JobLevels = new[] { 75 },
                AltCharacterName1 = new string('x', CharacterWriter.MaxNameLength + 1), AltCharacterName2 = "BOB",
            });
            db.Users.Add(new AppUser { Id = "u2", UserName = "u2", CharacterName = "Ann", AltCharacterName2 = "Annalt" });
            await db.SaveChangesAsync();
        }

        Assert.Equal(new[] { ("u1", (short)0, "Bob", "bob"), ("u2", (short)0, "Ann", "ann"), ("u2", (short)2, "Annalt", "annalt") },
            await RowsAsync(options));
        await using var check = new ApplicationDbContext(options);
        var u1 = await check.Users.AsNoTracking().SingleAsync(u => u.Id == "u1");
        Assert.Equal(("Bob", new string('x', CharacterWriter.MaxNameLength + 1), "BOB"), (u1.CharacterName, u1.AltCharacterName1, u1.AltCharacterName2));
        Assert.Equal(new[] { 75 }, u1.JobLevels);
    }

    // An existing account's copy columns changed straight (not through CharacterWriter): the change
    // is undone, so they never change by hand. Other columns save as usual.
    [Fact]
    public async Task ChangingAnExistingAccountsColumnsStraight_IsUndone_AndTheRestSaves()
    {
        var options = NewOptions();
        await using (var db = new ApplicationDbContext(options))
        {
            db.Users.Add(new AppUser { Id = "u1", UserName = "u1", CharacterName = "Bob", AltCharacterName1 = "Bobalt" });
            await db.SaveChangesAsync();
        }
        await using (var db = new ApplicationDbContext(options))
        {
            var user = await db.Users.SingleAsync(u => u.Id == "u1");
            user.CharacterName = "Robert";
            user.AltCharacterName1 = null;
            user.Alt2JobLevels = new[] { 1 };
            user.TimeZone = "Europe/London";
            db.SaveChanges();
        }

        await using var check = new ApplicationDbContext(options);
        var saved = await check.Users.AsNoTracking().SingleAsync(u => u.Id == "u1");
        Assert.Equal(("Bob", "Bobalt", "Europe/London"), (saved.CharacterName, saved.AltCharacterName1, saved.TimeZone));
        Assert.Null(saved.Alt2JobLevels);
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

    // The C# rules the new-account guard (and TestCharacters) use.
    [Fact]
    public void DesiredRows_TrimSkipBlankAndTooLong_AndKeepTheFirstSlotOfARepeatedName()
    {
        var user = new AppUser
        {
            CharacterName = " Twin ", JobLevels = new[] { 1 },
            AltCharacterName1 = "TWIN", Alt1JobLevels = new[] { 2 },
            AltCharacterName2 = "Other\t", Alt2CraftLevels = new[] { 3 },
        };
        Assert.Equal(
            new[] { ((short)0, "Twin", "twin"), ((short)2, "Other", "other") },
            CharacterSync.DesiredRows(user).Select(row => (row.Slot, row.Name, row.NormalizedName)));
        Assert.Equal(new[] { 1 }, CharacterSync.DesiredRows(user)[0].JobLevels);
        Assert.Equal(new[] { 3 }, CharacterSync.DesiredRows(user)[1].CraftLevels);

        Assert.Equal(
            new[] { (short)1 },
            CharacterSync.DesiredRows(new AppUser { CharacterName = "  ", AltCharacterName1 = "Alt", AltCharacterName2 = new string('y', 257) })
                .Select(row => row.Slot));
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
