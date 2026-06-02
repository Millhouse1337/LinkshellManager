using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Identity;

namespace LinkshellManagerDiscordApp.Models;

public class AppUser : IdentityUser
{
    public string? CharacterName { get; set; }

    [MaxLength(64)]
    public string? AltCharacterName1 { get; set; }

    [MaxLength(64)]
    public string? AltCharacterName2 { get; set; }

    public string? TimeZone { get; set; }

    public int? PrimaryLinkshellId { get; set; }

    public string? PrimaryLinkshellName { get; set; }

    public byte[]? ProfileImage { get; set; }

    // App-wide super admin. NOT linkshell-scoped — grants access to global
    // server controls (e.g. the addon kill-switch on the Settings page).
    // Seeded on startup for the configured SuperAdmin account; see Program.cs.
    public bool IsSuperAdmin { get; set; }

    public ICollection<AppUserLinkshell> AppUserLinkshells { get; set; } = new List<AppUserLinkshell>();

    public ICollection<AppUserEvent> AppUserEvents { get; set; } = new List<AppUserEvent>();

    public ICollection<AppUserEventHistory> AppUserEventHistories { get; set; } = new List<AppUserEventHistory>();

    public ICollection<Notification> Notifications { get; set; } = new List<Notification>();
}
