using LinkshellManagerDiscordApp.Data;
using LinkshellManagerDiscordApp.Models;
using Microsoft.EntityFrameworkCore;

namespace LinkshellManagerDiscordApp.Services;

// WHICH MEMBER IS THIS CHARACTER? -- the one answer every path uses.
//
// A member can be standing in the world as any of three names: the account's main
// (AppUser.CharacterName) and the account's two alts (Characters slots 1 and 2). Charging loot,
// refunding it, checking a winner can afford it, crediting a window roster, a Claim Shield tag and
// the presence list all have to turn a typed or captured name into an account. Each used to do it
// with its own loop, and the loops drifted: some skipped the account name, so loot won under it
// was refunded on an edit but never charged at the close, and the tie-break differed between them.
//
// The rule, in one place:
//   - names are compared trimmed and ignoring capitals;
//   - a name is looked for in the three slots IN ORDER -- main, alt 1, alt 2 -- and the first slot
//     that has it decides. So a name that is one member's main and another member's alt belongs to
//     the member whose main it is: an alt is something a player types into their own profile, and
//     it must not take over somebody else's character;
//   - within one slot, the first member wins (memberships are fed in by Id, so "first" is the
//     oldest membership and the answer does not depend on the order the database returns rows);
//   - a membership with no account is never an answer: there is nobody to charge or credit.
//
// Until Round 5 the membership's own roster name was a fourth slot, checked first. It was always a
// copy of the account's main, so dropping it changed no answer (CharacterNameLookupParityTests
// proved it on a messy roster until Round5Contract dropped the roster name; see git history).
//
// RosterNameSet (LinkshellRosterNames.cs) is the yes/no version of the same three names.
public enum CharacterNameSlot
{
    Account = 1,
    Alt1 = 2,
    Alt2 = 3,
}

// One member's three names, in resolve order.
public readonly record struct CharacterNames(string? Account, string? Alt1, string? Alt2)
{
    public string? this[CharacterNameSlot slot] => slot switch
    {
        CharacterNameSlot.Account => Account,
        CharacterNameSlot.Alt1 => Alt1,
        _ => Alt2,
    };
}

// A resolved name: who, and which of their names it was (Presence shows "alt of <main>").
public readonly record struct CharacterMatch<TMember>(TMember Member, CharacterNameSlot Slot);

public sealed class CharacterResolver<TMember> where TMember : class
{
    private readonly Dictionary<string, CharacterMatch<TMember>> _byName;

    internal CharacterResolver(Dictionary<string, CharacterMatch<TMember>> byName)
    {
        _byName = byName;
    }

    public bool TryResolve(string? characterName, out CharacterMatch<TMember> match)
    {
        var key = CharacterResolver.Normalize(characterName);
        if (key is null)
        {
            match = default;
            return false;
        }
        return _byName.TryGetValue(key, out match);
    }

    // The member, or null for a blank name or one nobody on the roster has.
    public TMember? Resolve(string? characterName) =>
        TryResolve(characterName, out var match) ? match.Member : null;

    // For the one caller that learns a name mid-request (the addon's self-identification backfill
    // on a window post): adds it only if nobody has it already, the same first-write-wins rule.
    public void AddIfMissing(string? characterName, TMember member, CharacterNameSlot slot)
    {
        var key = CharacterResolver.Normalize(characterName);
        if (key is not null)
        {
            _byName.TryAdd(key, new CharacterMatch<TMember>(member, slot));
        }
    }
}

public static class CharacterResolver
{
    private static readonly CharacterNameSlot[] Slots =
    {
        CharacterNameSlot.Account,
        CharacterNameSlot.Alt1,
        CharacterNameSlot.Alt2,
    };

    // Trimmed, or null when there is nothing to look up. Capitals are handled by the dictionary.
    public static string? Normalize(string? characterName) =>
        string.IsNullOrWhiteSpace(characterName) ? null : characterName.Trim();

    // The general builder: each member with its three names. `members` must already be in the order
    // ties should go (oldest first), and must hold only members that can be charged or credited.
    public static CharacterResolver<TMember> Build<TMember>(
        IEnumerable<(TMember Member, CharacterNames Names)> members)
        where TMember : class
    {
        var list = members.ToList();
        var byName = new Dictionary<string, CharacterMatch<TMember>>(StringComparer.OrdinalIgnoreCase);
        foreach (var slot in Slots)
        {
            foreach (var (member, names) in list)
            {
                var key = Normalize(names[slot]);
                if (key is not null)
                {
                    byName.TryAdd(key, new CharacterMatch<TMember>(member, slot));
                }
            }
        }
        return new CharacterResolver<TMember>(byName);
    }

    // Memberships already in memory WITH their AppUser loaded (the main name lives there), plus the
    // accounts' Characters rows (AccountCharacters.LoadForLinkshellAsync) for the alts.
    public static CharacterResolver<AppUserLinkshell> ForMemberships(
        IEnumerable<AppUserLinkshell> memberships, IReadOnlyDictionary<string, Character[]> characters) =>
        Build(memberships
            .Where(link => !string.IsNullOrWhiteSpace(link.AppUserId))
            .OrderBy(link => link.Id)
            .Select(link => (link, AccountCharacters.Names(link.AppUser, AccountCharacters.For(characters, link.AppUserId)))));

    // Memberships paired with their account, for callers that join the two (often AsNoTracking).
    public static CharacterResolver<AppUserLinkshell> ForMemberships(
        IEnumerable<(AppUserLinkshell Membership, AppUser User)> membershipsWithUser,
        IReadOnlyDictionary<string, Character[]> characters) =>
        Build(membershipsWithUser
            .Where(pair => !string.IsNullOrWhiteSpace(pair.Membership.AppUserId))
            .OrderBy(pair => pair.Membership.Id)
            .Select(pair => (pair.Membership, AccountCharacters.Names(pair.User, AccountCharacters.For(characters, pair.Membership.AppUserId)))));

    // The linkshell's members with an account, TRACKED and with AppUser, for a caller that goes on
    // to move DKP through the membership it finds.
    public static async Task<CharacterResolver<AppUserLinkshell>> LoadMembershipsAsync(
        ApplicationDbContext db, int linkshellId, CancellationToken cancellationToken)
    {
        var memberships = await db.AppUserLinkshells
            .Include(link => link.AppUser)
            .Where(link => link.LinkshellId == linkshellId && link.AppUserId != null)
            .OrderBy(link => link.Id)
            .ToListAsync(cancellationToken);
        var characters = await AccountCharacters.LoadForLinkshellAsync(db, linkshellId, cancellationToken);
        return ForMemberships(memberships, characters);
    }
}
