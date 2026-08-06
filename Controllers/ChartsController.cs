using LinkshellManagerDiscordApp.Data;
using LinkshellManagerDiscordApp.Models;
using LinkshellManagerDiscordApp.Services;
using LinkshellManagerDiscordApp.ViewModels;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace LinkshellManagerDiscordApp.Controllers;

/// <summary>
/// The website's Charts pages — one action per board, all rendering the same view.
///
/// Server-rendered from the DbContext and posted back through MVC actions, like every other page
/// here, rather than fetching /api/activity from the browser: this is the only app in the repo where
/// a page would need JavaScript to render at all, and it would lose PRG and antiforgery ergonomics.
///
/// What is shared with the Activity is the RULES, not the transport. ChartBoardService.BuildLedger
/// and ResolveCreditsAsync are one implementation; this controller and
/// ActivityDataController.Charts are both thin callers, so the two surfaces cannot derive a
/// different ledger from the same rows. Same arrangement as ItemSaleRecorder.
/// </summary>
[Authorize]
public class ChartsController : Controller
{
    private readonly ApplicationDbContext _context;
    private readonly UserManager<AppUser> _userManager;
    private readonly ChartBoardService _chartBoards;

    public ChartsController(
        ApplicationDbContext context,
        UserManager<AppUser> userManager,
        ChartBoardService chartBoards)
    {
        _context = context;
        _userManager = userManager;
        _chartBoards = chartBoards;
    }

    public IActionResult Index() => RedirectToAction(nameof(Sky));

    public Task<IActionResult> Sky(CancellationToken cancellationToken) =>
        RenderBoardAsync(ChartBoardCatalog.Sky, cancellationToken);

    public Task<IActionResult> Sea(CancellationToken cancellationToken) =>
        RenderBoardAsync(ChartBoardCatalog.Sea, cancellationToken);

    public Task<IActionResult> Dynamis(CancellationToken cancellationToken) =>
        RenderBoardAsync(ChartBoardCatalog.Dynamis, cancellationToken);

    public Task<IActionResult> Limbus(CancellationToken cancellationToken) =>
        RenderBoardAsync(ChartBoardCatalog.Limbus, cancellationToken);

    // Named for the board KEY, like the four above — Board.cshtml's sub-nav links with
    // asp-action="@board.Key" and BackTo redirects to it, so a mismatch is a dead link and a throw on
    // every post. ChartBoardCatalogTests.EveryBoard_HasAControllerActionNamedAfterItsKey guards it.
    public Task<IActionResult> HENM(CancellationToken cancellationToken) =>
        RenderBoardAsync(ChartBoardCatalog.Henm, cancellationToken);

    private async Task<IActionResult> RenderBoardAsync(string boardKey, CancellationToken cancellationToken)
    {
        var board = ChartBoardCatalog.Find(boardKey);
        if (board is null) return NotFound();

        var user = await _userManager.GetUserAsync(User);
        if (user is null) return Challenge();

        var linkshellId = user.PrimaryLinkshellId;
        if (!linkshellId.HasValue)
        {
            return View("Board", new ChartBoardViewModel
            {
                BoardKey = board.Key,
                BoardLabel = board.Label,
                Blurb = board.Blurb,
            });
        }

        var canManage = await CanManageAsync(user.Id, linkshellId.Value, cancellationToken);
        var items = await _chartBoards.LoadItemsAsync(linkshellId.Value, board.Key, cancellationToken);
        var roster = await _chartBoards.LoadRosterAsync(linkshellId.Value, cancellationToken);

        return View("Board", new ChartBoardViewModel
        {
            LinkshellId = linkshellId.Value,
            LinkshellName = user.PrimaryLinkshellName,
            BoardKey = board.Key,
            BoardLabel = board.Label,
            Blurb = board.Blurb,
            PathColumns = board.PathColumns,
            CentersRows = board.CentersRows,
            Bosses = board.Bosses
                .Select(boss =>
                {
                    // Resolved to the target CARD, so the arrow badge's spelling and its hue both
                    // come off the thing it points at. Mirrors ActivityDataController.Charts.
                    var leadsTo = board.LeadsToFor(boss);
                    return new ChartBossCardViewModel
                    {
                        Boss = boss.Name,
                        ThemeKey = boss.ThemeKey,
                        Kind = boss.Kind,
                        EmblemPath = "/" + board.EmblemPathFor(boss),
                        Subtitle = boss.Subtitle,
                        Group = boss.Group,
                        LeadsTo = leadsTo?.Name,
                        LeadsToThemeKey = leadsTo?.ThemeKey,
                        EndsRow = boss.EndsRow,
                        Rewards = boss.Rewards ?? Array.Empty<string>(),
                        ReferenceNote = boss.ReferenceNote,
                        PopItemOptions = boss.PopItems ?? Array.Empty<ChartPopItemOption>(),
                        Items = items
                            .Where(item => string.Equals(item.Boss, boss.Name, StringComparison.OrdinalIgnoreCase))
                            .Select(item => new ChartPopItemViewModel
                            {
                                Id = item.Id,
                                Boss = item.Boss,
                                ItemName = item.ItemName,
                                HeldByCharacterName = item.HeldByCharacterName,
                                Quantity = item.Quantity,
                                Notes = item.Notes,
                                CreditedTo = item.Credits
                                    .Select(credit => credit.CharacterName)
                                    .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                                    .ToList(),
                            })
                            .ToList(),
                    };
                })
                .ToList(),
            Ledger = ChartBoardService.BuildLedger(board, items, roster),
            Roster = canManage ? roster : new List<ChartRosterEntry>(),
            LastUpdatedUtc = await _chartBoards.GetLastUpdatedAsync(linkshellId.Value, board.Key, cancellationToken),
            CanManage = canManage,
        });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> AddItem(
        string? board, string? boss, string? itemName, string? heldByCharacterName, int quantity, string? notes,
        int[]? membershipIds,
        CancellationToken cancellationToken)
    {
        var gate = await AuthorizeWriteAsync(cancellationToken);
        if (gate.Failure is not null) return gate.Failure;

        var draft = ChartBoardService.NormalizeDraft(
            board, boss, itemName, heldByCharacterName, null, quantity, notes);
        if (draft is null)
        {
            TempData["ChartsError"] = "Pick a boss on this board and give the item a name.";
            return BackTo(board);
        }

        // The farmers ticked on the add form, resolved before the row is built: naming somebody from
        // another linkshell refuses the whole thing rather than leaving an uncredited row behind.
        // Unticked boxes do not post, so this is the same set-wise contract SetCredits uses.
        var (creditError, credits) = await _chartBoards.ResolveCreditsAsync(
            gate.LinkshellId,
            (membershipIds ?? Array.Empty<int>())
                .Select(membershipId => new ChartCreditDraft(membershipId, null, null))
                .ToList(),
            cancellationToken);
        if (creditError is not null)
        {
            TempData["ChartsError"] = creditError;
            return BackTo(board);
        }

        var now = DateTime.UtcNow;
        var item = new ChartPopItem
        {
            LinkshellId = gate.LinkshellId,
            Board = draft.Board,
            Boss = draft.Boss,
            ItemName = draft.ItemName,
            HeldByCharacterName = draft.HeldByCharacterName,
            Quantity = draft.Quantity,
            Notes = draft.Notes,
            SortOrder = await _chartBoards.NextSortOrderAsync(
                gate.LinkshellId, draft.Board, draft.Boss, cancellationToken),
            CreatedByAppUserId = gate.Actor.AppUserId,
            CreatedByCharacterName = gate.Actor.CharacterName,
            CreatedAt = now,
            UpdatedAt = now,
        };

        // One SaveChanges for the row AND its farmers, so an add that names them cannot half-succeed.
        ChartBoardService.AttachCredits(item, credits, gate.Actor);
        _context.ChartPopItems.Add(item);

        await _context.SaveChangesAsync(cancellationToken);
        TempData["ChartsMessage"] = credits.Count == 0
            ? $"Added {draft.ItemName} to {draft.Boss}."
            : $"Added {draft.ItemName} to {draft.Boss}, credited to {credits.Count} farmer(s).";
        return BackTo(draft.Board);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> EditItem(
        int id, string? boss, string? itemName, string? heldByCharacterName, int quantity, string? notes,
        int[]? membershipIds, bool creditsIncluded,
        CancellationToken cancellationToken)
    {
        var found = await LoadForWriteAsync(id, cancellationToken);
        if (found.Failure is not null) return found.Failure;

        var item = found.Item!;
        // The row's own board is authoritative — an item moves between bosses, never between boards.
        var draft = ChartBoardService.NormalizeDraft(
            item.Board, boss, itemName, heldByCharacterName, null, quantity, notes);
        if (draft is null)
        {
            TempData["ChartsError"] = "Pick a boss on this board and give the item a name.";
            return BackTo(item.Board);
        }

        if (!string.Equals(item.Boss, draft.Boss, StringComparison.OrdinalIgnoreCase))
        {
            item.SortOrder = await _chartBoards.NextSortOrderAsync(
                item.LinkshellId, item.Board, draft.Boss, cancellationToken);
        }

        item.Boss = draft.Boss;
        item.ItemName = draft.ItemName;
        item.HeldByCharacterName = draft.HeldByCharacterName;
        item.HeldByMembershipId = null;
        item.Quantity = draft.Quantity;
        item.Notes = draft.Notes;
        item.UpdatedAt = DateTime.UtcNow;

        // Only when the form CARRIED the credit picker. Unticked boxes do not post, so without that
        // marker an empty list is indistinguishable from a form that never asked — and saving a
        // quantity would silently wipe the row's farmers. Same null-vs-empty contract as the API.
        if (creditsIncluded)
        {
            var (creditError, credits) = await _chartBoards.ResolveCreditsAsync(
                item.LinkshellId,
                (membershipIds ?? Array.Empty<int>())
                    .Select(membershipId => new ChartCreditDraft(membershipId, null, null))
                    .ToList(),
                cancellationToken);
            if (creditError is not null)
            {
                TempData["ChartsError"] = creditError;
                return BackTo(item.Board);
            }

            await _chartBoards.ReplaceCreditsAsync(item, credits, found.Actor, cancellationToken);
        }

        await _context.SaveChangesAsync(cancellationToken);
        TempData["ChartsMessage"] = $"Saved {draft.ItemName}.";
        return BackTo(item.Board);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteItem(int id, CancellationToken cancellationToken)
    {
        var found = await LoadForWriteAsync(id, cancellationToken);
        if (found.Failure is not null) return found.Failure;

        var board = found.Item!.Board;
        _context.ChartPopItems.Remove(found.Item!);
        await _context.SaveChangesAsync(cancellationToken);
        TempData["ChartsMessage"] = "Pop item removed.";
        return BackTo(board);
    }

    /// <summary>
    /// Replaces the COMPLETE farmer list for one row. Unticked boxes do not post, so an absent name
    /// is a removal — exactly the set-based contract the API uses.
    /// </summary>
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SetCredits(int id, int[]? membershipIds, CancellationToken cancellationToken)
    {
        var found = await LoadForWriteAsync(id, cancellationToken);
        if (found.Failure is not null) return found.Failure;

        var item = found.Item!;
        var drafts = (membershipIds ?? Array.Empty<int>())
            .Select(membershipId => new ChartCreditDraft(membershipId, null, null))
            .ToList();

        var (error, resolved) = await _chartBoards.ResolveCreditsAsync(
            item.LinkshellId, drafts, cancellationToken);
        if (error is not null)
        {
            TempData["ChartsError"] = error;
            return BackTo(item.Board);
        }

        await _chartBoards.ReplaceCreditsAsync(item, resolved, found.Actor, cancellationToken);
        await _context.SaveChangesAsync(cancellationToken);
        TempData["ChartsMessage"] = $"Farming credit saved for {item.ItemName}.";
        return BackTo(item.Board);
    }

    /// <summary>
    /// PRG back to whichever board the edit belonged to. Unknown boards fall back to Sky.
    ///
    /// Every board's action name IS its catalog key — Board.cshtml's sub-nav already depends on that
    /// (asp-action="@board.Key") — so the canonical key routes directly rather than through a ternary
    /// that needs a new branch per board. The old two-way version silently sent every Dynamis edit
    /// back to Sky.
    ///
    /// A board in the catalog with no action of that name makes this throw, and makes the sub-nav
    /// render an anchor with NO href at all; only one of those two failures is loud, which is why
    /// ChartBoardCatalogTests.EveryBoard_HasAControllerActionNamedAfterItsKey exists.
    /// </summary>
    private IActionResult BackTo(string? board) =>
        RedirectToAction(ChartBoardCatalog.NormalizeBoard(board) ?? nameof(Sky));

    // ---- authorization ----------------------------------------------------------

    /// <summary>
    /// The granular permission, matching the Activity API exactly. A leader always has it.
    ///
    /// Deliberately NOT LinkshellRanks.IsLeaderOrOfficer: a coarse rank here plus a named permission
    /// in the API is a privilege escalation available by picking a front-end. That is the bug
    /// GrantTreasuryToOfficersWhoUsedIt exists to document — do not "simplify" this back to a rank
    /// check.
    /// </summary>
    private async Task<bool> CanManageAsync(string appUserId, int linkshellId, CancellationToken cancellationToken)
    {
        var membership = await _context.AppUserLinkshells
            .AsNoTracking()
            .FirstOrDefaultAsync(link => link.AppUserId == appUserId && link.LinkshellId == linkshellId, cancellationToken);
        if (membership is null) return false;
        if (LinkshellRanks.IsLeader(membership.Rank)) return true;

        var role = await _context.LinkshellRoles
            .AsNoTracking()
            .FirstOrDefaultAsync(item => item.LinkshellId == linkshellId && item.Name == membership.Rank, cancellationToken);
        return role?.CanManageCharts == true;
    }

    private async Task<(IActionResult? Failure, int LinkshellId, ChartBoardActor Actor)>
        AuthorizeWriteAsync(CancellationToken cancellationToken)
    {
        var user = await _userManager.GetUserAsync(User);
        if (user is null) return (Challenge(), 0, default);

        var linkshellId = user.PrimaryLinkshellId;
        if (!linkshellId.HasValue) return (Forbid(), 0, default);
        if (!await CanManageAsync(user.Id, linkshellId.Value, cancellationToken)) return (Forbid(), 0, default);

        var membership = await _context.AppUserLinkshells
            .AsNoTracking()
            .FirstOrDefaultAsync(link => link.AppUserId == user.Id && link.LinkshellId == linkshellId.Value, cancellationToken);

        return (null, linkshellId.Value,
            new ChartBoardActor(user.Id, membership?.CharacterName ?? user.CharacterName));
    }

    /// <summary>
    /// Loads a row for writing and re-checks the permission against the ROW's linkshell, so an id
    /// from another linkshell is refused rather than edited.
    /// </summary>
    private async Task<(IActionResult? Failure, ChartPopItem? Item, ChartBoardActor Actor)>
        LoadForWriteAsync(int itemId, CancellationToken cancellationToken)
    {
        var user = await _userManager.GetUserAsync(User);
        if (user is null) return (Challenge(), null, default);

        var item = await _context.ChartPopItems
            .FirstOrDefaultAsync(row => row.Id == itemId, cancellationToken);
        if (item is null) return (NotFound(), null, default);

        if (!await CanManageAsync(user.Id, item.LinkshellId, cancellationToken))
        {
            return (Forbid(), null, default);
        }

        var membership = await _context.AppUserLinkshells
            .AsNoTracking()
            .FirstOrDefaultAsync(link => link.AppUserId == user.Id && link.LinkshellId == item.LinkshellId, cancellationToken);

        return (null, item, new ChartBoardActor(user.Id, membership?.CharacterName ?? user.CharacterName));
    }
}
