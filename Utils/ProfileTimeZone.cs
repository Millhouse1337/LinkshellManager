namespace LinkshellManagerDiscordApp.Utils;

// When a member's profile time zone counts as "never chosen", so the app may fill it in from the
// browser (POST profile/detect-time-zone). Only null/blank. "UTC" is a zone people really pick;
// it used to count as unset here and was replaced on the member's next visit.
public static class ProfileTimeZone
{
    public static bool IsUnset(string? timeZone) => string.IsNullOrWhiteSpace(timeZone);
}
