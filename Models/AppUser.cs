using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.AspNetCore.Identity;

namespace LinkshellManagerDiscordApp.Models;

public class AppUser : IdentityUser
{
    public string? CharacterName { get; set; }

    [MaxLength(64)]
    public string? AltCharacterName1 { get; set; }

    [MaxLength(64)]
    public string? AltCharacterName2 { get; set; }

    // Per-job levels for the two alt characters, in the same FFXI-job-id-indexed
    // format as AppUserLinkshell.JobLevels (see ProfileJobLevels). Alts have no
    // linkshell membership of their own, so their job lists live here on the
    // account. Null when the alt has no name / no levels set.
    [Column(TypeName = "jsonb")]
    public int[]? Alt1JobLevels { get; set; }

    [Column(TypeName = "jsonb")]
    public int[]? Alt2JobLevels { get; set; }

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
