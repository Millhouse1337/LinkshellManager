using LinkshellManagerDiscordApp.Utils;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace LinkshellManagerDiscordApp.Controllers;

public sealed partial class ActivityDataController
{
    // Read-only roster of every member's leveled jobs (the levels they entered on
    // their Profile), for the linkshell's main + alt characters. Any member of the
    // linkshell can view it (GetMembershipAsync also enforces the guild lock).
    [HttpGet("linkshells/{linkshellId:int}/jobs-roster")]
    public async Task<IActionResult> GetJobsRosterAsync(int linkshellId, CancellationToken cancellationToken)
    {
        var appUser = await ResolveAppUserAsync(cancellationToken);
        if (appUser is null)
        {
            return Unauthorized(new
            {
                error = "Sign in with ASP.NET Identity or provide a Discord bearer token to load the jobs roster."
            });
        }

        var membership = await GetMembershipAsync(appUser.Id, linkshellId, cancellationToken);
        if (membership is null)
        {
            return Forbid();
        }

        // Only app-linked members carry profile job data; sheet-only placeholders
        // (no AppUserId) have nothing to show.
        var members = await _dbContext.AppUserLinkshells
            .Include(link => link.AppUser)
            .Where(link => link.LinkshellId == linkshellId && link.AppUserId != null)
            .OrderBy(link => link.CharacterName)
            .AsNoTracking()
            .ToListAsync(cancellationToken);

        // Relic flags come from each member's OWN job ratings (self rows, HasRelic),
        // keyed by (AppUserId, CharacterSlot). JobRating.JobIndex is already
        // catalog-aligned, so it maps straight onto the pills.
        var relicRows = await _dbContext.JobRatings
            .AsNoTracking()
            .Where(r => r.LinkshellId == linkshellId
                && r.RaterAppUserId == r.TargetAppUserId
                && r.HasRelic
                && r.JobIndex >= 0)
            .Select(r => new { r.TargetAppUserId, r.CharacterSlot, r.JobIndex, r.RelicNames })
            .ToListAsync(cancellationToken);
        var relicLookup = relicRows
            .GroupBy(r => (r.TargetAppUserId, r.CharacterSlot))
            .ToDictionary(g => g.Key, g => g.Select(x => x.JobIndex).ToHashSet());
        // Display string per job = the owned relics joined ("Bravura, Ragnarok").
        var relicNameLookup = relicRows
            .GroupBy(r => (r.TargetAppUserId, r.CharacterSlot))
            .ToDictionary(
                g => g.Key,
                g => g.GroupBy(x => x.JobIndex).ToDictionary(
                    j => j.Key,
                    j => string.Join(", ", j.First().RelicNames ?? Array.Empty<string>())));

        var jobCount = EventJobCatalog.MainJobOptions.Length;
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
                foreach (var kv in map) { if (kv.Key >= 0 && kv.Key < jobCount) { names[kv.Key] = kv.Value; } }
            }
            return names;
        }

        var roster = members
            .Select(link => new ActivityJobsRosterMemberDto(
                link.Id,
                link.CharacterName ?? link.AppUser?.CharacterName ?? link.AppUser?.UserName ?? "Unknown member",
                link.Rank,
                ProfileJobLevels.ToCatalogLevels(link.JobLevels),
                string.IsNullOrWhiteSpace(link.AppUser?.AltCharacterName1) ? null : link.AppUser!.AltCharacterName1,
                ProfileJobLevels.ToCatalogLevels(link.AppUser?.Alt1JobLevels),
                string.IsNullOrWhiteSpace(link.AppUser?.AltCharacterName2) ? null : link.AppUser!.AltCharacterName2,
                ProfileJobLevels.ToCatalogLevels(link.AppUser?.Alt2JobLevels),
                ProfileJobLevels.ToCatalogFlags(link.StrongJobs),
                ProfileJobLevels.ToCatalogFlags(link.AppUser?.Alt1StrongJobs),
                ProfileJobLevels.ToCatalogFlags(link.AppUser?.Alt2StrongJobs),
                RelicFlags(link.AppUserId, 0),
                RelicFlags(link.AppUserId, 1),
                RelicFlags(link.AppUserId, 2),
                ProfileJobLevels.NormalizeMerits(link.MeritJobs),
                ProfileJobLevels.NormalizeMerits(link.AppUser?.Alt1MeritJobs),
                ProfileJobLevels.NormalizeMerits(link.AppUser?.Alt2MeritJobs),
                RelicNames(link.AppUserId, 0),
                RelicNames(link.AppUserId, 1),
                RelicNames(link.AppUserId, 2)))
            .ToList();

        return Ok(new ActivityJobsRosterDto(EventJobCatalog.MainJobOptions, roster));
    }
}
