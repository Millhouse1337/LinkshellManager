using LinkshellManagerDiscordApp.Data;
using LinkshellManagerDiscordApp.Models;
using LinkshellManagerDiscordApp.Services;

namespace LinkshellManager.Tests;

// Since Round 5 the name lookups read alts from Characters rows (AccountCharacters), not from the
// account columns. Tests that build memberships in memory (no database) still describe a member
// with the account columns; this turns those into the Characters rows the database would hold,
// by the rules a new account's old columns become its characters with (CharacterSync.DesiredRows).
internal static class TestCharacters
{
    public static Dictionary<string, Character[]> Of(IEnumerable<AppUserLinkshell> memberships) =>
        Of(memberships.Select(link => link.AppUser).OfType<AppUser>());

    public static Dictionary<string, Character[]> Of(IEnumerable<AppUser> users) =>
        AccountCharacters.ByAccount(users
            .GroupBy(user => user.Id, StringComparer.Ordinal)
            .Select(group => group.First())
            .SelectMany(user => CharacterSync.DesiredRows(user).Select(row => new Character
            {
                AppUserId = user.Id,
                Slot = row.Slot,
                Name = row.Name,
                NormalizedName = row.NormalizedName,
                JobLevels = row.JobLevels,
                StrongJobs = row.StrongJobs,
                MeritJobs = row.MeritJobs,
                CraftLevels = row.CraftLevels,
            })));
}
