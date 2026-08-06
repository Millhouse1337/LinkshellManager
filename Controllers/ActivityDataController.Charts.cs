using LinkshellManagerDiscordApp.Models;
using LinkshellManagerDiscordApp.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace LinkshellManagerDiscordApp.Controllers;

// The Charts API. One set of endpoints for every board — Sky, Sea, and later Dynamis / Limbus —
// with the board as a route segment rather than a copy of the stack per board.
//
// Reads are open to every member: a farming board only officers can see cannot settle the argument
// it exists to settle. Writes need CanManageCharts, and that permission is the ONLY gate on BOTH
// surfaces — ChartsController checks the same flag rather than the coarse Leader/Officer rank,
// because a coarse rank on one front-end and a named permission on the other is a privilege
// escalation available by picking a front-end. That is the bug GrantTreasuryToOfficersWhoUsedIt
// exists to document.
//
// Every derived number — per-boss totals, the ledger's cell states, the "49 / 63  78%" row total —
// comes from ChartBoardService, which the website also calls. Nothing on a board is stored twice.
public sealed partial class ActivityDataController
{
    [HttpGet("linkshells/{linkshellId:int}/charts/{board}")]
    public async Task<IActionResult> GetChartBoardAsync(
        int linkshellId, string board, CancellationToken cancellationToken)
    {
        var catalog = ChartBoardCatalog.Find(board);
        if (catalog is null)
        {
            return NotFound(new { error = "That chart board does not exist." });
        }

        var appUser = await ResolveAppUserAsync(cancellationToken);
        if (appUser is null)
        {
            return Unauthorized(new { error = $"Sign in to see the {catalog.Label} chart." });
        }

        var membership = await GetMembershipAsync(appUser.Id, linkshellId, cancellationToken);
        if (membership is null)
        {
            return Forbid();
        }

        var canManage = await CanAsync(membership, role => role.CanManageCharts, cancellationToken);
        return Ok(await BuildChartBoardAsync(linkshellId, catalog, canManage, cancellationToken));
    }

    /// <summary>The boards the sub-nav offers, so the client does not keep a second copy of the list.</summary>
    [HttpGet("charts/boards")]
    public IActionResult GetChartBoards() =>
        Ok(ChartBoardCatalog.Boards
            .Select(board => new ActivityChartBoardSummaryDto(board.Key, board.Label))
            .ToList());

    [HttpPost("linkshells/{linkshellId:int}/charts/{board}/items")]
    public async Task<IActionResult> AddChartPopItemAsync(
        int linkshellId,
        string board,
        [FromBody] ActivityChartPopItemRequest request,
        CancellationToken cancellationToken)
    {
        var gate = await AuthorizeChartsWriteAsync(linkshellId, cancellationToken);
        if (gate.Failure is not null)
        {
            return gate.Failure;
        }

        // The route's board wins over the body's, so a request cannot file a row under a board the
        // URL did not name.
        var draft = ChartBoardService.NormalizeDraft(
            board, request.Boss, request.ItemName, request.HeldByCharacterName,
            request.HeldByMembershipId, request.Quantity, request.Notes);
        if (draft is null)
        {
            return BadRequest(new { error = "Pick a boss on this board and give the item a name." });
        }

        var holder = await ResolveChartHolderAsync(linkshellId, draft, cancellationToken);
        if (holder.Failure is not null)
        {
            return holder.Failure;
        }

        // Resolved BEFORE the row is built: naming a farmer from another linkshell fails the whole
        // request rather than leaving a pop item behind with no credit on it.
        var credits = await ResolveChartCreditsAsync(linkshellId, request.Credits, cancellationToken);
        if (credits.Failure is not null)
        {
            return credits.Failure;
        }

        var now = DateTime.UtcNow;
        var item = new ChartPopItem
        {
            LinkshellId = linkshellId,
            Board = draft.Board,
            Boss = draft.Boss,
            ItemName = draft.ItemName,
            HeldByCharacterName = holder.CharacterName,
            HeldByMembershipId = holder.MembershipId,
            Quantity = draft.Quantity,
            Notes = draft.Notes,
            SortOrder = await _chartBoards.NextSortOrderAsync(
                linkshellId, draft.Board, draft.Boss, cancellationToken),
            CreatedByAppUserId = gate.Actor.AppUserId,
            CreatedByCharacterName = gate.Actor.CharacterName,
            CreatedAt = now,
            UpdatedAt = now,
        };

        // One SaveChanges for the row AND its farmers, so an add that names them cannot half-succeed.
        ChartBoardService.AttachCredits(item, credits.Resolved, gate.Actor);

        _dbContext.ChartPopItems.Add(item);
        await _dbContext.SaveChangesAsync(cancellationToken);
        return Ok(new { success = true, id = item.Id });
    }

    [HttpPost("charts/items/{itemId:int}/update")]
    public async Task<IActionResult> UpdateChartPopItemAsync(
        int itemId,
        [FromBody] ActivityChartPopItemRequest request,
        CancellationToken cancellationToken)
    {
        var found = await LoadChartPopItemForWriteAsync(itemId, cancellationToken);
        if (found.Failure is not null)
        {
            return found.Failure;
        }

        var item = found.Item!;
        // The row's own board is authoritative: an item never moves between boards, only between
        // bosses on the one it already belongs to.
        var draft = ChartBoardService.NormalizeDraft(
            item.Board, request.Boss, request.ItemName, request.HeldByCharacterName,
            request.HeldByMembershipId, request.Quantity, request.Notes);
        if (draft is null)
        {
            return BadRequest(new { error = "Pick a boss on this board and give the item a name." });
        }

        var holder = await ResolveChartHolderAsync(item.LinkshellId, draft, cancellationToken);
        if (holder.Failure is not null)
        {
            return holder.Failure;
        }

        // Moving a row to another boss sends it to the bottom of the new card rather than keeping a
        // position that meant something on the old one.
        if (!string.Equals(item.Boss, draft.Boss, StringComparison.OrdinalIgnoreCase))
        {
            item.SortOrder = await _chartBoards.NextSortOrderAsync(
                item.LinkshellId, item.Board, draft.Boss, cancellationToken);
        }

        item.Boss = draft.Boss;
        item.ItemName = draft.ItemName;
        item.HeldByCharacterName = holder.CharacterName;
        item.HeldByMembershipId = holder.MembershipId;
        item.Quantity = draft.Quantity;
        item.Notes = draft.Notes;
        item.UpdatedAt = DateTime.UtcNow;

        // Only when the request carried a list. A caller that says nothing about credits — the
        // website's edit form does not — must not have the row's farmers wiped as a side effect of
        // changing a quantity.
        if (request.Credits is not null)
        {
            var credits = await ResolveChartCreditsAsync(
                item.LinkshellId, request.Credits, cancellationToken);
            if (credits.Failure is not null)
            {
                return credits.Failure;
            }

            await _chartBoards.ReplaceCreditsAsync(item, credits.Resolved, found.Actor, cancellationToken);
        }

        await _dbContext.SaveChangesAsync(cancellationToken);
        return Ok(new { success = true });
    }

    // Deleting a row takes its credits with it (cascade). That is right: credit is credit for THAT
    // item, and a row nobody tracks any more has no farming to attribute.
    [HttpPost("charts/items/{itemId:int}/delete")]
    public async Task<IActionResult> DeleteChartPopItemAsync(int itemId, CancellationToken cancellationToken)
    {
        var found = await LoadChartPopItemForWriteAsync(itemId, cancellationToken);
        if (found.Failure is not null)
        {
            return found.Failure;
        }

        _dbContext.ChartPopItems.Remove(found.Item!);
        await _dbContext.SaveChangesAsync(cancellationToken);
        return Ok(new { success = true });
    }

    // Set-based: the request carries the COMPLETE farmer list for the row, so a name left out is a
    // removal. Duplicates cannot survive, which is why the table has no unique index.
    [HttpPost("charts/items/{itemId:int}/credits")]
    public async Task<IActionResult> SetChartPopItemCreditsAsync(
        int itemId,
        [FromBody] ActivitySetChartCreditsRequest request,
        CancellationToken cancellationToken)
    {
        var found = await LoadChartPopItemForWriteAsync(itemId, cancellationToken);
        if (found.Failure is not null)
        {
            return found.Failure;
        }

        var item = found.Item!;
        var credits = await ResolveChartCreditsAsync(item.LinkshellId, request.Credits, cancellationToken);
        if (credits.Failure is not null)
        {
            return credits.Failure;
        }

        await _chartBoards.ReplaceCreditsAsync(item, credits.Resolved, found.Actor, cancellationToken);
        await _dbContext.SaveChangesAsync(cancellationToken);
        return Ok(new { success = true });
    }

    // ---- helpers ----------------------------------------------------------------

    /// <summary>
    /// Builds a whole board's payload. Shared by the read endpoint and mirrored by the website's
    /// controller, so the two surfaces cannot present a different board.
    /// </summary>
    private async Task<ActivityChartBoardDto> BuildChartBoardAsync(
        int linkshellId, ChartBoard board, bool canManage, CancellationToken cancellationToken)
    {
        var items = await _chartBoards.LoadItemsAsync(linkshellId, board.Key, cancellationToken);
        var roster = await _chartBoards.LoadRosterAsync(linkshellId, cancellationToken);
        var ledger = ChartBoardService.BuildLedger(board, items, roster);

        var bosses = board.Bosses
            .Select(boss =>
            {
                var bossItems = items
                    .Where(item => string.Equals(item.Boss, boss.Name, StringComparison.OrdinalIgnoreCase))
                    .ToList();
                // Resolved to the target CARD, so the badge's spelling and its hue both come off the
                // thing it points at rather than off a literal this payload carries twice.
                var leadsTo = board.LeadsToFor(boss);
                return new ActivityChartBossDto(
                    boss.Name,
                    boss.ThemeKey,
                    boss.Kind,
                    boss.Group,
                    board.EmblemPathFor(boss),
                    boss.Subtitle,
                    boss.Rewards ?? Array.Empty<string>(),
                    boss.ReferenceNote,
                    (boss.PopItems ?? Array.Empty<ChartPopItemOption>())
                        .Select(option => new ActivityChartPopItemOptionDto(
                            option.Name, option.Source, option.Label))
                        .ToList(),
                    bossItems.Select(MapChartPopItem).ToList(),
                    bossItems.Count,
                    bossItems.Sum(item => item.Quantity),
                    leadsTo?.Name,
                    leadsTo?.ThemeKey,
                    boss.EndsRow);
            })
            .ToList();

        return new ActivityChartBoardDto(
            linkshellId,
            board.Key,
            board.Label,
            board.Blurb,
            bosses,
            board.PathColumns,
            board.CentersRows,
            new ActivityChartLedgerDto(
                ledger.Bosses,
                ledger.Rows
                    .Select(row => new ActivityChartLedgerRowDto(
                        row.MembershipId,
                        row.CharacterName,
                        row.IsCurrentMember,
                        row.Rank,
                        row.Cells
                            .Select(cell => new ActivityChartLedgerCellDto(
                                cell.Boss, cell.Status, cell.Detail, cell.CreditedItems, cell.TotalItems))
                            .ToList(),
                        row.TotalCredited,
                        row.TotalTracked,
                        row.CreditedPercent))
                    .ToList()),
            canManage
                ? roster
                    .Select(member => new ActivityChartRosterMemberDto(
                        member.MembershipId, member.AppUserId, member.CharacterName, member.Rank,
                        member.AltCharacterNames))
                    .ToList()
                : new List<ActivityChartRosterMemberDto>(),
            await _chartBoards.GetLastUpdatedAsync(linkshellId, board.Key, cancellationToken),
            canManage);
    }

    private static ActivityChartPopItemDto MapChartPopItem(ChartPopItem item) =>
        new(item.Id,
            item.Board,
            item.Boss,
            item.ItemName,
            item.HeldByCharacterName,
            item.HeldByMembershipId,
            item.Quantity,
            item.Notes,
            item.SortOrder,
            item.Credits
                .OrderBy(credit => credit.CharacterName, StringComparer.OrdinalIgnoreCase)
                .Select(credit => new ActivityChartCreditDto(
                    credit.MembershipId, credit.CharacterName, credit.Detail))
                .ToList(),
            item.Credits.Count,
            item.UpdatedAt);

    /// <summary>
    /// Turns a request's credit list into trusted rows, or into the 400 that refuses the whole
    /// request. One helper for all three write paths — add, update and the credits endpoint — so
    /// naming a farmer from another linkshell is refused identically whichever one is called.
    /// A null or empty list resolves to no credits without an error.
    /// </summary>
    private async Task<(IActionResult? Failure, List<ChartResolvedCredit> Resolved)>
        ResolveChartCreditsAsync(
            int linkshellId,
            IReadOnlyList<ActivityChartCreditInput>? requested,
            CancellationToken cancellationToken)
    {
        var drafts = (requested ?? Array.Empty<ActivityChartCreditInput>())
            .Select(credit => new ChartCreditDraft(credit.MembershipId, credit.CharacterName, credit.Detail))
            .ToList();

        var (error, resolved) = await _chartBoards.ResolveCreditsAsync(linkshellId, drafts, cancellationToken);
        return error is not null ? (BadRequest(new { error }), resolved) : (null, resolved);
    }

    /// <summary>
    /// Resolves who is holding an item. Same boundary as the credits: a membership id is checked
    /// against THIS linkshell, so a request cannot park a pop item on a member of another one.
    /// A name with no id is fine — an unsynced character is a real member.
    /// </summary>
    private async Task<(IActionResult? Failure, string? CharacterName, int? MembershipId)>
        ResolveChartHolderAsync(int linkshellId, ChartPopItemDraft draft, CancellationToken cancellationToken)
    {
        if (draft.HeldByMembershipId is not { } membershipId)
        {
            return (null, draft.HeldByCharacterName, null);
        }

        var name = await _dbContext.AppUserLinkshells
            .AsNoTracking()
            .Where(member => member.LinkshellId == linkshellId && member.Id == membershipId)
            .Select(member => member.CharacterName)
            .FirstOrDefaultAsync(cancellationToken);

        if (string.IsNullOrWhiteSpace(name))
        {
            return (BadRequest(new { error = "That member is not in this linkshell." }), null, null);
        }

        return (null, name, membershipId);
    }

    private async Task<(IActionResult? Failure, ChartBoardActor Actor)> AuthorizeChartsWriteAsync(
        int linkshellId, CancellationToken cancellationToken)
    {
        var appUser = await ResolveAppUserAsync(cancellationToken);
        if (appUser is null)
        {
            return (Unauthorized(new { error = "Sign in to edit the chart." }), default);
        }

        var membership = await GetMembershipAsync(appUser.Id, linkshellId, cancellationToken);
        if (!await CanAsync(membership, role => role.CanManageCharts, cancellationToken))
        {
            return (Forbid(), default);
        }

        return (null, new ChartBoardActor(appUser.Id, membership!.CharacterName ?? appUser.CharacterName));
    }

    private async Task<(IActionResult? Failure, ChartPopItem? Item, ChartBoardActor Actor)>
        LoadChartPopItemForWriteAsync(int itemId, CancellationToken cancellationToken)
    {
        var item = await _dbContext.ChartPopItems
            .FirstOrDefaultAsync(row => row.Id == itemId, cancellationToken);
        if (item is null)
        {
            return (NotFound(new { error = "That pop item was not found." }), null, default);
        }

        // Deliberately re-runs the full gate for the row's OWN linkshell rather than comparing ids
        // here. The per-linkshell Discord guild lock lives inside GetMembershipAsync; checking
        // item.LinkshellId by hand looks equivalent and silently bypasses it.
        var gate = await AuthorizeChartsWriteAsync(item.LinkshellId, cancellationToken);
        return gate.Failure is not null ? (gate.Failure, null, default) : (null, item, gate.Actor);
    }
}
