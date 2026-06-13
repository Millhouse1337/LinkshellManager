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

    // Per-job "strong" flags for the two alt characters, parallel to Alt1/Alt2
    // JobLevels and in the same FFXI-job-id-indexed format (see ProfileJobLevels).
    // A non-zero entry marks the job as well-geared/merited. Null when the alt has
    // no name / no flags set.
    [Column(TypeName = "jsonb")]
    public int[]? Alt1StrongJobs { get; set; }

    [Column(TypeName = "jsonb")]
    public int[]? Alt2StrongJobs { get; set; }

    // Per-job free-text merit notes for the two alt characters, catalog-aligned
    // (index 0 = WAR … 14 = SMN), parallel to the alts' Strong flags. Null when the
    // alt has no name / no merits set.
    [Column(TypeName = "jsonb")]
    public string[]? Alt1MeritJobs { get; set; }

    [Column(TypeName = "jsonb")]
    public string[]? Alt2MeritJobs { get; set; }

    // Per-craft levels for the main character and the two alts, in CraftCatalog
    // order (Alchemy, Bonecraft, …, Fishing). Crafting is account-level (it doesn't
    // vary by linkshell), so — unlike per-membership job levels — all three live
    // here on the account. Null when unset / the alt has no name.
    [Column(TypeName = "jsonb")]
    public int[]? CraftLevels { get; set; }

    [Column(TypeName = "jsonb")]
    public int[]? Alt1CraftLevels { get; set; }

    [Column(TypeName = "jsonb")]
    public int[]? Alt2CraftLevels { get; set; }

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
