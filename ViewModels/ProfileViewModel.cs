using System.ComponentModel.DataAnnotations;

namespace LinkshellManagerDiscordApp.ViewModels;

public class ProfileViewModel
{
    public string? CharacterName { get; set; }

    [StringLength(64)]
    public string? AltCharacterName1 { get; set; }

    [StringLength(64)]
    public string? AltCharacterName2 { get; set; }

    public string? TimeZone { get; set; }
    public byte[]? ProfileImageData { get; set; }

    // Per-job levels for the 15 classic jobs, in EventJobCatalog.MainJobOptions
    // order (index 0 = WAR ... 14 = SMN). Round-trips through the form; persisted
    // to the user's linkshell memberships in the addon's FFXI-job-id format via
    // ProfileJobLevels. Empty when the user has no linkshell (nothing to store).
    public List<int> JobLevels { get; set; } = new();

    // Same per-job level lists for the two alt characters (catalog order).
    // Persisted on the account (AppUser.Alt1JobLevels / Alt2JobLevels).
    public List<int> Alt1JobLevels { get; set; } = new();
    public List<int> Alt2JobLevels { get; set; } = new();

    // Populated by the controller from IDateTimeZoneProvider so the Profile view
    // can render a dropdown instead of a free-form input. Not posted back: the
    // server validates the submitted TimeZone against the provider directly.
    public IReadOnlyList<string> AvailableTimeZones { get; set; } = Array.Empty<string>();

    // Onboarding state surfaced into the Profile view. Mirrors the Discord
    // activity's "Next Steps" checklist so the web shell exposes the same
    // three milestones once a user is signed in.
    public bool ProfileComplete { get; set; }
    public bool HasLinkshell { get; set; }
    public bool AddonConfigured { get; set; }
}
