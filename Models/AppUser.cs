using Microsoft.AspNetCore.Identity;

namespace LinkshellManagerDiscordApp.Models;

public class AppUser : IdentityUser
{
    public string? CharacterName { get; set; }

    public string? TimeZone { get; set; }

    public int? PrimaryLinkshellId { get; set; }

    public string? PrimaryLinkshellName { get; set; }

    public byte[]? ProfileImage { get; set; }

    public ICollection<AppUserLinkshell> AppUserLinkshells { get; set; } = new List<AppUserLinkshell>();

    public ICollection<AppUserEvent> AppUserEvents { get; set; } = new List<AppUserEvent>();

    public ICollection<AppUserEventHistory> AppUserEventHistories { get; set; } = new List<AppUserEventHistory>();

    public ICollection<Notification> Notifications { get; set; } = new List<Notification>();
}
