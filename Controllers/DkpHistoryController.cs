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
public class DkpHistoryController : Controller
{
    private readonly ApplicationDbContext _context;
    private readonly UserManager<AppUser> _userManager;
    private readonly TimeZoneConversionService _timeZones;

    private readonly DkpPoolResolver _dkpPools;

    public DkpHistoryController(
        ApplicationDbContext context,
        UserManager<AppUser> userManager,
        TimeZoneConversionService timeZones,
        DkpPoolResolver dkpPools)
    {
        _context = context;
        _userManager = userManager;
        _timeZones = timeZones;
        _dkpPools = dkpPools;
    }

    public async Task<IActionResult> Index(
        string? appUserId,
        int page = 1,
        [FromServices] WindowEventDkpLedgerService? windowEventDkpLedger = null,
        CancellationToken cancellationToken = default)
    {
        var user = await _userManager.GetUserAsync(User);
        if (user is null)
        {
            return Challenge();
        }

        var accessibleMemberships = await _context.AppUserLinkshells
            .Include(link => link.Linkshell)
            .Where(link => link.AppUserId == user.Id)
            .OrderBy(link => link.Linkshell!.LinkshellName)
            .ToListAsync();

        var viewModel = new DkpHistoryViewModel
        {
            Linkshells = accessibleMemberships
                .Select(link => new DkpHistoryLinkshellOptionViewModel
                {
                    Id = link.LinkshellId,
                    Name = link.Linkshell?.LinkshellName ?? "Unknown linkshell"
                })
                .GroupBy(link => link.Id)
                .Select(group => group.First())
                .ToList()
        };

        if (viewModel.Linkshells.Count == 0)
        {
            return View(viewModel);
        }

        var selectedLinkshellId = user.PrimaryLinkshellId
            ?? viewModel.Linkshells.First().Id;

        if (viewModel.Linkshells.All(link => link.Id != selectedLinkshellId))
        {
            selectedLinkshellId = viewModel.Linkshells.First().Id;
        }

        // Mirror the Activity DKP endpoint: materialize any posted window-event
        // credit so DKP history/totals are current before rendering.
        if (windowEventDkpLedger is not null)
        {
            await windowEventDkpLedger.EnsurePostedWindowEventLedgerEntriesForLinkshellAsync(selectedLinkshellId, cancellationToken);
        }

        var linkshellMembers = await _context.AppUserLinkshells
            .Where(link => link.LinkshellId == selectedLinkshellId && link.AppUserId != null)
            .OrderBy(link => link.CharacterName)
            .ToListAsync();

        viewModel.SelectedLinkshellId = selectedLinkshellId;
        viewModel.SelectedLinkshellName = viewModel.Linkshells.First(link => link.Id == selectedLinkshellId).Name;
        // DKP each member has spent on loot in still-live events but not yet committed.
        // Shown as a pending deduction; already removed from biddable power so it can't be
        // double-spent before the event closes.
        var pendingLootByUser = await AuctionDkpService.ComputePendingLiveLootSpendByUserAsync(
            _context, selectedLinkshellId, HttpContext.RequestAborted);
        viewModel.Members = linkshellMembers
            .Where(link => !string.IsNullOrWhiteSpace(link.AppUserId))
            .Select(link => new DkpHistoryMemberOptionViewModel
            {
                AppUserId = link.AppUserId!,
                CharacterName = link.CharacterName ?? "Unknown member",
                CurrentBalance = link.LinkshellDkp ?? 0,
                PendingLootSpend = pendingLootByUser.GetValueOrDefault(link.AppUserId!)
            })
            .ToList();

        if (viewModel.Members.Count == 0)
        {
            return View(viewModel);
        }

        var selectedAppUserId = appUserId;
        if (string.IsNullOrWhiteSpace(selectedAppUserId) || viewModel.Members.All(member => member.AppUserId != selectedAppUserId))
        {
            selectedAppUserId = viewModel.Members.FirstOrDefault(member => member.AppUserId == user.Id)?.AppUserId
                ?? viewModel.Members.First().AppUserId;
        }

        var ledgerEntries = await _context.DkpLedgerEntries
            .Where(entry => entry.LinkshellId == selectedLinkshellId && entry.AppUserId == selectedAppUserId)
            .OrderBy(entry => entry.OccurredAt)
            .ThenBy(entry => entry.Sequence)
            .ToListAsync();

        viewModel.TotalEntryCount = ledgerEntries.Count;
        viewModel.PageSize = DkpHistoryViewModel.DefaultPageSize;
        viewModel.PageNumber = Math.Clamp(page, 1, viewModel.TotalPages);

        // Running balance is computed by walking the ledger in chronological
        // (oldest-first) order so each entry's "after" balance is correct.
        // The user-facing table wants the opposite — newest first — so we
        // reverse the materialized projection before applying pagination.
        var poolMap = await _dkpPools.GetMapAsync(selectedLinkshellId, HttpContext.RequestAborted);

        var runningBalance = 0d;
        viewModel.SelectedAppUserId = selectedAppUserId;
        viewModel.SelectedMemberName = viewModel.Members.First(member => member.AppUserId == selectedAppUserId).CharacterName;
        viewModel.CurrentBalance = viewModel.Members.First(member => member.AppUserId == selectedAppUserId).CurrentBalance;
        viewModel.SelectedPendingLootSpend = pendingLootByUser.GetValueOrDefault(selectedAppUserId);
        var projected = ledgerEntries
            .Select(entry =>
            {
                runningBalance += entry.Amount;
                return new DkpHistoryEntryViewModel
                {
                    Id = entry.Id,
                    EntryType = entry.EntryType,
                    Amount = entry.Amount,
                    RunningBalance = runningBalance,
                    OccurredAt = ConvertUtcToUserTimeZone(entry.OccurredAt, user.TimeZone),
                    EventName = entry.EventName,
                    EventType = entry.EventType,
                    EventLocation = entry.EventLocation,
                    EventStartTime = ConvertUtcToUserTimeZone(entry.EventStartTime, user.TimeZone),
                    EventEndTime = ConvertUtcToUserTimeZone(entry.EventEndTime, user.TimeZone),
                    ItemName = entry.ItemName,
                    Details = entry.Details,
                    EditReason = entry.EditReason,
                    // A null DkpPoolId means the default pool, so name it rather than leaving the
                    // tag blank on every pre-pools row.
                    DkpPoolName = poolMap.HasMultiplePools
                        ? poolMap.NameFor(entry.DkpPoolId ?? poolMap.DefaultPoolId)
                        : null,
                };
            })
            .ToList();
        projected.Reverse();

        // Render the whole ledger; the DKP History view paginates (10 per page)
        // and filters it client-side so search is instant across every entry.
        viewModel.Entries = projected;

        // The earned-by-event-type breakdown and the per-pool balances both fall out of the ledger
        // rows already in memory — no extra queries, and nothing to N+1 across the roster.
        var membership = await _context.AppUserLinkshells
            .AsNoTracking()
            .FirstOrDefaultAsync(link => link.LinkshellId == selectedLinkshellId && link.AppUserId == selectedAppUserId);

        // Lifetime EARNED per event type. The seed watermark and the reconciliation entry types are
        // applied exactly as DkpSheetService.ComputeTotals does, so an imported member's per-type
        // sum reconciles with their Total instead of double-counting their pre-import history.
        var reconciliationTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "TemplateImport", "SheetImport" };
        var seedWatermark = membership?.DkpSeedLedgerId ?? 0;
        viewModel.EarnedByEventType = ledgerEntries
            .Where(entry => entry.Amount > 0
                && entry.Id > seedWatermark
                && !reconciliationTypes.Contains(entry.EntryType))
            .GroupBy(entry => string.IsNullOrWhiteSpace(entry.EventType) ? "Other" : entry.EventType!,
                StringComparer.OrdinalIgnoreCase)
            .Select(group => new DkpEarnedByEventTypeViewModel
            {
                EventType = group.Key,
                Earned = group.Sum(entry => entry.Amount),
                PoolName = poolMap.HasMultiplePools ? poolMap.NameFor(poolMap.Resolve(group.Key)) : null,
            })
            .OrderByDescending(item => item.Earned)
            .ToList();

        if (poolMap.HasMultiplePools && membership is not null)
        {
            var byPool = DkpPoolBalanceService.Project(
                membership.LinkshellDkp ?? 0,
                ledgerEntries.Select(entry => new LedgerPoolRow(entry.Id, entry.DkpPoolId, entry.Amount)),
                membership.DkpPoolLedgerFromId,
                poolMap.DefaultPoolId,
                poolMap.Pools.Select(pool => pool.Id).ToList());

            viewModel.PoolBalances = poolMap.Pools
                .Select(pool => new DkpPoolBalanceViewModel
                {
                    PoolId = pool.Id,
                    Name = pool.Name,
                    Accent = Models.DkpPoolAccents.Resolve(pool.Accent),
                    Balance = byPool.GetValueOrDefault(pool.Id),
                })
                .ToList();
        }

        return View(viewModel);
    }

    private DateTime? ConvertUtcToUserTimeZone(DateTime? utcDateTime, string? timeZoneId)
        => _timeZones.ToUserTime(utcDateTime, timeZoneId);
}
