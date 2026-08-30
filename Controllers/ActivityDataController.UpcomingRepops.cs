using LinkshellManagerDiscordApp.Services;
using Microsoft.AspNetCore.Mvc;

namespace LinkshellManagerDiscordApp.Controllers;

// The pops the linkshell is still waiting on, for the create-event form's Start pre-fill: an
// officer staging an HNM camp is almost always staging it for the repop already predicted by a
// logged ToD, so the form fills Start in with that instant instead of making them retype it.
//
// Its own endpoint rather than a field on the overview: the overview is polled, and this answer is
// only wanted while a create/edit form is open. See UpcomingRepopLookup for the lookup itself,
// which the web event form shares.
public sealed partial class ActivityDataController
{
    [HttpGet("linkshells/{linkshellId:int}/upcoming-repops")]
    public async Task<IActionResult> GetUpcomingRepopsAsync(int linkshellId, CancellationToken cancellationToken)
    {
        var appUser = await ResolveAppUserAsync(cancellationToken);
        if (appUser is null)
        {
            return Unauthorized(new { error = "Sign in to read this linkshell's upcoming repops." });
        }

        var membership = await GetMembershipAsync(appUser.Id, linkshellId, cancellationToken);
        if (!await CanAsync(membership, r => r.CanManageEvents, cancellationToken))
        {
            return Forbid();
        }

        var entries = await UpcomingRepopLookup.ForLinkshellAsync(
            _dbContext, linkshellId, DateTime.UtcNow, cancellationToken);

        return Ok(new
        {
            entries = entries.Select(entry => new
            {
                todId = entry.TodId,
                monsterName = entry.MonsterName,
                matchNames = entry.MatchNames,
                repopTime = entry.RepopTimeUtc,
                dayNumber = entry.DayNumber
            }).ToList()
        });
    }
}
