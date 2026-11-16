using LinkshellManagerDiscordApp.Data;
using LinkshellManagerDiscordApp.Models;
using LinkshellManagerDiscordApp.Services;
using Microsoft.EntityFrameworkCore;

namespace LinkshellManager.Tests;

// Since Round 5 an account's names, jobs and crafts live only in its Characters rows (the old account
// columns were dropped by Round5Contract). Tests describe a member with its account's CharacterName
// (the main) plus Characters rows for anything else:
// - Row: one Characters row (an alt, or a main with jobs);
// - Add: adds rows for a test account to a context. A new account saved with a CharacterName and no
//   main row of its own gets that name as its main (the save hook), so a test only adds the alts;
// - Of: the rows the database would hold for memberships built in memory (no database): each
//   account's main from its CharacterName, plus the given extra rows; or a seeded database's rows.
internal static class TestCharacters
{
    public static Character Row(
        string accountId, short slot, string name,
        int[]? jobLevels = null, int[]? strongJobs = null, string[]? meritJobs = null, int[]? craftLevels = null)
    {
        // Saved rows are trimmed by the save hook; the name is kept as given so a test can check that.
        return new Character
        {
            AppUserId = accountId,
            Slot = slot,
            Name = name,
            NormalizedName = CharacterWriter.Normalize(name.Trim()),
            JobLevels = jobLevels,
            StrongJobs = strongJobs,
            MeritJobs = meritJobs,
            CraftLevels = craftLevels,
            UpdatedAt = DateTime.UtcNow,
        };
    }

    // The account's alts as rows (a blank name is no row).
    public static Character[] Alts(string accountId, string? alt1, string? alt2 = null) =>
        new[] { (Slot: Character.Alt1Slot, Name: alt1), (Slot: Character.Alt2Slot, Name: alt2) }
            .Where(alt => !string.IsNullOrWhiteSpace(alt.Name))
            .Select(alt => Row(accountId, alt.Slot, alt.Name!))
            .ToArray();

    public static void Add(ApplicationDbContext db, params Character[] rows) => db.Characters.AddRange(rows);

    public static void AddAlts(ApplicationDbContext db, string accountId, string? alt1, string? alt2 = null) =>
        db.Characters.AddRange(Alts(accountId, alt1, alt2));

    public static Dictionary<string, Character[]> Of(IEnumerable<AppUserLinkshell> memberships, params Character[] extra) =>
        Of(memberships.Select(link => link.AppUser).OfType<AppUser>(), extra);

    // Each account's rows: those on its Characters list, plus the extra rows, plus (when neither has a
    // slot 0 row) its main from CharacterName by the save hook's rule for a new account: trimmed, none
    // when blank.
    public static Dictionary<string, Character[]> Of(IEnumerable<AppUser> users, params Character[] extra)
    {
        var accounts = users
            .GroupBy(user => user.Id, StringComparer.Ordinal)
            .Select(group => group.First())
            .ToList();
        var listed = accounts.SelectMany(user => user.Characters).Concat(extra).ToList();
        var mains = accounts
            .Where(user => !string.IsNullOrWhiteSpace(user.CharacterName)
                && !listed.Any(row => row.Slot == Character.MainSlot && string.Equals(row.AppUserId, user.Id, StringComparison.Ordinal)))
            .Select(user => Row(user.Id, Character.MainSlot, user.CharacterName!.Trim()));
        return AccountCharacters.ByAccount(mains.Concat(listed));
    }

    // A test account built in memory (no database) with its alts on its Characters list, so Of finds
    // them.
    public static AppUser WithAlts(this AppUser user, string? alt1, string? alt2 = null)
    {
        foreach (var row in Alts(user.Id, alt1, alt2))
        {
            user.Characters.Add(row);
        }
        return user;
    }

    // Every account's saved rows, for a test that seeded a database and then resolves names in memory.
    public static Dictionary<string, Character[]> Of(ApplicationDbContext db) =>
        AccountCharacters.ByAccount(db.Characters.AsNoTracking().ToList());
}
