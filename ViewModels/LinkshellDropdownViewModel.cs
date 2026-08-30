using LinkshellManagerDiscordApp.Models;

namespace LinkshellManagerDiscordApp.ViewModels;

// Model for the LinkshellDropdown view component: the linkshells an account belongs to
// plus which one is active. It used to borrow SettingsViewModel, but the Settings page
// no longer picks a linkshell (the sidebar switcher does), so it carries its own.
public class LinkshellDropdownViewModel
{
    public List<Linkshell> Linkshells { get; set; } = new();

    public int? SelectedLinkshellId { get; set; }
}
