using LinkshellManagerDiscordApp.Models;
using LinkshellManagerDiscordApp.Utils;

namespace LinkshellManagerDiscordApp.ViewModels;

// Backs the "Jobs Roster" page — every member's leveled jobs (the levels they
// entered on their Profile), for the linkshell's main + alt characters.
public class JobsRosterViewModel
{
    public List<Linkshell> Linkshells { get; set; } = new();

    public int? SelectedLinkshellId { get; set; }

    public List<JobsRosterEntry> Entries { get; set; } = new();

    // Catalog job names in display order (WAR..SMN); each entry's level arrays
    // are aligned to this same order via ProfileJobLevels.ToCatalogLevels.
    public IReadOnlyList<string> JobCatalog { get; } = EventJobCatalog.MainJobOptions;
}

public class JobsRosterEntry
{
    public string CharacterName { get; set; } = string.Empty;

    public string? Rank { get; set; }

    // Catalog-aligned levels (index i = level of JobCatalog[i]); 0 = not leveled.
    public IReadOnlyList<int> JobLevels { get; set; } = Array.Empty<int>();

    public string? Alt1Name { get; set; }

    public IReadOnlyList<int> Alt1JobLevels { get; set; } = Array.Empty<int>();

    public string? Alt2Name { get; set; }

    public IReadOnlyList<int> Alt2JobLevels { get; set; } = Array.Empty<int>();

    // Catalog-aligned "strong" flags parallel to each character's level array
    // (true = well-geared/merited). Rendered as a marker on the job pills.
    public IReadOnlyList<bool> StrongJobs { get; set; } = Array.Empty<bool>();

    public IReadOnlyList<bool> Alt1StrongJobs { get; set; } = Array.Empty<bool>();

    public IReadOnlyList<bool> Alt2StrongJobs { get; set; } = Array.Empty<bool>();

    // Catalog-aligned relic flags (true = the member marked this job as having
    // its relic) — drives the glowing flame border on the job pill.
    public IReadOnlyList<bool> RelicFlags { get; set; } = Array.Empty<bool>();
    public IReadOnlyList<bool> Alt1RelicFlags { get; set; } = Array.Empty<bool>();
    public IReadOnlyList<bool> Alt2RelicFlags { get; set; } = Array.Empty<bool>();

    // Catalog-aligned relic weapon names per job (joined, e.g. "Bravura, Ragnarok"),
    // shown as the pill's hover tooltip. Empty when none.
    public IReadOnlyList<string> RelicNames { get; set; } = Array.Empty<string>();
    public IReadOnlyList<string> Alt1RelicNames { get; set; } = Array.Empty<string>();
    public IReadOnlyList<string> Alt2RelicNames { get; set; } = Array.Empty<string>();

    // Catalog-aligned merit notes per job (free text), also shown in the tooltip.
    public IReadOnlyList<string> MeritJobs { get; set; } = Array.Empty<string>();
    public IReadOnlyList<string> Alt1MeritJobs { get; set; } = Array.Empty<string>();
    public IReadOnlyList<string> Alt2MeritJobs { get; set; } = Array.Empty<string>();

    // Catalog-aligned SELF gear/skill ratings (1-5; 0 = unrated) the member gave
    // their OWN character — surfaced read-only on the View Profile page so the
    // linkshell can see a member's own assessment of each job. Only populated
    // there; the Jobs Roster leaves them empty.
    public IReadOnlyList<int> GearRatings { get; set; } = Array.Empty<int>();
    public IReadOnlyList<int> Alt1GearRatings { get; set; } = Array.Empty<int>();
    public IReadOnlyList<int> Alt2GearRatings { get; set; } = Array.Empty<int>();
    public IReadOnlyList<int> SkillRatings { get; set; } = Array.Empty<int>();
    public IReadOnlyList<int> Alt1SkillRatings { get; set; } = Array.Empty<int>();
    public IReadOnlyList<int> Alt2SkillRatings { get; set; } = Array.Empty<int>();
}

// Backs the per-member "View Profile" page (one member's leveled jobs, main +
// alts). Reuses JobsRosterEntry for the jobs payload and is laid out so future
// profile sections (e.g. crafts) can be added without reshaping this model.
public class MemberProfileViewModel
{
    public JobsRosterEntry Entry { get; set; } = new();

    public int LinkshellId { get; set; }

    public string? LinkshellName { get; set; }

    // The viewed member's AppUser id (null for an unsynced/placeholder member). When
    // set, the page fetches peer job-ratings ("what the linkshell thinks") for it via
    // the Activity job-ratings API; null = no peer section.
    public string? TargetAppUserId { get; set; }

    public IReadOnlyList<string> JobCatalog { get; } = EventJobCatalog.MainJobOptions;
}
