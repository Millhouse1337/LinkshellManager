using LinkshellManagerDiscordApp.Models;

namespace LinkshellManagerDiscordApp.Data;

#pragma warning disable CS0618 // Round 5: the account's old name columns, read only to turn them into Characters rows.

// What is left of the old copy from the account columns into Characters (AddCharacters).
//
// Since Round 5 (R5.5) Characters is the only written source of names, jobs and crafts
// (CharacterWriter), and the account columns are a mirror the save writes FROM Characters
// (ApplicationDbContext.StampAccountsFromCharactersAsync). The copy the other way (a SQL copy after
// every save and again at every start-up) is gone. Two rules of it stay:
//
// - DesiredRows: the Characters rows an account's old columns describe. The save hook uses it for a
//   brand-new account saved with names in its old columns and no Characters rows (only tests do that
//   now), and the tests use it to describe members with the old columns.
// - NameNotBlankCheckSql: the CK_Characters_Name_NotBlank check (part of the schema).
//
// The rules: names are trimmed of the same whitespace .NET's Trim removes
// (TodDerivedSql.WhitespaceChars), a blank name has no row, a name longer than 256 characters has no
// row, NormalizedName is the lower-case name, and a slot that repeats an earlier slot's name in the
// same account has no row (the unique (AppUserId, NormalizedName) index allows one row per name per
// account).
public static class CharacterSync
{
    // The account's columns that are copies of its characters: the main's name first, then each
    // slot's jobs and crafts. The save hook writes every one of them from Characters.
    public static readonly IReadOnlyList<string> SourceProperties = new[]
    {
        nameof(AppUser.CharacterName), nameof(AppUser.JobLevels), nameof(AppUser.StrongJobs),
        nameof(AppUser.MeritJobs), nameof(AppUser.CraftLevels),
        nameof(AppUser.AltCharacterName1), nameof(AppUser.Alt1JobLevels), nameof(AppUser.Alt1StrongJobs),
        nameof(AppUser.Alt1MeritJobs), nameof(AppUser.Alt1CraftLevels),
        nameof(AppUser.AltCharacterName2), nameof(AppUser.Alt2JobLevels), nameof(AppUser.Alt2StrongJobs),
        nameof(AppUser.Alt2MeritJobs), nameof(AppUser.Alt2CraftLevels),
    };

    private const string Ws = TodDerivedSql.WhitespaceChars;

    // The check on Characters.Name: at least one character that is not whitespace.
    public static readonly string NameNotBlankCheckSql = $"\"Name\" ~ '[^{Ws}]'";

    // One wanted Characters row, worked out in C#. The arrays are the account's own (not copies).
    public sealed record DesiredRow(
        short Slot, string Name, string NormalizedName,
        int[]? JobLevels, int[]? StrongJobs, string[]? MeritJobs, int[]? CraftLevels);

    // The rows an account's old columns describe: .NET Trim, not blank, at most 256 characters
    // (counted as Postgres counts them), lower-cased with ToLowerInvariant, and a repeat of an
    // earlier slot's name left out.
    public static IReadOnlyList<DesiredRow> DesiredRows(AppUser user)
    {
        var slots = new (string? Name, int[]? Jobs, int[]? Strong, string[]? Merits, int[]? Crafts)[]
        {
            (user.CharacterName, user.JobLevels, user.StrongJobs, user.MeritJobs, user.CraftLevels),
            (user.AltCharacterName1, user.Alt1JobLevels, user.Alt1StrongJobs, user.Alt1MeritJobs, user.Alt1CraftLevels),
            (user.AltCharacterName2, user.Alt2JobLevels, user.Alt2StrongJobs, user.Alt2MeritJobs, user.Alt2CraftLevels),
        };
        var rows = new List<DesiredRow>(3);
        for (short slot = 0; slot < slots.Length; slot++)
        {
            var name = slots[slot].Name?.Trim();
            if (string.IsNullOrEmpty(name) || name.EnumerateRunes().Count() > Character.MaxNameLength)
            {
                continue;
            }
            var normalized = name.ToLowerInvariant();
            if (rows.Any(row => string.Equals(row.NormalizedName, normalized, StringComparison.Ordinal)))
            {
                continue;
            }
            rows.Add(new DesiredRow(slot, name, normalized,
                slots[slot].Jobs, slots[slot].Strong, slots[slot].Merits, slots[slot].Crafts));
        }
        return rows;
    }
}
