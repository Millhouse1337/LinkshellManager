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

        var roster = members
            .Select(link => new ActivityJobsRosterMemberDto(
                link.Id,
                link.CharacterName ?? link.AppUser?.CharacterName ?? link.AppUser?.UserName ?? "Unknown member",
                link.Rank,
                ProfileJobLevels.ToCatalogLevels(link.JobLevels),
                string.IsNullOrWhiteSpace(link.AppUser?.AltCharacterName1) ? null : link.AppUser!.AltCharacterName1,
                ProfileJobLevels.ToCatalogLevels(link.AppUser?.Alt1JobLevels),
                string.IsNullOrWhiteSpace(link.AppUser?.AltCharacterName2) ? null : link.AppUser!.AltCharacterName2,
                ProfileJobLevels.ToCatalogLevels(link.AppUser?.Alt2JobLevels)))
            .ToList();

        return Ok(new ActivityJobsRosterDto(EventJobCatalog.MainJobOptions, roster));
    }
}
