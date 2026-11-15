using LinkshellManagerDiscordApp.Data;
using LinkshellManagerDiscordApp.Models;
using Microsoft.EntityFrameworkCore;

namespace LinkshellManagerDiscordApp.Services;

// THE ACCOUNTS' CHARACTER NAMES, read from Characters (CharactersSwitch).
//
// The name lookups (CharacterResolver.LoadMembershipsAsync, LinkshellRosterNames.LoadCandidatesAsync
// and the addon's /roster alts) used to read an account's main and two alts from four columns of
// AspNetUsers. They now read Characters, the copy the app keeps of those columns (CharacterSync):
// one row per name, already trimmed, with blank slots and a slot repeating the same account's name
// left out. A left-out repeat never changes an answer: the earlier slot gives the account the same
// name first. The accounts are still where names are WRITTEN; only these reads moved.
public static class AccountCharacters
{
    // Every Characters row of every account that has a membership in the linkshell, by account.
    // Not tracked: a copy is never edited through EF.
    public static async Task<Dictionary<string, Character[]>> LoadForLinkshellAsync(
        ApplicationDbContext db, int linkshellId, CancellationToken cancellationToken)
    {
        var rows = await db.Characters
            .AsNoTracking()
            .Where(character => db.AppUserLinkshells.Any(link =>
                link.LinkshellId == linkshellId && link.AppUserId == character.AppUserId))
            .ToListAsync(cancellationToken);
        return rows
            .GroupBy(character => character.AppUserId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.OrderBy(character => character.Slot).ToArray(), StringComparer.Ordinal);
    }

    // The account's row for a slot (Character.MainSlot, Alt1Slot, Alt2Slot), or null when that slot
    // has no name.
    public static Character? InSlot(IReadOnlyCollection<Character>? characters, short slot) =>
        characters?.FirstOrDefault(character => character.Slot == slot);

    // The account's characters as the account's three resolve slots. No account (or no rows) means
    // no names, exactly as a membership with no account had none.
    public static CharacterNames Names(string? membershipCharacterName, IReadOnlyCollection<Character>? characters) =>
        new(membershipCharacterName,
            InSlot(characters, Character.MainSlot)?.Name,
            InSlot(characters, Character.Alt1Slot)?.Name,
            InSlot(characters, Character.Alt2Slot)?.Name);

    public static Character[]? For(Dictionary<string, Character[]> byAccount, string? appUserId) =>
        appUserId is not null && byAccount.TryGetValue(appUserId, out var characters) ? characters : null;
}
