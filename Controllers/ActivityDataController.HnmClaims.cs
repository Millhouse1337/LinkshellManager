using LinkshellManagerDiscordApp.Services;
using Microsoft.AspNetCore.Mvc;
using static LinkshellManagerDiscordApp.Controllers.ActivityMappers;

namespace LinkshellManagerDiscordApp.Controllers;

// The dashboard's HNM claim-rate chart over a range its 7d / 30d / All buttons don't cover: the
// "..." → last N days. The three fixed ranges ride the overview; this is the same aggregation
// (HnmClaimStatsService) for one more, fetched only when someone asks for it. Member-gated.
public sealed partial class ActivityDataController
{
    [HttpGet("hnm-claims")]
    public async Task<IActionResult> GetHnmClaimsAsync(
        [FromQuery] int linkshellId,
        [FromQuery] int days,
        [FromServices] HnmClaimStatsService hnmClaimStats,
        CancellationToken cancellationToken)
    {
        var appUser = await ResolveAppUserAsync(cancellationToken);
        if (appUser is null) return Unauthorized(new { error = "Sign in to view the claim rate." });
        if (linkshellId <= 0) return BadRequest(new { error = "A linkshell selection is required." });
        if (days < 1 || days > HnmClaimStatsService.MaxCustomDays)
        {
            return BadRequest(new { error = $"Pick a range of 1 to {HnmClaimStatsService.MaxCustomDays} days." });
        }

        var membership = await GetMembershipAsync(appUser.Id, linkshellId, cancellationToken);
        if (membership is null) return Forbid();

        Response.Headers.CacheControl = "no-store";
        var slices = await hnmClaimStats.BuildLastDaysAsync(linkshellId, days, cancellationToken);
        return Ok(MapHnmClaimSlices(slices));
    }
}
