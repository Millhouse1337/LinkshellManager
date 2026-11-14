namespace LinkshellManagerDiscordApp.Services;

// Where the website's "Download launcher" link sends people. The address is fixed server config
// (Launcher:DownloadUrl in appsettings.json, or Launcher__DownloadUrl in the droplet's env file),
// not a setting anyone can change from the website: every visitor, signed in or not, downloads
// and runs whatever it points at. Only the on/off switch lives in the Admin tab.
public static class LauncherDownloadPolicy
{
    public const string ConfigKey = "Launcher:DownloadUrl";

    // The configured address, trimmed, or null when it is missing or not an absolute https://
    // address. A null keeps the link off (LauncherController answers 404).
    public static string? Validate(string? url)
    {
        var trimmed = url?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            return null;
        }

        return Uri.TryCreate(trimmed, UriKind.Absolute, out var parsed) && parsed.Scheme == Uri.UriSchemeHttps
            ? trimmed
            : null;
    }
}
