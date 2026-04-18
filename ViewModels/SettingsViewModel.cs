using LinkshellManagerDiscordApp.Models;

namespace LinkshellManagerDiscordApp.ViewModels;

public class SettingsViewModel
{
    public List<Linkshell> Linkshells { get; set; } = new();
    public int? SelectedLinkshellId { get; set; }
}
