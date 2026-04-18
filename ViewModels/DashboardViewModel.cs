using LinkshellManagerDiscordApp.Models;

namespace LinkshellManagerDiscordApp.ViewModels;

public class DashboardViewModel
{
    public string? SelectedLinkshellName { get; set; }
    public List<Linkshell> Linkshells { get; set; } = new();
    public int? SelectedLinkshellId { get; set; }
    public List<AppUserLinkshell> Members { get; set; } = new();
    public List<Event> Events { get; set; } = new();
    public List<EventHistory> EventHistories { get; set; } = new();
    public int TotalMembers { get; set; }
    public int UpcomingEvents { get; set; }
    public int CompletedEvents { get; set; }
}
