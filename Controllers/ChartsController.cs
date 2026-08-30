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
    private readonly AdminOverrideService _adminOverride;
    private readonly ChartBoardService _chartBoards;
    private readonly ChartWishlistService _chartWishlist;
    private readonly ChartKeyItemService _chartKeyItems;

    public ChartsController(
        ApplicationDbContext context,
        UserManager<AppUser> userManager,
        AdminOverrideService adminOverride,
        ChartBoardService chartBoards,
        ChartWishlistService chartWishlist,
        ChartKeyItemService chartKeyItems)
    {
        _context = context;
        _userManager = userManager;
        _adminOverride = adminOverride;
        _chartBoards = chartBoards;
        _chartWishlist = chartWishlist;
        _chartKeyItems = chartKeyItems;
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

        // The viewer's own membership, for the key item row that is theirs to tick. Read off the
        // roster that was just loaded rather than as a second query.
        var viewerMembershipId = roster
            .FirstOrDefault(member => member.AppUserId == user.Id)?.MembershipId;

        // Built with the SAME roster the cards use, so a badge and the list below it cannot come
        // from two different reads.
        var wishlist = ChartWishlistService.BuildWishlist(
            board,
            await _chartWishlist.LoadAsync(linkshellId.Value, board.Key, cancellationToken),
            user.Id,
            canManage);

        var keyItems = ChartKeyItemService.BuildGrid(
            board,
            await _chartKeyItems.LoadAsync(linkshellId.Value, board.Key, cancellationToken),
            roster);

        var keyItemsByBoss = keyItems.Columns
            .Where(column => column.Boss is not null)
            .ToDictionary(column => column.Boss!, StringComparer.OrdinalIgnoreCase);

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
                        DropItemOptions = boss.DropItems ?? Array.Empty<ChartPopItemOption>(),
                        // Counted off the list built above, never queried per card.
                        PendingRequestCount = wishlist.PendingCountsByBoss
                            .TryGetValue(boss.Name, out var requested) ? requested : 0,
                        KeyItemName = keyItemsByBoss
                            .TryGetValue(boss.Name, out var keyItem) ? keyItem.Name : null,
                        KeyItemHaveCount = keyItem?.HaveCount ?? 0,
                        KeyItemTotalMembers = keyItem?.TotalMembers ?? 0,
                        KeyItemMissing = keyItem?.MissingCharacterNames ?? Array.Empty<string>(),
                        Items = items
                            .Where(item => string.Equals(item.Boss, boss.Name, StringComparison.OrdinalIgnoreCase))
                            .Select(item => new ChartPopItemViewModel
                            {
                                Id = item.Id,
                                Boss = item.Boss,
                                ItemName = item.ItemName,
                                Kind = item.Kind,
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
            AllowsPopItems = board.AllowsPopItems,
            AllowsDropItems = board.AllowsDropItems,
            AllowsWishlist = board.AllowsWishlist,
            AllowsKeyItems = board.AllowsKeyItems,
            Wishlist = wishlist,
            KeyItems = keyItems,
            ViewerMembershipId = viewerMembershipId,
            // Membership, not CanManage. This is the one part of Charts a plain member writes.
            CanRequest = board.AllowsWishlist,
        });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> AddItem(
        string? board, string? boss, string? itemName, string? heldByCharacterName, int quantity, string? notes,
        int[]? membershipIds, string? kind,
        CancellationToken cancellationToken)
    {
        var gate = await AuthorizeWriteAsync(cancellationToken);
        if (gate.Failure is not null) return gate.Failure;

        var draft = ChartBoardService.NormalizeDraft(
            board, boss, itemName, heldByCharacterName, null, quantity, notes, kind);
        if (draft is null)
        {
            TempData["ChartsError"] = "Pick a boss on this board and give the item a name.";
            return BackTo(board);
        }

        // THE one place a board's pop/drop feature flag is enforced.
        //
        // Deliberately not inside NormalizeDraft, which EditItem and DeleteItem's sibling also call:
        // Dynamis and Limbus still hold rows entered before they stopped taking adds, and a check
        // down there would make every one of them permanently uneditable. Server-side because "the
        // form is not rendered" is not a gate - this is a plain form post.
        var catalog = ChartBoardCatalog.Find(draft.Board)!;
        if (!(draft.Kind == ChartItemKinds.Drop ? catalog.AllowsDropItems : catalog.AllowsPopItems))
        {
            TempData["ChartsError"] = $"The {catalog.Label} board does not take items of that kind.";
            return BackTo(draft.Board);
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
            Kind = draft.Kind,
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
        // The row's own board AND its own kind are authoritative: an item moves between bosses,
        // never between boards and never between kinds. Taking the kind from the form would let an
        // edit relabel a pop item as a drop, and would need the feature check that must not be on
        // this path at all.
        var draft = ChartBoardService.NormalizeDraft(
            item.Board, boss, itemName, heldByCharacterName, null, quantity, notes, item.Kind);
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

        // Read BEFORE the delete, so the message does not depend on a removed entity still being
        // readable off the change tracker.
        var board = found.Item!.Board;
        var wasDrop = found.Item!.Kind == ChartItemKinds.Drop;

        _context.ChartPopItems.Remove(found.Item!);
        await _context.SaveChangesAsync(cancellationToken);
        TempData["ChartsMessage"] = wasDrop ? "Drop item removed." : "Pop item removed.";
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

    // ---- the wishlist -----------------------------------------------------------
    //
    // The one part of Charts a member without CanManageCharts may write, which is why these use
    // AuthorizeMemberAsync rather than AuthorizeWriteAsync, and why the ownership decisions come
    // from ChartWishlistService rather than from a comparison written here.

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> AddWishlistRequest(
        string? board, string? boss, string? itemName, int quantity, string? notes,
        CancellationToken cancellationToken)
    {
        var gate = await AuthorizeMemberAsync(cancellationToken);
        if (gate.Failure is not null) return gate.Failure;

        var draft = ChartWishlistService.NormalizeDraft(board, boss, itemName, quantity, notes);
        if (draft is null)
        {
            TempData["ChartsError"] =
                "Give the item a name, and pick a zone on this board or leave it as anywhere.";
            return BackTo(board);
        }

        var now = DateTime.UtcNow;
        _context.ChartWishlistRequests.Add(new ChartWishlistRequest
        {
            LinkshellId = gate.LinkshellId,
            Board = draft.Board,
            Boss = draft.Boss,
            ItemName = draft.ItemName,
            Quantity = draft.Quantity,
            Notes = draft.Notes,
            Status = ChartWishlistStatuses.Pending,
            Priority = await _chartWishlist.NextPriorityAsync(
                gate.LinkshellId, draft.Board, cancellationToken),
            // Requested FOR the caller, always. There is deliberately no "on behalf of": an officer
            // entering somebody else's want would own it, and the ownership rule keys on this column.
            RequestedByAppUserId = gate.Actor.AppUserId,
            RequestedByMembershipId = gate.MembershipId,
            RequestedByCharacterName = gate.Actor.CharacterName ?? string.Empty,
            RequestedAt = now,
            UpdatedAt = now,
        });

        await _context.SaveChangesAsync(cancellationToken);
        TempData["ChartsMessage"] = $"Requested {draft.ItemName}.";
        return BackTo(draft.Board);
    }

    /// <summary>
    /// Withdraws a request, which DELETES it. An officer removing somebody else's is the same
    /// operation, so one action covers both - see ChartWishlistStatuses for why there is no third
    /// status.
    /// </summary>
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> WithdrawWishlistRequest(int id, CancellationToken cancellationToken)
    {
        var found = await LoadWishlistForWriteAsync(id, cancellationToken);
        if (found.Failure is not null) return found.Failure;

        var row = found.Row!;
        if (!ChartWishlistService.CanEditRequest(row, found.Actor.AppUserId, found.CanManage))
        {
            return Forbid();
        }

        var board = row.Board;
        _context.ChartWishlistRequests.Remove(row);
        await _context.SaveChangesAsync(cancellationToken);
        TempData["ChartsMessage"] = "Item request removed.";
        return BackTo(board);
    }

    // Officers only, unlike withdrawing: marking a request fulfilled is a claim about what the
    // linkshell did, not about what somebody wants.
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SetWishlistStatus(
        int id, string? status, CancellationToken cancellationToken)
    {
        var found = await LoadWishlistForWriteAsync(id, cancellationToken);
        if (found.Failure is not null) return found.Failure;
        if (!found.CanManage) return Forbid();

        var canonical = ChartWishlistService.NormalizeStatus(status);
        if (canonical is null)
        {
            TempData["ChartsError"] = "That is not a request status.";
            return BackTo(found.Row!.Board);
        }

        var row = found.Row!;
        row.Status = canonical;
        row.UpdatedAt = DateTime.UtcNow;

        // The stamp follows the status BOTH ways, so a request put back to pending stops claiming
        // somebody settled it.
        var fulfilled = canonical == ChartWishlistStatuses.Fulfilled;
        row.FulfilledAt = fulfilled ? DateTime.UtcNow : null;
        row.FulfilledByAppUserId = fulfilled ? found.Actor.AppUserId : null;
        row.FulfilledByCharacterName = fulfilled ? found.Actor.CharacterName : null;

        await _context.SaveChangesAsync(cancellationToken);
        TempData["ChartsMessage"] = fulfilled
            ? $"Marked {row.ItemName} fulfilled."
            : $"Put {row.ItemName} back on the list.";
        return BackTo(row.Board);
    }

    /// <summary>
    /// Reorders a board's queue set-wise: the post carries the COMPLETE ordered id list, and an id
    /// from elsewhere refuses the whole thing rather than shuffling the rest.
    /// </summary>
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ReorderWishlist(
        string? board, int[]? orderedIds, CancellationToken cancellationToken)
    {
        var gate = await AuthorizeWriteAsync(cancellationToken);
        if (gate.Failure is not null) return gate.Failure;

        var catalog = ChartBoardCatalog.Find(board);
        if (catalog is null || !catalog.AllowsWishlist)
        {
            TempData["ChartsError"] = "That board takes no item requests.";
            return BackTo(board);
        }

        var error = await _chartWishlist.ReorderAsync(
            gate.LinkshellId, catalog.Key, orderedIds ?? Array.Empty<int>(), cancellationToken);
        if (error is not null)
        {
            TempData["ChartsError"] = error;
            return BackTo(catalog.Key);
        }

        await _context.SaveChangesAsync(cancellationToken);
        TempData["ChartsMessage"] = "Request order saved.";
        return BackTo(catalog.Key);
    }

    // ---- key items --------------------------------------------------------------

    /// <summary>
    /// Ticks or unticks one cell. BOTH self-serve and officer override: a member may set their own,
    /// an officer anybody's, and the two write an identical row distinguishable only by its audit
    /// columns. The rule is ChartKeyItemService.CanSetKeyItemFor, shared with the Activity.
    /// </summary>
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SetKeyItem(
        string? board, string? keyItemName, int membershipId, bool has,
        CancellationToken cancellationToken)
    {
        var gate = await AuthorizeMemberAsync(cancellationToken);
        if (gate.Failure is not null) return gate.Failure;

        if (!ChartKeyItemService.CanSetKeyItemFor(membershipId, gate.MembershipId, gate.CanManage))
        {
            return Forbid();
        }

        // SetAsync draws the other two boundaries: the name against the CLOSED catalog list, and the
        // membership against THIS linkshell.
        var error = await _chartKeyItems.SetAsync(
            gate.LinkshellId, board, keyItemName, membershipId, has, gate.Actor, cancellationToken);
        if (error is not null)
        {
            TempData["ChartsError"] = error;
            return BackTo(board);
        }

        await _context.SaveChangesAsync(cancellationToken);
        TempData["ChartsMessage"] = has ? "Key item ticked off." : "Key item cleared.";
        return BackTo(board);
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
        // The app-wide admin override is applied here too, so both surfaces grant it
        // identically — the escalation-by-front-end hazard above cuts both ways.
        if (await _adminOverride.IsActiveForAsync(appUserId, cancellationToken)) return true;
        if (LinkshellRanks.IsLeader(membership.Rank)) return true;

        var role = await _context.LinkshellRoles
            .AsNoTracking()
            .FirstOrDefaultAsync(item => item.LinkshellId == linkshellId && item.Name == membership.Rank, cancellationToken);
        return role?.CanManageCharts == true;
    }

    /// <summary>
    /// MEMBERSHIP, and nothing more - the gate for the wishlist and for key items, which are the
    /// first Charts writes a plain member may make.
    ///
    /// Returns CanManage alongside, so an action branches ONCE rather than asking twice and risking
    /// two different answers within one request.
    ///
    /// Twin of ActivityDataController.AuthorizeChartsMemberAsync, and the two MUST be changed
    /// together. A coarse check on one front-end and a named permission on the other is a privilege
    /// escalation available by picking a front-end - see the comment on CanManageAsync below.
    /// </summary>
    private async Task<(IActionResult? Failure, int LinkshellId, ChartBoardActor Actor,
                        int? MembershipId, bool CanManage)>
        AuthorizeMemberAsync(CancellationToken cancellationToken)
    {
        var user = await _userManager.GetUserAsync(User);
        if (user is null) return (Challenge(), 0, default, null, false);

        var linkshellId = user.PrimaryLinkshellId;
        if (!linkshellId.HasValue) return (Forbid(), 0, default, null, false);

        var membership = await _context.AppUserLinkshells
            .AsNoTracking()
            .FirstOrDefaultAsync(link => link.AppUserId == user.Id && link.LinkshellId == linkshellId.Value, cancellationToken);
        if (membership is null) return (Forbid(), 0, default, null, false);

        return (
            null,
            linkshellId.Value,
            new ChartBoardActor(user.Id, membership.CharacterName ?? user.CharacterName),
            membership.Id,
            await CanManageAsync(user.Id, linkshellId.Value, cancellationToken));
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
    /// <summary>
    /// Loads a request for writing and re-runs the MEMBER gate against the ROW's linkshell, so an id
    /// from another linkshell is refused rather than edited. Twin of LoadForWriteAsync below.
    ///
    /// Returns CanManage rather than deciding anything: which of the two rules applies is the
    /// caller's business, and both of them live in ChartWishlistService.
    /// </summary>
    private async Task<(IActionResult? Failure, ChartWishlistRequest? Row, ChartBoardActor Actor, bool CanManage)>
        LoadWishlistForWriteAsync(int requestId, CancellationToken cancellationToken)
    {
        var user = await _userManager.GetUserAsync(User);
        if (user is null) return (Challenge(), null, default, false);

        var row = await _context.ChartWishlistRequests
            .FirstOrDefaultAsync(item => item.Id == requestId, cancellationToken);
        if (row is null) return (NotFound(), null, default, false);

        var membership = await _context.AppUserLinkshells
            .AsNoTracking()
            .FirstOrDefaultAsync(link => link.AppUserId == user.Id && link.LinkshellId == row.LinkshellId, cancellationToken);
        if (membership is null) return (Forbid(), null, default, false);

        return (
            null,
            row,
            new ChartBoardActor(user.Id, membership.CharacterName ?? user.CharacterName),
            await CanManageAsync(user.Id, row.LinkshellId, cancellationToken));
    }

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
