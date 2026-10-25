namespace LinkshellManagerDiscordApp.Services;

public enum LauncherUrlOutcome
{
    // Empty box: clear the URL (and switch the link off).
    Clear,
    Save,
    Invalid
}

// The rule for the launcher download URL a super admin pastes into the Activity's Admin tab
// (WebAdminController). Empty clears it; anything else must be an absolute https:// address,
// because the URL is handed to every visitor, including signed-out ones.
public static class LauncherDownloadPolicy
{
    public static LauncherUrlOutcome Check(string? url, out string? normalized)
    {
        normalized = url?.Trim();
        if (string.IsNullOrEmpty(normalized))
        {
            normalized = null;
            return LauncherUrlOutcome.Clear;
        }

        return Uri.TryCreate(normalized, UriKind.Absolute, out var parsed) && parsed.Scheme == Uri.UriSchemeHttps
            ? LauncherUrlOutcome.Save
            : LauncherUrlOutcome.Invalid;
    }
}
