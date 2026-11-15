using System.Reflection;
using LinkshellManagerDiscordApp.Data;
using LinkshellManagerDiscordApp.Models;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace LinkshellManager.Tests;

// The plain-C# side of CharacterSync (AddCharacters). The copy itself is SQL and is tested on real
// Postgres (CharacterSyncTests and AddCharactersRoundTripTests in LinkshellManager.PostgresTests).
public class CharacterSyncTests
{
    // A save is copied only when one of these changed, so a name, job or craft column added to the
    // account later must be added to the list too (and to the copy's SQL).
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

    [Fact]
    public void TooLongNames_CountsTrimmedCharacters_PerSlot()
    {
        var user = new AppUser
        {
            CharacterName = "  " + new string('a', Character.MaxNameLength) + " ",
            AltCharacterName1 = new string('b', Character.MaxNameLength + 1),
            // 200 emoji are 400 UTF-16 chars but 200 characters to Postgres's char_length.
            AltCharacterName2 = string.Concat(Enumerable.Repeat("\U0001F600", 200)),
        };

        Assert.Equal(new[] { ((short)1, Character.MaxNameLength + 1) }, CharacterSync.TooLongNames(user));
        Assert.Empty(CharacterSync.TooLongNames(new AppUser { CharacterName = "   " }));
    }

    // The InMemory database has no SQL, but the name lookups read Characters (CharactersSwitch), so
    // there the copy is made in C# by the same rules: saves still work, and the rows follow every
    // add, edit and delete. (Before CharactersSwitch the InMemory copy was skipped altogether.)
    [Fact]
    public async Task InMemorySave_CopiesByTheSameRules_AndStillSaves()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        await using (var db = new ApplicationDbContext(options))
        {
            db.Users.Add(new AppUser { Id = "u1", UserName = "u1", CharacterName = " Bob ", AltCharacterName1 = new string('x', 300), JobLevels = new[] { 75 } });
            db.Users.Add(new AppUser { Id = "u2", UserName = "u2", CharacterName = "Ann" });
            Assert.Equal(2, await db.SaveChangesAsync());
            var user = await db.Users.SingleAsync(u => u.Id == "u1");
            user.AltCharacterName2 = "Bobalt";
            db.SaveChanges();
            Assert.Equal("Bobalt", (await db.Users.AsNoTracking().SingleAsync(u => u.Id == "u1")).AltCharacterName2);
        }

        Assert.Equal(new[] { ("u1", (short)0, "Bob", "bob"), ("u1", (short)2, "Bobalt", "bobalt"), ("u2", (short)0, "Ann", "ann") },
            await RowsAsync(options));

        // A rename, a repeat of the main (left out), and a deleted account.
        await using (var db = new ApplicationDbContext(options))
        {
            var user = await db.Users.SingleAsync(u => u.Id == "u1");
            user.CharacterName = "Robert";
            user.AltCharacterName2 = "ROBERT";
            db.Users.Remove(await db.Users.SingleAsync(u => u.Id == "u2"));
            await db.SaveChangesAsync();
        }
        Assert.Equal(new[] { ("u1", (short)0, "Robert", "robert") }, await RowsAsync(options));
        await using (var check = new ApplicationDbContext(options))
        {
            Assert.Equal(new[] { 75 }, (await check.Characters.SingleAsync()).JobLevels);
        }
    }

    private static async Task<List<(string, short, string, string)>> RowsAsync(DbContextOptions<ApplicationDbContext> options)
    {
        await using var db = new ApplicationDbContext(options);
        return (await db.Characters.AsNoTracking().ToListAsync())
            .OrderBy(c => c.AppUserId, StringComparer.Ordinal).ThenBy(c => c.Slot)
            .Select(c => (c.AppUserId, c.Slot, c.Name, c.NormalizedName))
            .ToList();
    }

    // The C# rules the InMemory copy uses, which Postgres tests compare with the SQL.
    [Fact]
    public void DesiredRows_TrimSkipBlankAndTooLong_AndKeepTheFirstSlotOfARepeatedName()
    {
        var user = new AppUser
        {
            CharacterName = " Twin ", JobLevels = new[] { 1 },
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
}
