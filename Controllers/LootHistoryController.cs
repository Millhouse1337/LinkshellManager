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

    private readonly DkpLedgerWriter _dkpLedger;
    private readonly DkpPoolResolver _dkpPools;
    private readonly ManualLootService _manualLoot;

    public LootHistoryController(
        ApplicationDbContext context,
        UserManager<AppUser> userManager,
        TimeZoneConversionService timeZones,
        LootEditService lootEditService,
        DkpLedgerWriter dkpLedger,
        DkpPoolResolver dkpPools,
        ManualLootService manualLoot)
    {
        _context = context;
        _userManager = userManager;
        _timeZones = timeZones;
        _lootEditService = lootEditService;
        _dkpLedger = dkpLedger;
        _dkpPools = dkpPools;
        _manualLoot = manualLoot;
    }

    [HttpGet("/LootHistory/Add")]
    public async Task<IActionResult> Add(string? eventQuery)
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

        var addPoolMap = await _dkpPools.GetMapAsync(linkshellId.Value, HttpContext.RequestAborted);
        var model = new LootAddViewModel
        {
            LinkshellId = linkshellId.Value,
            LinkshellName = linkshellName,
            LinkshellLootStructure = membership.Linkshell?.LootStructure,
            RosterCharacterNames = await LoadRosterCharacterNamesAsync(linkshellId.Value),
            EventQuery = eventQuery,
            // Defaults to the pool HNM maps to. An event-linked pick re-derives from that event's
            // own type server-side, so this only really decides the "No event" case.
            DkpPoolId = addPoolMap.Resolve("HNM"),
            DkpPools = addPoolMap.Pools.Select(pool => new LootDkpPoolOption { Id = pool.Id, Name = pool.Name }).ToList()
        };
        await LoadEventOptionsAsync(model, HttpContext.RequestAborted);
        return View(model);
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

        var poolMap = await _dkpPools.GetMapAsync(linkshellId.Value, HttpContext.RequestAborted);

        async Task<IActionResult> RedisplayAsync()
        {
            model.LinkshellId = linkshellId.Value;
            model.LinkshellName = linkshellName;
            model.LinkshellLootStructure = membership.Linkshell?.LootStructure;
            model.RosterCharacterNames = await LoadRosterCharacterNamesAsync(linkshellId.Value);
            model.DkpPools = poolMap.Pools.Select(pool => new LootDkpPoolOption { Id = pool.Id, Name = pool.Name }).ToList();
            await LoadEventOptionsAsync(model, HttpContext.RequestAborted);
            return View(model);
        }

        if (!ModelState.IsValid)
        {
            return await RedisplayAsync();
        }

        // Everything below — roster match, affordability, the debit, the DkpDebitedAt stamp that
        // keeps a live event's close from charging this a second time — is ManualLootService's.
        var result = await _manualLoot.AddAsync(
            linkshellId.Value,
            ManualLootTarget.Parse(model.SourceKind, model.EventId, model.EventHistoryId),
            model.ItemName,
            model.ItemWinner,
            model.WinningDkpSpent.GetValueOrDefault(),
            model.DkpPoolId,
            HttpContext.RequestAborted);

        if (!result.Success)
        {
            // Attached to the field it is about where that is knowable, so the officer sees the
            // problem next to the input rather than as a banner at the top.
            var key = result.Error?.Contains("member", StringComparison.OrdinalIgnoreCase) == true
                ? nameof(model.ItemWinner)
                : string.Empty;
            ModelState.AddModelError(key, result.Error ?? "Adding loot failed.");
            return await RedisplayAsync();
        }

        TempData["LootHistoryMessage"] =
            $"Loot added: {result.Detail!.ItemName} → {result.Detail.ItemWinner} ({result.Detail.WinningDkpSpent} DKP).";
        return RedirectToAction(nameof(Index));
    }

    // Live and past events for the Add loot pickers.
    //
    // Live is short by nature. Past is NOT — a linkshell accumulates hundreds — so it is the most
    // recent RecentPastEventCount, widened by a search when the officer types one. Without the
    // search the older half of the archive would simply be unreachable, which is the same trap the
    // attendance archive's flat Take() fell into.
    private async Task LoadEventOptionsAsync(LootAddViewModel model, CancellationToken cancellationToken)
    {
        const int RecentPastEventCount = 25;

        model.LiveEvents = await _context.Events
            .AsNoTracking()
            .Where(evt => evt.LinkshellId == model.LinkshellId)
            .OrderByDescending(evt => evt.CommencementStartTime ?? evt.StartTime)
            .Select(evt => new LootEventOption
            {
                Id = evt.Id,
                Name = evt.EventName ?? "Event",
                Detail = evt.EventType
            })
            .ToListAsync(cancellationToken);

        var pastQuery = _context.EventHistories
            .AsNoTracking()
            .Where(history => history.LinkshellId == model.LinkshellId);

        var search = model.EventQuery?.Trim();
        if (!string.IsNullOrWhiteSpace(search))
        {
            var pattern = $"%{search}%";
            pastQuery = pastQuery.Where(history =>
                (history.EventName != null && EF.Functions.ILike(history.EventName, pattern))
                || (history.EventType != null && EF.Functions.ILike(history.EventType, pattern)));
        }

        model.PastEvents = await pastQuery
            .OrderByDescending(history => history.StartTime ?? history.TimeStamp)
            .Take(RecentPastEventCount)
            .Select(history => new LootEventOption
            {
                Id = history.Id,
                Name = history.EventName ?? "Event",
                Detail = history.EventType
            })
            .ToListAsync(cancellationToken);
    }

    // GET /LootHistory — paginated combined ToD + Event loot for the user's
    // primary linkshell. Edit buttons render only when the caller has the
    // CanAddLoot role flag on their membership.
    public async Task<IActionResult> Index(
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

        var entries = await LoadEntriesAsync(linkshellId.Value, user.TimeZone);
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

    // Loot is no longer SPLIT by where it came from — every new row is an EventLootDetail filed
    // against a live event, a past event, or nothing — so the All/ToDs/Events filter went with it.
    //
    // ToD rows are still READ. The addon and the old Log ToD form wrote real ones, people paid real
    // DKP for them, and hiding them would look exactly like losing loot.
    private async Task<List<LootHistoryEntryViewModel>> LoadEntriesAsync(int linkshellId, string? userTimeZone)
    {
        var todRows = await _context.TodLootDetails
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
            .ToListAsync();

        var eventRows = await _context.EventLootDetails
            .AsNoTracking()
            // LinkshellId leads: a "No event" row has neither parent to reach a linkshell through,
            // and this is the only predicate that finds one. The two parent tests stay for rows
            // written before that column existed and never backfilled.
            .Where(detail =>
                detail.LinkshellId == linkshellId
                || (detail.Event != null && detail.Event.LinkshellId == linkshellId)
                || (detail.EventHistory != null && detail.EventHistory.LinkshellId == linkshellId))
            .Select(detail => new LootHistoryEntryViewModel
            {
                LootDetailId = detail.Id,
                Source = "Event",
                ParentId = detail.EventHistoryId ?? detail.EventId ?? 0,
                Context = detail.EventHistory != null
                    ? detail.EventHistory.EventName
                    : (detail.Event != null ? detail.Event.EventName : null),
                // A "No event" row has no event dates to borrow, so it falls back to when the DKP
                // actually moved — which for hand-entered loot is the moment it happened.
                OccurredAt = detail.EventHistory != null
                    ? detail.EventHistory.EndTime
                    : (detail.Event != null ? (detail.Event.EndTime ?? detail.Event.StartTime) : detail.DkpDebitedAt),
                ItemName = detail.ItemName,
                ItemWinner = detail.ItemWinner,
                WinningDkpSpent = detail.WinningDkpSpent,
                ActualDeductedDkp = detail.ActualDeductedDkp,
                LastEditReason = detail.LastEditReason,
                EditedAt = detail.EditedAt,
                EditedByCharacterName = detail.EditedByCharacterName
            })
            .ToListAsync();

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
