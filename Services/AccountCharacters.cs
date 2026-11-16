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
// The old AspNetUsers columns are still where names are WRITTEN in this build (CharacterSync copies
// them into Characters on every save). Rows are read AsNoTracking, so a read after a save sees
// what that save's copy wrote.
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
}
