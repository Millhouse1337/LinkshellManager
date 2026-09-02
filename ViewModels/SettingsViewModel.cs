namespace LinkshellManagerDiscordApp.ViewModels;

// The Settings page is server-wide switches only -- the active linkshell is picked in
// the sidebar (LinkshellController.Select), not here.
public class SettingsViewModel
{
    // Super-admin-only global controls. The card is only rendered when
    // IsSuperAdmin is true; the controller action re-checks server-side.
    public bool IsSuperAdmin { get; set; }
    public bool AddonGloballyDisabled { get; set; }

    // Server-wide Claim Shield switch. Independent of the addon kill-switch above: this turns off
    // one feature everywhere without taking the addon down.
    public bool ClaimShieldGloballyDisabled { get; set; }

    // App-wide permission override: full permissions in every linkshell the super
    // admin is a member of, plus an ADMIN badge beside their real rank.
    public bool AdminOverrideEnabled { get; set; }

    // Controls the "Download Launcher" button on the public home page and in the sidebar,
    // and the absolute URL it points at.
    public bool LauncherDownloadEnabled { get; set; }
    public string? LauncherDownloadUrl { get; set; }
}
