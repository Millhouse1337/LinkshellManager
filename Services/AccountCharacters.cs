using LinkshellManagerDiscordApp.Data;
using LinkshellManagerDiscordApp.Models;
using Microsoft.EntityFrameworkCore;

namespace LinkshellManagerDiscordApp.Services;

// THE ACCOUNTS' CHARACTERS, read from the Characters table.
//
// Since Round 5 every read of an account's alt names, and of any character's jobs and crafts, goes
// through here. Characters holds one row per name (slot 0 the main, 1 and 2 the alts), already
// trimmed, with blank slots and a slot repeating the same account's name left out. A left-out
// repeat never changes an answer: the earlier slot gives the account that name first.
//
// The account's MAIN NAME is still AppUser.CharacterName (the account's display name). The roster
// name on a membership (AppUserLinkshell.CharacterName) is no longer read at all: it is only ever a
// copy of the account's main.
//
// Characters is also where names are WRITTEN, through CharacterWriter (R5.5); the account's
// CharacterName and old columns are copies the save keeps. Rows are read AsNoTracking.
public static class AccountCharacters
{
    // Every Characters row of every account that has a membership in the linkshell, by account.
    public static async Task<Dictionary<string, Character[]>> LoadForLinkshellAsync(
        ApplicationDbContext db, int linkshellId, CancellationToken cancellationToken)
    {
        var rows = await db.Characters
            .AsNoTracking()
            .Where(character => db.AppUserLinkshells.Any(link =>
                link.LinkshellId == linkshellId && link.AppUserId == character.AppUserId))
            .ToListAsync(cancellationToken);
        return ByAccount(rows);
    }

    // One account's rows, in slot order (empty when it has none).
    public static async Task<Character[]> LoadForAccountAsync(
        ApplicationDbContext db, string? appUserId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(appUserId))
        {
            return Array.Empty<Character>();
        }
        return await db.Characters
            .AsNoTracking()
            .Where(character => character.AppUserId == appUserId)
            .OrderBy(character => character.Slot)
            .ToArrayAsync(cancellationToken);
    }

    // Several accounts' rows, by account. An account with no rows is not in the dictionary.
    public static async Task<Dictionary<string, Character[]>> LoadForAccountsAsync(
        ApplicationDbContext db, IEnumerable<string?> appUserIds, CancellationToken cancellationToken)
    {
        var ids = appUserIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id!)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (ids.Count == 0)
        {
            return new Dictionary<string, Character[]>(StringComparer.Ordinal);
        }
        var rows = await db.Characters
            .AsNoTracking()
            .Where(character => ids.Contains(character.AppUserId))
            .ToListAsync(cancellationToken);
        return ByAccount(rows);
    }

    public static Dictionary<string, Character[]> ByAccount(IEnumerable<Character> rows) =>
        rows
            .GroupBy(character => character.AppUserId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.OrderBy(character => character.Slot).ToArray(), StringComparer.Ordinal);

    // The account's row for a slot (Character.MainSlot, Alt1Slot, Alt2Slot), or null when that slot
    // has no name.
    public static Character? InSlot(IEnumerable<Character>? characters, short slot) =>
        characters?.FirstOrDefault(character => character.Slot == slot);

    public static Character? MainCharacter(IEnumerable<Character>? characters) => InSlot(characters, Character.MainSlot);

    public static Character? Alt1(IEnumerable<Character>? characters) => InSlot(characters, Character.Alt1Slot);

    public static Character? Alt2(IEnumerable<Character>? characters) => InSlot(characters, Character.Alt2Slot);

    // The account's rows from a by-account dictionary, or null for no account / no rows.
    public static Character[]? For(IReadOnlyDictionary<string, Character[]> byAccount, string? appUserId) =>
        appUserId is not null && byAccount.TryGetValue(appUserId, out var characters) ? characters : null;

    // The account's three resolve names: its main (AppUser.CharacterName) and its two alts. When the
    // AppUser was not loaded, the main comes from the slot 0 row, which is the same name trimmed. No
    // account means no names, exactly as a membership with no account had none.
    public static CharacterNames Names(AppUser? user, IEnumerable<Character>? characters) =>
        new(user?.CharacterName ?? MainCharacter(characters)?.Name, Alt1(characters)?.Name, Alt2(characters)?.Name);

    // A member's name for a snapshot column (ledger, journal, loot): the account's main. Read from
    // the loaded AppUser when there is one (EF fills it in whenever the account is tracked too),
    // else from the database. Null for a membership with no account. This replaces reading the
    // membership's roster name, which was always a copy of the main.
    public static async Task<string?> MainNameAsync(
        ApplicationDbContext db, AppUserLinkshell? membership, CancellationToken cancellationToken)
    {
        if (membership?.AppUserId is not { } appUserId)
        {
            return null;
        }
        if (membership.AppUser is { } user)
        {
            return user.CharacterName;
        }
        return await db.Users
            .AsNoTracking()
            .Where(account => account.Id == appUserId)
            .Select(account => account.CharacterName)
            .FirstOrDefaultAsync(cancellationToken);
    }

    // How the account is shown when a name is needed: its main, else its login name.
    public static string DisplayName(AppUser user) => user.CharacterName ?? user.UserName ?? "Unknown";

    // The addon may publish job levels only for a character of the account its token was issued to.
    // Returns which of the owner's characters the name is (slot 0 main, 1 or 2 an alt) and the stored
    // spelling, or null when it is none of them (another member's main or alt included). Spaces
    // around a name and capitals do not matter.
    //
    // The owner's Characters rows are checked, lowest slot first. A name that is not there still
    // counts as the main when it is the account's own CharacterName (a main too long for the table).
    // The membership roster names are not read any more (Round 5): they only ever copied the main.
    // (This lived in MainCharacterJobs, whose writers CharacterWriter replaced in R5.5.)
    public static (short Slot, string Name)? OwnCharacterSlot(
        string? characterName,
        AppUser owner,
        IReadOnlyCollection<Character> ownerCharacters)
    {
        var name = characterName?.Trim();
        if (string.IsNullOrEmpty(name)) return null;

        bool Same(string? stored) => string.Equals(stored?.Trim(), name, StringComparison.OrdinalIgnoreCase);

        var character = ownerCharacters
            .Where(c => c.AppUserId == owner.Id && c.Slot is >= Character.MainSlot and <= Character.Alt2Slot)
            .OrderBy(c => c.Slot)
            .FirstOrDefault(c => Same(c.Name));
        if (character is not null) return (character.Slot, character.Name.Trim());

        return Same(owner.CharacterName) ? (Character.MainSlot, owner.CharacterName!.Trim()) : null;
    }
}
