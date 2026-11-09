using LinkshellManagerDiscordApp.Services;
using Microsoft.AspNetCore.Mvc;

namespace LinkshellManagerDiscordApp.Controllers;

// Each monster's newest ToD, for the "Last ToD" readout beside Starts on the create/edit event
// form. Read only while that form is open, like upcoming-repops. See LatestTodLookup.
public sealed partial class ActivityDataController
{
    [HttpGet("linkshells/{linkshellId:int}/latest-tods")]
    public async Task<IActionResult> GetLatestTodsAsync(int linkshellId, CancellationToken cancellationToken)
    {
        var appUser = await ResolveAppUserAsync(cancellationToken);
        if (appUser is null)
        {
            return Unauthorized(new { error = "Sign in to read this linkshell's ToDs." });
        }

        var membership = await GetMembershipAsync(appUser.Id, linkshellId, cancellationToken);
        if (!await CanAsync(membership, r => r.CanManageEvents, cancellationToken))
        {
            return Forbid();
        }

        var entries = await LatestTodLookup.ForLinkshellAsync(_dbContext, linkshellId, cancellationToken);

        return Ok(new
        {
            entries = entries.Select(entry => new
            {
                todId = entry.TodId,
                monsterName = entry.MonsterName,
                matchNames = entry.MatchNames,
                time = entry.TimeUtc,
                hq = entry.Hq,
                dayNumber = entry.DayNumber,
                nextDay = entry.NextDay
            }).ToList()
        });
    }
}
