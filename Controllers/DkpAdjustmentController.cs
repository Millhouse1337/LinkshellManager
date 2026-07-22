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
public class DkpAdjustmentController : Controller
{
    private readonly ApplicationDbContext _context;
    private readonly UserManager<AppUser> _userManager;
    private readonly WindowEventDkpLedgerService _windowEventDkpLedger;
    private readonly DkpLedgerWriter _dkpLedger;
    private readonly DkpPoolResolver _dkpPools;

    public DkpAdjustmentController(
        ApplicationDbContext context,
        UserManager<AppUser> userManager,
        WindowEventDkpLedgerService windowEventDkpLedger,
        DkpLedgerWriter dkpLedger,
        DkpPoolResolver dkpPools)
    {
        _context = context;
        _userManager = userManager;
        _windowEventDkpLedger = windowEventDkpLedger;
        _dkpLedger = dkpLedger;
        _dkpPools = dkpPools;
    }

    public async Task<IActionResult> Index(int? linkshellId)
    {
        var user = await _userManager.GetUserAsync(User);
        if (user is null) return Challenge();

        var manageableLinkshells = await GetManageableLinkshellsAsync(user.Id);

        var viewModel = new DkpAdjustmentViewModel
        {
            CanManage = manageableLinkshells.Count > 0,
            Linkshells = manageableLinkshells
                .Select(link => new DkpAdjustmentLinkshellOption { Id = link.Id, Name = link.LinkshellName ?? "Unknown linkshell" })
                .ToList()
        };

        if (viewModel.Linkshells.Count == 0)
        {
            return View(viewModel);
        }

        var selectedLinkshellId = linkshellId
            ?? (manageableLinkshells.Any(l => l.Id == user.PrimaryLinkshellId) ? user.PrimaryLinkshellId : null)
            ?? viewModel.Linkshells.First().Id;

        if (viewModel.Linkshells.All(l => l.Id != selectedLinkshellId))
        {
            selectedLinkshellId = viewModel.Linkshells.First().Id;
        }

        viewModel.SelectedLinkshellId = selectedLinkshellId;
        viewModel.SelectedLinkshellName = viewModel.Linkshells.First(l => l.Id == selectedLinkshellId).Name;
        viewModel.SelectedLinkshellType = manageableLinkshells.First(l => l.Id == selectedLinkshellId).LinkshellType;

        await _windowEventDkpLedger.EnsurePostedWindowEventLedgerEntriesForLinkshellAsync(selectedLinkshellId, HttpContext.RequestAborted);

        var members = await _context.AppUserLinkshells
            .Where(link => link.LinkshellId == selectedLinkshellId && link.AppUserId != null)
            .OrderBy(link => link.CharacterName)
            .ToListAsync();

        viewModel.Members = members
            .Where(link => !string.IsNullOrWhiteSpace(link.AppUserId))
            .Select(link => new DkpAdjustmentMemberRow
            {
                MembershipId = link.Id,
                AppUserId = link.AppUserId!,
                CharacterName = link.CharacterName ?? "Unknown member",
                Rank = link.Rank,
                CurrentBalance = link.LinkshellDkp ?? 0
            })
            .ToList();

        var poolMap = await _dkpPools.GetMapAsync(selectedLinkshellId, HttpContext.RequestAborted);
        viewModel.DefaultPoolId = poolMap.DefaultPoolId;
        viewModel.Pools = poolMap.Pools
            .Select(pool => new DkpAdjustmentPoolOption { Id = pool.Id, Name = pool.Name })
            .ToList();

        return View(viewModel);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Adjust(DkpAdjustmentInput input)
    {
        var user = await _userManager.GetUserAsync(User);
        if (user is null) return Challenge();

        if (!await CanManageAsync(user.Id, input.LinkshellId)) return Forbid();

        if (!ModelState.IsValid)
        {
            TempData["DkpAdjustmentError"] = "Enter an amount and a reason for the adjustment.";
            return RedirectToAction(nameof(Index), new { linkshellId = input.LinkshellId });
        }

        var membership = await _context.AppUserLinkshells
            .FirstOrDefaultAsync(link => link.Id == input.MembershipId && link.LinkshellId == input.LinkshellId);
        if (membership is null) return NotFound();

        // An adjustment has no event type to follow, so the officer picks the pool. PINNED —
        // remapping event types must never move a manual correction. Defaults to the default pool,
        // which is where everything lives for a linkshell that hasn't partitioned its event types.
        var map = await _dkpPools.GetMapAsync(input.LinkshellId, HttpContext.RequestAborted);
        var poolId = input.DkpPoolId is int chosen && map.Pools.Any(pool => pool.Id == chosen)
            ? chosen
            : map.DefaultPoolId;

        await _dkpLedger.AppendAsync(
            membership,
            "Adjustment",
            input.Amount,
            DateTime.UtcNow,
            DkpPoolRef.Pinned(poolId),
            new DkpEntryContext(
                CharacterName: membership.CharacterName,
                Details: input.Reason.Trim()),
            HttpContext.RequestAborted);

        await _context.SaveChangesAsync();
        TempData["DkpAdjustmentSuccess"] = $"Adjusted {membership.CharacterName}'s DKP by {input.Amount:+0.##;-0.##;0}.";
        return RedirectToAction(nameof(Index), new { linkshellId = input.LinkshellId });
    }

    private async Task<bool> CanManageAsync(string appUserId, int linkshellId)
    {
        var membership = await _context.AppUserLinkshells
            .FirstOrDefaultAsync(ul => ul.AppUserId == appUserId && ul.LinkshellId == linkshellId);
        return membership is not null && LinkshellRanks.IsLeaderOrOfficer(membership.Rank);
    }

    private async Task<List<Linkshell>> GetManageableLinkshellsAsync(string appUserId)
    {
        return await _context.AppUserLinkshells
            .Where(ul => ul.AppUserId == appUserId
                         && (ul.Rank == LinkshellRanks.Leader || ul.Rank == LinkshellRanks.Officer))
            .Select(ul => ul.Linkshell!)
            .Where(l => l != null)
            .OrderBy(l => l.LinkshellName)
            .ToListAsync();
    }
}
