using Microsoft.AspNetCore.Mvc;

namespace LinkshellManagerDiscordApp.Controllers;

// Every chart item the linkshell is holding, across all boards, for Treasury -> Items.
//
// Read-only, and read straight off the Charts rows rather than copied into the Items table: a
// chart holding is edited, credited and removed on its board, and a second copy in Items would be
// one more thing to keep in step. Treasury lists them so the linkshell's stash reads as one list.
//
// Same read rule as the boards themselves (GetChartBoardAsync): any member.
public sealed partial class ActivityChartsController
{
    [HttpGet("linkshells/{linkshellId:int}/chart-holdings")]
    public async Task<IActionResult> GetChartHoldingsAsync(int linkshellId, CancellationToken cancellationToken)
    {
        var appUser = await ResolveAppUserAsync(cancellationToken);
        if (appUser is null)
        {
            return Unauthorized(new { error = "Sign in to see the linkshell's chart holdings." });
        }

        var membership = await GetMembershipAsync(appUser.Id, linkshellId, cancellationToken);
        if (membership is null)
        {
            return Forbid();
        }

        return Ok(await _chartBoards.LoadHoldingsAsync(linkshellId, cancellationToken));
    }
}
