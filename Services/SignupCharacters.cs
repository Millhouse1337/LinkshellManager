using LinkshellManagerDiscordApp.Models;

namespace LinkshellManagerDiscordApp.Services;

// Which character a member signs up as — their main or one of their alts. Alts
// live on AppUser (AltCharacterName1/2); the roster "main" is the membership's
// CharacterName (falling back to the account main). Signup/participation records
// already store a CharacterName, so resolving to the right name is all that's
// needed — no schema change. Shared by the web, the Discord Activity API, and
// the Discord in-channel signup so the picker behaves identically everywhere.
public static class SignupCharacters
{
    // Ordered, de-duplicated selectable names for a member: main first, then alts.
    public static List<string> ForMember(AppUser? user, AppUserLinkshell? membership)
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

        Add(membership?.CharacterName);
        Add(user?.CharacterName);
        Add(user?.AltCharacterName1);
        Add(user?.AltCharacterName2);
        return list;
    }

    // The member's primary (main) character name — the default when no specific
    // character is requested.
    public static string Primary(AppUser? user, AppUserLinkshell? membership)
        => ForMember(user, membership).FirstOrDefault()
           ?? membership?.CharacterName
           ?? user?.CharacterName
           ?? user?.UserName
           ?? "Member";

    // The character name to record for a signup given an optional requested name.
    // Falls back to the member's main when the request is blank or isn't one of
    // their own characters (so a stale/forged name can't be recorded).
    public static string Resolve(AppUser? user, AppUserLinkshell? membership, string? requested)
    {
        var options = ForMember(user, membership);
        var main = options.FirstOrDefault()
                   ?? membership?.CharacterName ?? user?.CharacterName ?? user?.UserName ?? "Member";
        if (string.IsNullOrWhiteSpace(requested)) return main;
        var match = options.FirstOrDefault(o => string.Equals(o, requested.Trim(), StringComparison.OrdinalIgnoreCase));
        return match ?? main;
    }

    // True when the member has at least one alt to choose from (so the UI only
    // shows a picker when there's actually a choice).
    public static bool HasAlternatives(AppUser? user, AppUserLinkshell? membership)
        => ForMember(user, membership).Count > 1;
}
