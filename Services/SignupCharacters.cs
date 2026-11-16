using LinkshellManagerDiscordApp.Models;

namespace LinkshellManagerDiscordApp.Services;

// Which character a member signs up as — their main or one of their alts. The main is the
// account's CharacterName and the alts are the account's Characters rows (slots 1 and 2; load them
// with AccountCharacters). Signup/participation records already store a CharacterName, so resolving
// to the right name is all that's needed — no schema change. Shared by the Discord Activity API and
// the Discord in-channel signup so the picker behaves identically everywhere.
public static class SignupCharacters
{
    // Ordered, de-duplicated selectable names for a member: main first, then alts.
    public static List<string> ForMember(AppUser? user, IEnumerable<Character>? characters)
    {
        var list = new List<string>();
        void Add(string? name)
        {
            var trimmed = name?.Trim();
            if (string.IsNullOrEmpty(trimmed)) return;
            if (!list.Any(existing => string.Equals(existing, trimmed, StringComparison.OrdinalIgnoreCase)))
            {
                list.Add(trimmed);
            }
        }

        var names = AccountCharacters.Names(user, characters);
        Add(names.Account);
        Add(names.Alt1);
        Add(names.Alt2);
        return list;
    }

    // The member's primary (main) character name — the default when no specific
    // character is requested.
    public static string Primary(AppUser? user, IEnumerable<Character>? characters)
        => ForMember(user, characters).FirstOrDefault()
           ?? user?.CharacterName
           ?? user?.UserName
           ?? "Member";

    // The character name to record for a signup given an optional requested name.
    // Falls back to the member's main when the request is blank or isn't one of
    // their own characters (so a stale/forged name can't be recorded).
    public static string Resolve(AppUser? user, IEnumerable<Character>? characters, string? requested)
    {
        var options = ForMember(user, characters);
        var main = options.FirstOrDefault() ?? user?.CharacterName ?? user?.UserName ?? "Member";
        if (string.IsNullOrWhiteSpace(requested)) return main;
        var match = options.FirstOrDefault(o => string.Equals(o, requested.Trim(), StringComparison.OrdinalIgnoreCase));
        return match ?? main;
    }

    // True when the member has at least one alt to choose from (so the UI only
    // shows a picker when there's actually a choice).
    public static bool HasAlternatives(AppUser? user, IEnumerable<Character>? characters)
        => ForMember(user, characters).Count > 1;
}
