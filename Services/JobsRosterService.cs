using LinkshellManagerDiscordApp.Data;
using LinkshellManagerDiscordApp.Models;
using LinkshellManagerDiscordApp.Utils;
using LinkshellManagerDiscordApp.ViewModels;
using Microsoft.EntityFrameworkCore;

namespace LinkshellManagerDiscordApp.Services;

// Builds a linkshell's leveled-jobs entries (main + alts, with the "strong"
// merit flags, relic flags/names and merit notes) from the levels members
// entered on their Profile. Shared by the Jobs Roster page and the Dashboard
// roster's "Show Jobs" toggle so both render identical pills from one build.
public sealed class JobsRosterService
{
    private readonly ApplicationDbContext _context;

    public JobsRosterService(ApplicationDbContext context)
    {
        _context = context;
    }

    // Loads the linkshell's app-linked members and builds their entries.
    public async Task<List<JobsRosterEntry>> BuildAsync(int linkshellId, CancellationToken cancellationToken = default)
    {
        // Only app-linked members carry profile job data; sheet-only placeholders
        // (no AppUserId) have nothing to show, so leave them out.
        var members = await _context.AppUserLinkshells
            .Include(link => link.AppUser)
            .Where(link => link.LinkshellId == linkshellId && link.AppUserId != null)
            .OrderBy(link => link.CharacterName)
            .AsNoTracking()
            .ToListAsync(cancellationToken);

        return await BuildForMembersAsync(linkshellId, members, cancellationToken);
    }

    // Same build over an already-loaded member list (callers that render the
    // roster anyway shouldn't pay for a second members query). Members without
    // an AppUserId are skipped — they have no profile to read jobs from.
    public async Task<List<JobsRosterEntry>> BuildForMembersAsync(
        int linkshellId,
        IReadOnlyCollection<AppUserLinkshell> members,
        CancellationToken cancellationToken = default)
    {
        var linked = members.Where(link => link.AppUserId != null).ToList();
        if (linked.Count == 0)
        {
            return new List<JobsRosterEntry>();
        }

        // Relic flags/names from every member's OWN job ratings (self rows with
        // HasRelic), keyed by (AppUserId, CharacterSlot) — mirrors the Activity.
        var jobCount = EventJobCatalog.MainJobOptions.Length;
        var relicRows = await _context.JobRatings.AsNoTracking()
            .Where(rating => rating.LinkshellId == linkshellId
                && rating.RaterAppUserId == rating.TargetAppUserId
                && rating.HasRelic
                && rating.JobIndex >= 0)
            .Select(rating => new { rating.TargetAppUserId, rating.CharacterSlot, rating.JobIndex, rating.RelicNames })
            .ToListAsync(cancellationToken);
        var relicLookup = relicRows
            .GroupBy(rating => (rating.TargetAppUserId, rating.CharacterSlot))
            .ToDictionary(group => group.Key, group => group.Select(rating => rating.JobIndex).ToHashSet());
        var relicNameLookup = relicRows
            .GroupBy(rating => (rating.TargetAppUserId, rating.CharacterSlot))
            .ToDictionary(
                group => group.Key,
                group => group.GroupBy(rating => rating.JobIndex).ToDictionary(
                    job => job.Key,
                    job => string.Join(", ", job.First().RelicNames ?? Array.Empty<string>())));

        bool[] RelicFlags(string? appUserId, int slot)
        {
            var flags = new bool[jobCount];
            if (appUserId != null && relicLookup.TryGetValue((appUserId, slot), out var set))
            {
                for (var i = 0; i < jobCount; i++) { flags[i] = set.Contains(i); }
            }
            return flags;
        }

        string[] RelicNames(string? appUserId, int slot)
        {
            var names = new string[jobCount];
            for (var i = 0; i < jobCount; i++) { names[i] = string.Empty; }
            if (appUserId != null && relicNameLookup.TryGetValue((appUserId, slot), out var map))
            {
                foreach (var pair in map) { if (pair.Key >= 0 && pair.Key < jobCount) { names[pair.Key] = pair.Value; } }
            }
            return names;
        }

        return linked.Select(link => new JobsRosterEntry
        {
            MemberId = link.Id,
            CharacterName = link.CharacterName ?? link.AppUser?.CharacterName ?? link.AppUser?.UserName ?? "Unknown",
            Rank = link.Rank,
            JobLevels = ProfileJobLevels.ToCatalogLevels(link.JobLevels),
            Alt1Name = string.IsNullOrWhiteSpace(link.AppUser?.AltCharacterName1) ? null : link.AppUser!.AltCharacterName1,
            Alt1JobLevels = ProfileJobLevels.ToCatalogLevels(link.AppUser?.Alt1JobLevels),
            Alt2Name = string.IsNullOrWhiteSpace(link.AppUser?.AltCharacterName2) ? null : link.AppUser!.AltCharacterName2,
            Alt2JobLevels = ProfileJobLevels.ToCatalogLevels(link.AppUser?.Alt2JobLevels),
            StrongJobs = ProfileJobLevels.ToCatalogFlags(link.StrongJobs),
            Alt1StrongJobs = ProfileJobLevels.ToCatalogFlags(link.AppUser?.Alt1StrongJobs),
            Alt2StrongJobs = ProfileJobLevels.ToCatalogFlags(link.AppUser?.Alt2StrongJobs),
            RelicFlags = RelicFlags(link.AppUserId, 0),
            Alt1RelicFlags = RelicFlags(link.AppUserId, 1),
            Alt2RelicFlags = RelicFlags(link.AppUserId, 2),
            RelicNames = RelicNames(link.AppUserId, 0),
            Alt1RelicNames = RelicNames(link.AppUserId, 1),
            Alt2RelicNames = RelicNames(link.AppUserId, 2),
            MeritJobs = ProfileJobLevels.NormalizeMerits(link.MeritJobs),
            Alt1MeritJobs = ProfileJobLevels.NormalizeMerits(link.AppUser?.Alt1MeritJobs),
            Alt2MeritJobs = ProfileJobLevels.NormalizeMerits(link.AppUser?.Alt2MeritJobs)
        }).ToList();
    }

    // AppUserIds among the given members that have actually opened/synced the
    // Discord Activity (a DiscordActivityUser row points at them). Drives the
    // roster's "App" tag — an AppUserId alone only means an account exists.
    public async Task<HashSet<string>> GetSyncedAppUserIdsAsync(
        IReadOnlyCollection<AppUserLinkshell> members,
        CancellationToken cancellationToken = default)
    {
        var memberAppUserIds = members
            .Select(link => link.AppUserId)
            .Where(id => id != null)
            .Select(id => id!)
            .Distinct()
            .ToList();

        if (memberAppUserIds.Count == 0)
        {
            return new HashSet<string>(StringComparer.Ordinal);
        }

        return (await _context.DiscordActivityUsers
            .AsNoTracking()
            .Where(user => user.IdentityUserId != null && memberAppUserIds.Contains(user.IdentityUserId))
            .Select(user => user.IdentityUserId!)
            .ToListAsync(cancellationToken))
            .ToHashSet(StringComparer.Ordinal);
    }
}
