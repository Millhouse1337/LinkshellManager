using LinkshellManagerDiscordApp.Services;
using Microsoft.AspNetCore.Mvc;

namespace LinkshellManagerDiscordApp.Controllers;

// Per-member key item progress.
//
// BOTH self-serve and officer override: a member ticks their own cell, an officer ticks anybody's,
// and the two write an identical row distinguishable only by its audit columns. The rule itself is
// ChartKeyItemService.CanSetKeyItemFor, shared with the website so neither surface can be the more
// permissive one.
//
// No GET here either - the grid rides in the existing GET .../charts/{board} payload alongside the
// per-card counts derived from the same rows.
public sealed partial class ActivityDataController
{
    [HttpPost("linkshells/{linkshellId:int}/charts/{board}/keyitems")]
    public async Task<IActionResult> SetChartKeyItemAsync(
        int linkshellId,
        string board,
        [FromBody] ActivityChartKeyItemRequest request,
        CancellationToken cancellationToken)
    {
        var gate = await AuthorizeChartsMemberAsync(linkshellId, cancellationToken);
        if (gate.Failure is not null)
        {
            return gate.Failure;
        }

        if (!ChartKeyItemService.CanSetKeyItemFor(request.MembershipId, gate.MembershipId, gate.CanManage))
        {
            return Forbid();
        }

        // SetAsync draws the remaining two boundaries itself: the key item name against the CLOSED
        // catalog list, and the membership against THIS linkshell.
        var error = await _chartKeyItems.SetAsync(
            linkshellId, board, request.KeyItemName, request.MembershipId, request.Has,
            gate.Actor, cancellationToken);
        if (error is not null)
        {
            return BadRequest(new { error });
        }

        await _dbContext.SaveChangesAsync(cancellationToken);
        return Ok(new { success = true });
    }
}
