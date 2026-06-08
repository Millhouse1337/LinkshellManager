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
}

// Backs the per-member "View Profile" page (one member's leveled jobs, main +
// alts). Reuses JobsRosterEntry for the jobs payload and is laid out so future
// profile sections (e.g. crafts) can be added without reshaping this model.
public class MemberProfileViewModel
{
    public JobsRosterEntry Entry { get; set; } = new();

    public int LinkshellId { get; set; }

    public string? LinkshellName { get; set; }

    public IReadOnlyList<string> JobCatalog { get; } = EventJobCatalog.MainJobOptions;
}
