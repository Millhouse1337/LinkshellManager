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

    // The InMemory database has no SQL: an account save there must not try the copy, and must work.
    [Fact]
    public async Task InMemorySave_DoesNotCopy_AndStillSaves()
    {
        await using var db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);
        db.Users.Add(new AppUser { Id = "u1", UserName = "u1", CharacterName = "Bob", AltCharacterName1 = new string('x', 300) });
        await db.SaveChangesAsync();
        var user = await db.Users.SingleAsync();
        user.AltCharacterName2 = "Bobalt";
        db.SaveChanges();

        Assert.Equal("Bobalt", (await db.Users.AsNoTracking().SingleAsync()).AltCharacterName2);
        Assert.Empty(await db.Characters.ToListAsync());
    }
}
