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
