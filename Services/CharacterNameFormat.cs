namespace LinkshellManagerDiscordApp.Services;

// What an FFXI character name can be, and how to tidy one a person typed.
//
// The game itself only ever issues names of 3 to 15 ASCII letters, one capital followed by
// lowercase. Anything else — a digit, a space, an apostrophe, an emoji, a Discord display name
// pasted in whole — can never match a character standing in the game, so it would sit on the
// roster unmatched forever: never credited by a scan, never found by the Lobby. Rejecting it at
// the form is the kind thing to do.
//
// Case is the one thing NOT rejected: "millhouse" and "MILLHOUSE" are both obviously Millhouse,
// so the name is put into the game's own form instead of bouncing the person for a shift key.
public static class CharacterNameFormat
{
    public const int MinLength = 3;
    public const int MaxLength = 15;

    // Tries to turn `raw` into a valid name. On success `normalized` is the game's form of it
    // (capital first letter, lowercase after). On failure `error` says what was wrong, in words
    // meant for the person who typed it.
    public static bool TryNormalize(string? raw, out string normalized, out string? error)
    {
        normalized = string.Empty;
        error = null;

        var trimmed = (raw ?? string.Empty).Trim();
        if (trimmed.Length == 0)
        {
            error = "Enter a character name.";
            return false;
        }

        foreach (var ch in trimmed)
        {
            if (!IsAsciiLetter(ch))
            {
                error = ch == ' '
                    ? "Character names are one word: letters only, no spaces."
                    : "Character names use letters A–Z only: no numbers, spaces, symbols or emoji.";
                return false;
            }
        }

        if (trimmed.Length < MinLength || trimmed.Length > MaxLength)
        {
            error = $"Character names are {MinLength} to {MaxLength} letters long.";
            return false;
        }

        normalized = ToGameCase(trimmed);
        return true;
    }

    // A best-effort prefill from something that is NOT a character name — a Discord display
    // name, say. Keeps the letters, drops everything else, caps the length, and gives up (empty)
    // when fewer than MinLength letters survive. Never an error: a prefill is a courtesy.
    public static string SuggestFrom(string? displayName)
    {
        var letters = new System.Text.StringBuilder(MaxLength);
        foreach (var ch in displayName ?? string.Empty)
        {
            if (!IsAsciiLetter(ch)) continue;
            letters.Append(ch);
            if (letters.Length == MaxLength) break;
        }
        return letters.Length < MinLength ? string.Empty : ToGameCase(letters.ToString());
    }

    private static bool IsAsciiLetter(char ch) => (ch >= 'a' && ch <= 'z') || (ch >= 'A' && ch <= 'Z');

    private static string ToGameCase(string letters)
        => char.ToUpperInvariant(letters[0]) + letters[1..].ToLowerInvariant();
}
