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

    // Per-job "strong" flags (catalog order), parallel to the level lists above.
    // True = the member marked the job well-geared/merited. Round-trip through the
    // form's Strong toggles; persisted via ProfileJobLevels.MergeFlagsIntoStored.
    public List<bool> StrongJobs { get; set; } = new();
    public List<bool> Alt1StrongJobs { get; set; } = new();
    public List<bool> Alt2StrongJobs { get; set; } = new();

    // Per-job merit NOTES (catalog order), parallel to the level lists. Free text
    // describing the merits a member has on a job (shown when the job is marked
    // strong/merited). Persisted via ProfileJobLevels.NormalizeMerits — main on
    // the membership (MeritJobs), alts on the AppUser.
    public List<string> MeritJobs { get; set; } = new();
    public List<string> Alt1MeritJobs { get; set; } = new();
    public List<string> Alt2MeritJobs { get; set; } = new();

    // Per-craft levels (CraftCatalog order, index 0 = Alchemy … 8 = Fishing).
    // Account-level: main on AppUser.CraftLevels, alts on Alt1/Alt2CraftLevels.
    public List<int> CraftLevels { get; set; } = new();
    public List<int> Alt1CraftLevels { get; set; } = new();
    public List<int> Alt2CraftLevels { get; set; } = new();

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

    // False when the super-admin global addon kill-switch is on. Hides the
    // "Set up the addon" onboarding step entirely when the addon is disabled.
    public bool AddonAvailable { get; set; } = true;

    // Context for the JS-driven gear/skill/relic ratings + rate-teammates panel,
    // which reuse the Discord Activity job-rating API (/api/activity/job-ratings).
    public int PrimaryLinkshellId { get; set; }
    public string MyAppUserId { get; set; } = string.Empty;

    // Teammates in the primary linkshell the member can rate (excludes self).
    public List<RatingTeammate> RatingTeammates { get; set; } = new();

    public sealed class RatingTeammate
    {
        public string AppUserId { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string? Alt1 { get; set; }
        public string? Alt2 { get; set; }
    }
}
