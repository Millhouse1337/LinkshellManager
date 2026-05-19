using LinkshellManagerDiscordApp.Data;
using LinkshellManagerDiscordApp.Models;
using LinkshellManagerDiscordApp.Services;
using LinkshellManagerDiscordApp.ViewModels;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace LinkshellManagerDiscordApp.Controllers;

[Authorize]
public class LootHistoryController : Controller
{
    private readonly ApplicationDbContext _context;
    private readonly UserManager<AppUser> _userManager;
    private readonly TimeZoneConversionService _timeZones;
    private readonly LootEditService _lootEditService;
    private readonly SheetSyncQueue _sheetSync;

    public LootHistoryController(
        ApplicationDbContext context,
        UserManager<AppUser> userManager,
        TimeZoneConversionService timeZones,
        LootEditService lootEditService,
        SheetSyncQueue sheetSync)
    {
        _context = context;
        _userManager = userManager;
        _timeZones = timeZones;
        _lootEditService = lootEditService;
        _sheetSync = sheetSync;
    }

    [HttpGet("/LootHistory/Add")]
    public async Task<IActionResult> Add()
    {
        var user = await _userManager.GetUserAsync(User);
        if (user is null) return Challenge();

        var (linkshellId, linkshellName) = await ResolvePrimaryLinkshellAsync(user);
        if (linkshellId is null || linkshellId == 0)
        {
            TempData["LootHistoryMessage"] = "Join or select a linkshell before adding loot.";
            return RedirectToAction(nameof(Index));
        }

        var membership = await _context.AppUserLinkshells
            .Include(link => link.Linkshell)
            .FirstOrDefaultAsync(link => link.AppUserId == user.Id && link.LinkshellId == linkshellId);
        if (membership is null) return Forbid();
        if (!await ResolveCanAddLootAsync(membership)) return Forbid();

        return View(new LootAddViewModel
        {
            LinkshellId = linkshellId.Value,
            LinkshellName = linkshellName,
            LinkshellLootStructure = membership.Linkshell?.LootStructure,
            RosterCharacterNames = await LoadRosterCharacterNamesAsync(linkshellId.Value)
        });
    }

    [HttpPost("/LootHistory/Add")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Add(LootAddViewModel model)
    {
        var user = await _userManager.GetUserAsync(User);
        if (user is null) return Challenge();

        var (linkshellId, linkshellName) = await ResolvePrimaryLinkshellAsync(user);
        if (linkshellId is null || linkshellId == 0) return NotFound();

        var membership = await _context.AppUserLinkshells
            .Include(link => link.Linkshell)
            .FirstOrDefaultAsync(link => link.AppUserId == user.Id && link.LinkshellId == linkshellId);
        if (membership is null) return Forbid();
        if (!await ResolveCanAddLootAsync(membership)) return Forbid();

        var roster = await LoadRosterCharacterNamesAsync(linkshellId.Value);

        // "Other" in the Source/monster picker means the real label was
        // typed into the free-text field; fold it into Context so the rest
        // of the pipeline (and history) only ever sees the actual value.
        // Context is optional, so a blank pick / blank custom stays null.
        if (string.Equals(model.Context?.Trim(), TodManagerViewModel.OtherMonster,
                StringComparison.OrdinalIgnoreCase))
        {
            model.Context = string.IsNullOrWhiteSpace(model.CustomContext)
                ? null
                : model.CustomContext.Trim();
        }
        else
        {
            model.Context = string.IsNullOrWhiteSpace(model.Context)
                ? null
                : model.Context.Trim();
        }

        if (ModelState.IsValid
            && !roster.Any(n => string.Equals(n, model.ItemWinner, StringComparison.OrdinalIgnoreCase)))
        {
            ModelState.AddModelError(nameof(model.ItemWinner), "Winner must be a current linkshell member.");
        }

        if (!ModelState.IsValid)
        {
            model.LinkshellId = linkshellId.Value;
            model.LinkshellName = linkshellName;
            model.LinkshellLootStructure = membership.Linkshell?.LootStructure;
            model.RosterCharacterNames = roster;
            return View(model);
        }

        var nowUtc = DateTime.UtcNow;
        // One lightweight backing ToD per submission so the loot flows through
        // the existing ToD-loot DKP deduction + ManualPoints per-day pipeline
        // and shows in history (Source = ToD) with full edit support.
        var tod = new Tod
        {
            LinkshellId = linkshellId.Value,
            MonsterName = string.IsNullOrWhiteSpace(model.Context) ? "Manual Loot" : model.Context!.Trim(),
            Claim = true,
            Time = nowUtc,
            TimeStamp = nowUtc,
            TotalClaims = 1
        };
        var detail = new TodLootDetail
        {
            Tod = tod,
            ItemName = model.ItemName!.Trim(),
            ItemWinner = model.ItemWinner!.Trim(),
            WinningDkpSpent = model.WinningDkpSpent
        };
        _context.Tods.Add(tod);
        _context.TodLootDetails.Add(detail);
        await ActivityDataController.AdjustTodLootDkpAsync(
            _context, tod, new[] { detail }, nowUtc, isRefund: false, HttpContext.RequestAborted);
        await _context.SaveChangesAsync();
        // Recompute this day's ManualPoints column (idempotent; edits/deletes
        // self-correct via Loot History Edit).
        await _sheetSync.EnqueueTodLootDeductionsAsync(tod.Id, HttpContext.RequestAborted);

        TempData["LootHistoryMessage"] = $"Loot added: {detail.ItemName} → {detail.ItemWinner} ({detail.WinningDkpSpent} DKP).";
        return RedirectToAction(nameof(Index));
    }

    // GET /LootHistory — paginated combined ToD + Event loot for the user's
    // primary linkshell. Edit buttons render only when the caller has the
    // CanAddLoot role flag on their membership.
    public async Task<IActionResult> Index(
        string? source = "all",
        string? q = null,
        int page = 1)
    {
        var user = await _userManager.GetUserAsync(User);
        if (user is null)
        {
            return Challenge();
        }

        var (linkshellId, linkshellName) = await ResolvePrimaryLinkshellAsync(user);
        var viewModel = new LootHistoryIndexViewModel
        {
            SelectedLinkshellId = linkshellId,
            SelectedLinkshellName = linkshellName,
            SourceFilter = (source ?? "all").Trim().ToLowerInvariant(),
            QueryFilter = string.IsNullOrWhiteSpace(q) ? null : q.Trim()
        };

        if (linkshellId is null || linkshellId == 0)
        {
            return View(viewModel);
        }

        var membership = await _context.AppUserLinkshells
            .Include(link => link.Linkshell)
            .FirstOrDefaultAsync(link => link.AppUserId == user.Id && link.LinkshellId == linkshellId);
        if (membership is null)
        {
            return View(viewModel);
        }

        viewModel.CanEdit = await ResolveCanAddLootAsync(membership);

        var entries = await LoadEntriesAsync(linkshellId.Value, viewModel.SourceFilter, user.TimeZone);
        if (!string.IsNullOrWhiteSpace(viewModel.QueryFilter))
        {
            var needle = viewModel.QueryFilter;
            entries = entries
                .Where(e =>
                    e.ItemWinner?.Contains(needle, StringComparison.OrdinalIgnoreCase) == true ||
                    e.ItemName?.Contains(needle, StringComparison.OrdinalIgnoreCase) == true)
                .ToList();
        }

        viewModel.TotalCount = entries.Count;
        viewModel.PageNumber = Math.Clamp(page, 1, viewModel.TotalPages);
        viewModel.Entries = entries
            .Skip((viewModel.PageNumber - 1) * viewModel.PageSize)
            .Take(viewModel.PageSize)
            .ToList();

        return View(viewModel);
    }

    [HttpGet("/LootHistory/Tod/{lootDetailId:int}/Edit")]
    public async Task<IActionResult> EditTod(int lootDetailId)
    {
        var user = await _userManager.GetUserAsync(User);
        if (user is null)
        {
            return Challenge();
        }

        var detail = await _context.TodLootDetails
            .Include(d => d.Tod)
                .ThenInclude(t => t!.Linkshell)
            .FirstOrDefaultAsync(d => d.Id == lootDetailId);
        if (detail is null || detail.Tod is null)
        {
            return NotFound();
        }

        if (!await CallerCanEditAsync(user.Id, detail.Tod.LinkshellId))
        {
            return Forbid();
        }

        var roster = await LoadRosterCharacterNamesAsync(detail.Tod.LinkshellId);
        return View("Edit", new LootHistoryEditViewModel
        {
            LootDetailId = detail.Id,
            Source = "Tod",
            Context = detail.Tod.MonsterName,
            CurrentItemName = detail.ItemName,
            CurrentItemWinner = detail.ItemWinner,
            CurrentWinningDkpSpent = detail.WinningDkpSpent,
            LinkshellLootStructure = detail.Tod.Linkshell?.LootStructure,
            ItemName = detail.ItemName,
            ItemWinner = detail.ItemWinner,
            WinningDkpSpent = detail.WinningDkpSpent,
            RosterCharacterNames = roster
        });
    }

    [HttpGet("/LootHistory/Event/{lootDetailId:int}/Edit")]
    public async Task<IActionResult> EditEvent(int lootDetailId)
    {
        var user = await _userManager.GetUserAsync(User);
        if (user is null)
        {
            return Challenge();
        }

        var detail = await _context.EventLootDetails
            .Include(d => d.Event)
                .ThenInclude(e => e!.Linkshell)
            .Include(d => d.EventHistory)
                .ThenInclude(h => h!.Linkshell)
            .FirstOrDefaultAsync(d => d.Id == lootDetailId);
        if (detail is null)
        {
            return NotFound();
        }

        var parentLinkshellId = detail.EventHistory?.LinkshellId ?? detail.Event?.LinkshellId;
        if (!parentLinkshellId.HasValue)
        {
            return NotFound();
        }

        if (!await CallerCanEditAsync(user.Id, parentLinkshellId.Value))
        {
            return Forbid();
        }

        var roster = await LoadRosterCharacterNamesAsync(parentLinkshellId.Value);
        return View("Edit", new LootHistoryEditViewModel
        {
            LootDetailId = detail.Id,
            Source = "Event",
            Context = detail.EventHistory?.EventName ?? detail.Event?.EventName,
            CurrentItemName = detail.ItemName,
            CurrentItemWinner = detail.ItemWinner,
            CurrentWinningDkpSpent = detail.WinningDkpSpent,
            LinkshellLootStructure = (detail.EventHistory?.Linkshell ?? detail.Event?.Linkshell)?.LootStructure,
            ItemName = detail.ItemName,
            ItemWinner = detail.ItemWinner,
            WinningDkpSpent = detail.WinningDkpSpent,
            RosterCharacterNames = roster
        });
    }

    [HttpPost("/LootHistory/Tod/{lootDetailId:int}/Edit")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> EditTod(int lootDetailId, LootHistoryEditViewModel model)
    {
        return await HandleEditPostAsync(lootDetailId, model, isTod: true);
    }

    [HttpPost("/LootHistory/Event/{lootDetailId:int}/Edit")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> EditEvent(int lootDetailId, LootHistoryEditViewModel model)
    {
        return await HandleEditPostAsync(lootDetailId, model, isTod: false);
    }

    [HttpPost("/LootHistory/Tod/{lootDetailId:int}/Delete")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteTod(int lootDetailId, string? reason)
    {
        return await HandleDeletePostAsync(lootDetailId, reason, isTod: true);
    }

    [HttpPost("/LootHistory/Event/{lootDetailId:int}/Delete")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteEvent(int lootDetailId, string? reason)
    {
        return await HandleDeletePostAsync(lootDetailId, reason, isTod: false);
    }

    // --- internals ---

    // Deleting a loot row removes it and refunds the winner's DKP. The same
    // CanAddLoot flag that gates Edit gates Delete (it's the "manage recorded
    // loot" permission). A reason is optional from the list view; it defaults
    // so the refund ledger entry is still auditable.
    private async Task<IActionResult> HandleDeletePostAsync(int lootDetailId, string? reason, bool isTod)
    {
        var user = await _userManager.GetUserAsync(User);
        if (user is null)
        {
            return Challenge();
        }

        int? linkshellId;
        if (isTod)
        {
            linkshellId = await _context.TodLootDetails
                .Where(detail => detail.Id == lootDetailId)
                .Select(detail => (int?)(detail.Tod != null ? detail.Tod.LinkshellId : 0))
                .FirstOrDefaultAsync();
        }
        else
        {
            linkshellId = await _context.EventLootDetails
                .Where(detail => detail.Id == lootDetailId)
                .Select(detail => detail.EventHistory != null
                    ? (int?)detail.EventHistory.LinkshellId
                    : (detail.Event != null ? (int?)detail.Event.LinkshellId : null))
                .FirstOrDefaultAsync();
        }

        if (!linkshellId.HasValue || linkshellId.Value == 0)
        {
            return NotFound();
        }

        if (!await CallerCanEditAsync(user.Id, linkshellId.Value))
        {
            return Forbid();
        }

        var deleteReason = string.IsNullOrWhiteSpace(reason)
            ? "Loot record deleted via Loot History."
            : reason.Trim();

        var result = isTod
            ? await _lootEditService.DeleteTodLootAsync(lootDetailId, user, deleteReason, DateTime.UtcNow, HttpContext.RequestAborted)
            : await _lootEditService.DeleteEventLootAsync(lootDetailId, user, deleteReason, DateTime.UtcNow, HttpContext.RequestAborted);

        TempData["LootHistoryMessage"] = result.Success
            ? "Loot record deleted and DKP refunded."
            : (result.ErrorMessage ?? "Loot delete failed.");
        return RedirectToAction(nameof(Index));
    }

    private async Task<IActionResult> HandleEditPostAsync(int lootDetailId, LootHistoryEditViewModel model, bool isTod)
    {
        var user = await _userManager.GetUserAsync(User);
        if (user is null)
        {
            return Challenge();
        }

        int? linkshellId;
        if (isTod)
        {
            linkshellId = await _context.TodLootDetails
                .Where(detail => detail.Id == lootDetailId)
                .Select(detail => (int?)(detail.Tod != null ? detail.Tod.LinkshellId : 0))
                .FirstOrDefaultAsync();
        }
        else
        {
            linkshellId = await _context.EventLootDetails
                .Where(detail => detail.Id == lootDetailId)
                .Select(detail => detail.EventHistory != null
                    ? (int?)detail.EventHistory.LinkshellId
                    : (detail.Event != null ? (int?)detail.Event.LinkshellId : null))
                .FirstOrDefaultAsync();
        }

        if (!linkshellId.HasValue || linkshellId.Value == 0)
        {
            return NotFound();
        }

        if (!await CallerCanEditAsync(user.Id, linkshellId.Value))
        {
            return Forbid();
        }

        if (!ModelState.IsValid)
        {
            // Re-load roster + context for the view re-render so the form
            // shows the previous current-state alongside the validation
            // errors. Cheap query and only on the unhappy path.
            model.RosterCharacterNames = await LoadRosterCharacterNamesAsync(linkshellId.Value);
            return View("Edit", model);
        }

        var serviceRequest = new LootEditRequest(
            ItemName: model.ItemName,
            ItemWinner: model.ItemWinner,
            WinningDkpSpent: model.WinningDkpSpent,
            Reason: model.Reason ?? string.Empty);

        LootEditResult result;
        try
        {
            result = isTod
                ? await _lootEditService.EditTodLootAsync(lootDetailId, serviceRequest, user, DateTime.UtcNow)
                : await _lootEditService.EditEventLootAsync(lootDetailId, serviceRequest, user, DateTime.UtcNow);
        }
        catch (InvalidOperationException ex)
        {
            ModelState.AddModelError(string.Empty, ex.Message);
            model.RosterCharacterNames = await LoadRosterCharacterNamesAsync(linkshellId.Value);
            return View("Edit", model);
        }

        if (!result.Success)
        {
            ModelState.AddModelError(string.Empty, result.ErrorMessage ?? "Loot edit failed.");
            model.RosterCharacterNames = await LoadRosterCharacterNamesAsync(linkshellId.Value);
            return View("Edit", model);
        }

        TempData["LootHistoryMessage"] = "Loot record updated.";
        return RedirectToAction(nameof(Index));
    }

    private async Task<(int? LinkshellId, string? LinkshellName)> ResolvePrimaryLinkshellAsync(AppUser user)
    {
        if (user.PrimaryLinkshellId.HasValue)
        {
            var ls = await _context.Linkshells
                .AsNoTracking()
                .Where(l => l.Id == user.PrimaryLinkshellId.Value)
                .Select(l => new { l.Id, l.LinkshellName })
                .FirstOrDefaultAsync();
            if (ls is not null)
            {
                return (ls.Id, ls.LinkshellName);
            }
        }

        var fallback = await _context.AppUserLinkshells
            .AsNoTracking()
            .Include(link => link.Linkshell)
            .Where(link => link.AppUserId == user.Id)
            .Select(link => new { link.LinkshellId, link.Linkshell!.LinkshellName })
            .FirstOrDefaultAsync();
        return fallback is null ? (null, null) : (fallback.LinkshellId, fallback.LinkshellName);
    }

    private async Task<bool> CallerCanEditAsync(string appUserId, int linkshellId)
    {
        var membership = await _context.AppUserLinkshells
            .FirstOrDefaultAsync(link => link.AppUserId == appUserId && link.LinkshellId == linkshellId);
        if (membership is null)
        {
            return false;
        }
        return await ResolveCanAddLootAsync(membership);
    }

    private async Task<bool> ResolveCanAddLootAsync(AppUserLinkshell membership)
    {
        // Mirror the activity's CanAsync(role => role.CanAddLoot) flow:
        // resolve the LinkshellRole by rank name, fall back to "Member" if
        // missing, then read CanAddLoot off it.
        var rank = string.IsNullOrWhiteSpace(membership.Rank) ? "Member" : membership.Rank.Trim();
        var role = await _context.LinkshellRoles
            .AsNoTracking()
            .FirstOrDefaultAsync(r => r.LinkshellId == membership.LinkshellId && r.Name == rank);
        if (role is null && !string.Equals(rank, "Member", StringComparison.OrdinalIgnoreCase))
        {
            role = await _context.LinkshellRoles
                .AsNoTracking()
                .FirstOrDefaultAsync(r => r.LinkshellId == membership.LinkshellId && r.Name == "Member");
        }
        return role?.CanAddLoot ?? false;
    }

    private async Task<List<string>> LoadRosterCharacterNamesAsync(int linkshellId)
    {
        return await _context.AppUserLinkshells
            .AsNoTracking()
            .Where(link => link.LinkshellId == linkshellId && !string.IsNullOrEmpty(link.CharacterName))
            .OrderBy(link => link.CharacterName)
            .Select(link => link.CharacterName!)
            .ToListAsync();
    }

    private async Task<List<LootHistoryEntryViewModel>> LoadEntriesAsync(int linkshellId, string sourceFilter, string? userTimeZone)
    {
        var todRows = sourceFilter is "all" or "tod"
            ? await _context.TodLootDetails
                .AsNoTracking()
                .Where(detail => detail.Tod != null && detail.Tod.LinkshellId == linkshellId)
                .Select(detail => new LootHistoryEntryViewModel
                {
                    LootDetailId = detail.Id,
                    Source = "Tod",
                    ParentId = detail.TodId ?? 0,
                    Context = detail.Tod!.MonsterName,
                    OccurredAt = detail.Tod.Time ?? detail.Tod.TimeStamp,
                    ItemName = detail.ItemName,
                    ItemWinner = detail.ItemWinner,
                    WinningDkpSpent = detail.WinningDkpSpent,
                    ActualDeductedDkp = detail.ActualDeductedDkp,
                    LastEditReason = detail.LastEditReason,
                    EditedAt = detail.EditedAt,
                    EditedByCharacterName = detail.EditedByCharacterName
                })
                .ToListAsync()
            : new();

        var eventRows = sourceFilter is "all" or "event"
            ? await _context.EventLootDetails
                .AsNoTracking()
                .Where(detail =>
                    (detail.Event != null && detail.Event.LinkshellId == linkshellId) ||
                    (detail.EventHistory != null && detail.EventHistory.LinkshellId == linkshellId))
                .Select(detail => new LootHistoryEntryViewModel
                {
                    LootDetailId = detail.Id,
                    Source = "Event",
                    ParentId = detail.EventHistoryId ?? detail.EventId ?? 0,
                    Context = detail.EventHistory != null
                        ? detail.EventHistory.EventName
                        : (detail.Event != null ? detail.Event.EventName : null),
                    OccurredAt = detail.EventHistory != null
                        ? detail.EventHistory.EndTime
                        : (detail.Event != null ? (detail.Event.EndTime ?? detail.Event.StartTime) : null),
                    ItemName = detail.ItemName,
                    ItemWinner = detail.ItemWinner,
                    WinningDkpSpent = detail.WinningDkpSpent,
                    ActualDeductedDkp = detail.ActualDeductedDkp,
                    LastEditReason = detail.LastEditReason,
                    EditedAt = detail.EditedAt,
                    EditedByCharacterName = detail.EditedByCharacterName
                })
                .ToListAsync()
            : new();

        var unified = todRows
            .Concat(eventRows)
            .OrderByDescending(row => row.OccurredAt ?? DateTime.MinValue)
            .ThenByDescending(row => row.LootDetailId)
            .ToList();

        // Convert UTC timestamps to the user's profile zone for display.
        foreach (var row in unified)
        {
            row.OccurredAt = _timeZones.ToUserTime(row.OccurredAt, userTimeZone);
            row.EditedAt = _timeZones.ToUserTime(row.EditedAt, userTimeZone);
        }

        return unified;
    }
}
