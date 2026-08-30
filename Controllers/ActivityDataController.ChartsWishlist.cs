using LinkshellManagerDiscordApp.Models;
using LinkshellManagerDiscordApp.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace LinkshellManagerDiscordApp.Controllers;

// The Charts wishlist API.
//
// The one part of Charts a member WITHOUT CanManageCharts may write, which makes the gates here
// different from every other Charts endpoint and worth reading carefully:
//
//   submit    membership only
//   withdraw  ChartWishlistService.CanEditRequest - your own, or anybody's if you can manage
//   fulfil    CanManageCharts
//   reorder   CanManageCharts
//
// Both ownership decisions come from the SERVICE rather than from an inline comparison, so the
// website cannot answer them differently - see ChartWishlistService.CanEditRequest.
//
// There is no GET. A board's requests ride in the existing GET .../charts/{board} payload, for the
// reason the ledger already does: a card's badge and the list below it are two views of one set of
// rows, and fetching them apart is what lets a badge disagree with the list under it.
public sealed partial class ActivityDataController
{
    [HttpPost("linkshells/{linkshellId:int}/charts/{board}/wishlist")]
    public async Task<IActionResult> AddChartWishlistRequestAsync(
        int linkshellId,
        string board,
        [FromBody] ActivityChartWishlistRequestInput request,
        CancellationToken cancellationToken)
    {
        var gate = await AuthorizeChartsMemberAsync(linkshellId, cancellationToken);
        if (gate.Failure is not null)
        {
            return gate.Failure;
        }

        // The ROUTE's board, never a body field: a request cannot file itself under a board the URL
        // did not name. Same rule AddChartPopItemAsync follows.
        var draft = ChartWishlistService.NormalizeDraft(
            board, request.Boss, request.ItemName, request.Quantity, request.Notes);
        if (draft is null)
        {
            return BadRequest(new { error = "Give the item a name, and pick a zone on this board or leave it as anywhere." });
        }

        var now = DateTime.UtcNow;
        _dbContext.ChartWishlistRequests.Add(new ChartWishlistRequest
        {
            LinkshellId = linkshellId,
            Board = draft.Board,
            Boss = draft.Boss,
            ItemName = draft.ItemName,
            Quantity = draft.Quantity,
            Notes = draft.Notes,
            Status = ChartWishlistStatuses.Pending,
            Priority = await _chartWishlist.NextPriorityAsync(linkshellId, draft.Board, cancellationToken),
            // Requested FOR the caller, always. There is deliberately no "on behalf of" field: an
            // officer entering somebody else's want would own it, and the ownership rule keys on
            // this column.
            RequestedByAppUserId = gate.Actor.AppUserId,
            RequestedByMembershipId = gate.MembershipId,
            RequestedByCharacterName = gate.Actor.CharacterName ?? string.Empty,
            RequestedAt = now,
            UpdatedAt = now,
        });

        await _dbContext.SaveChangesAsync(cancellationToken);
        return Ok(new { success = true });
    }

    // Withdraw DELETES. An officer removing somebody else's request is the same operation, which is
    // why one endpoint covers both - see ChartWishlistStatuses for why there is no third status.
    [HttpPost("charts/wishlist/{requestId:int}/withdraw")]
    public async Task<IActionResult> WithdrawChartWishlistRequestAsync(
        int requestId, CancellationToken cancellationToken)
    {
        var found = await LoadChartWishlistForWriteAsync(requestId, cancellationToken);
        if (found.Failure is not null)
        {
            return found.Failure;
        }

        if (!ChartWishlistService.CanEditRequest(found.Row!, found.Actor.AppUserId, found.CanManage))
        {
            return Forbid();
        }

        _dbContext.ChartWishlistRequests.Remove(found.Row!);
        await _dbContext.SaveChangesAsync(cancellationToken);
        return Ok(new { success = true });
    }

    [HttpPost("charts/wishlist/{requestId:int}/status")]
    public async Task<IActionResult> SetChartWishlistStatusAsync(
        int requestId,
        [FromBody] ActivityChartWishlistStatusRequest request,
        CancellationToken cancellationToken)
    {
        var found = await LoadChartWishlistForWriteAsync(requestId, cancellationToken);
        if (found.Failure is not null)
        {
            return found.Failure;
        }

        // Officers only, unlike withdrawing. Marking your own request fulfilled is a claim about
        // what the linkshell did, not about what you want.
        if (!found.CanManage)
        {
            return Forbid();
        }

        var status = ChartWishlistService.NormalizeStatus(request.Status);
        if (status is null)
        {
            return BadRequest(new { error = "That is not a request status." });
        }

        var row = found.Row!;
        row.Status = status;
        row.UpdatedAt = DateTime.UtcNow;

        // The stamp follows the status in both directions, so a request put back to pending does not
        // keep claiming somebody settled it.
        var fulfilled = status == ChartWishlistStatuses.Fulfilled;
        row.FulfilledAt = fulfilled ? DateTime.UtcNow : null;
        row.FulfilledByAppUserId = fulfilled ? found.Actor.AppUserId : null;
        row.FulfilledByCharacterName = fulfilled ? found.Actor.CharacterName : null;

        await _dbContext.SaveChangesAsync(cancellationToken);
        return Ok(new { success = true });
    }

    // Set-wise: the request carries the COMPLETE ordered id list for the board, and an id from
    // elsewhere refuses the whole thing rather than reordering the rest.
    [HttpPost("linkshells/{linkshellId:int}/charts/{board}/wishlist/order")]
    public async Task<IActionResult> ReorderChartWishlistAsync(
        int linkshellId,
        string board,
        [FromBody] ActivityChartWishlistOrderRequest request,
        CancellationToken cancellationToken)
    {
        var gate = await AuthorizeChartsWriteAsync(linkshellId, cancellationToken);
        if (gate.Failure is not null)
        {
            return gate.Failure;
        }

        var catalog = ChartBoardCatalog.Find(board);
        if (catalog is null || !catalog.AllowsWishlist)
        {
            return NotFound(new { error = "That board takes no item requests." });
        }

        var error = await _chartWishlist.ReorderAsync(
            linkshellId, catalog.Key, request.OrderedIds ?? Array.Empty<int>(), cancellationToken);
        if (error is not null)
        {
            return BadRequest(new { error });
        }

        await _dbContext.SaveChangesAsync(cancellationToken);
        return Ok(new { success = true });
    }

    /// <summary>
    /// Loads a request for writing and re-runs the MEMBER gate for the row's OWN linkshell.
    ///
    /// Deliberately re-runs the full gate rather than comparing linkshell ids here: the per-linkshell
    /// Discord guild lock lives inside GetMembershipAsync, and a hand-written id comparison looks
    /// equivalent while silently bypassing it. Same call LoadChartPopItemForWriteAsync makes.
    ///
    /// Returns CanManage rather than deciding anything: which of the two rules applies is the
    /// caller's business, and both of them live in ChartWishlistService.
    /// </summary>
    private async Task<(IActionResult? Failure, ChartWishlistRequest? Row, ChartBoardActor Actor, bool CanManage)>
        LoadChartWishlistForWriteAsync(int requestId, CancellationToken cancellationToken)
    {
        var row = await _dbContext.ChartWishlistRequests
            .FirstOrDefaultAsync(item => item.Id == requestId, cancellationToken);
        if (row is null)
        {
            return (NotFound(new { error = "That item request was not found." }), null, default, false);
        }

        var gate = await AuthorizeChartsMemberAsync(row.LinkshellId, cancellationToken);
        return gate.Failure is not null
            ? (gate.Failure, null, default, false)
            : (null, row, gate.Actor, gate.CanManage);
    }
}
