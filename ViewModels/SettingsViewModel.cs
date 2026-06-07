using LinkshellManagerDiscordApp.Models;

namespace LinkshellManagerDiscordApp.ViewModels;

public class SettingsViewModel
{
    public List<Linkshell> Linkshells { get; set; } = new();
    public int? SelectedLinkshellId { get; set; }

    // Super-admin-only global controls. The card is only rendered when
    // IsSuperAdmin is true; the controller action re-checks server-side.
    public bool IsSuperAdmin { get; set; }
    public bool AddonGloballyDisabled { get; set; }
}
