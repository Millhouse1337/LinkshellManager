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
    private readonly SheetSyncQueue _sheetSync;

    public DkpAdjustmentController(ApplicationDbContext context, UserManager<AppUser> userManager, SheetSyncQueue sheetSync)
    {
        _context = context;
        _userManager = userManager;
        _sheetSync = sheetSync;
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

        membership.LinkshellDkp = (membership.LinkshellDkp ?? 0) + input.Amount;

        _context.DkpLedgerEntries.Add(new DkpLedgerEntry
        {
            AppUserId = membership.AppUserId,
            LinkshellId = membership.LinkshellId,
            EntryType = "Adjustment",
            Amount = input.Amount,
            Sequence = 1,
            OccurredAt = DateTime.UtcNow,
            CharacterName = membership.CharacterName,
            Details = input.Reason.Trim()
        });

        await _context.SaveChangesAsync();
        await _sheetSync.EnqueueAsync(membership.LinkshellId);
        TempData["DkpAdjustmentSuccess"] = $"Adjusted {membership.CharacterName}'s DKP by {input.Amount:+0.##;-0.##;0}.";
        return RedirectToAction(nameof(Index), new { linkshellId = input.LinkshellId });
    }

    private async Task<bool> CanManageAsync(string appUserId, int linkshellId)
    {
        var membership = await _context.AppUserLinkshells
            .FirstOrDefaultAsync(ul => ul.AppUserId == appUserId && ul.LinkshellId == linkshellId);
        return membership is not null
               && !string.IsNullOrWhiteSpace(membership.Rank)
               && (membership.Rank.Equals("Leader", StringComparison.OrdinalIgnoreCase)
                   || membership.Rank.Equals("Officer", StringComparison.OrdinalIgnoreCase));
    }

    private async Task<List<Linkshell>> GetManageableLinkshellsAsync(string appUserId)
    {
        return await _context.AppUserLinkshells
            .Where(ul => ul.AppUserId == appUserId
                         && (ul.Rank == "Leader" || ul.Rank == "Officer"))
            .Select(ul => ul.Linkshell!)
            .Where(l => l != null)
            .OrderBy(l => l.LinkshellName)
            .ToListAsync();
    }
}
