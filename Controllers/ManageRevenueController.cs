using LinkshellManagerDiscordApp.Data;
using LinkshellManagerDiscordApp.Models;
using LinkshellManagerDiscordApp.ViewModels;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace LinkshellManagerDiscordApp.Controllers;

[Authorize]
public class ManageRevenueController : Controller
{
    private readonly ApplicationDbContext _context;
    private readonly UserManager<AppUser> _userManager;

    public ManageRevenueController(ApplicationDbContext context, UserManager<AppUser> userManager)
    {
        _context = context;
        _userManager = userManager;
    }

    public async Task<IActionResult> Index()
    {
        var user = await _userManager.GetUserAsync(User);
        if (user is null) return Challenge();

        var linkshellId = user.PrimaryLinkshellId;
        var canManage = await CanManageAsync(user.Id, linkshellId);

        var entries = new List<ManageRevenueViewModel>();
        long totalValue = 0;
        if (linkshellId.HasValue)
        {
            entries = await _context.RevenueEntries
                .Where(r => r.LinkshellId == linkshellId.Value)
                .OrderByDescending(r => r.OccurredAt)
                .Select(r => new ManageRevenueViewModel
                {
                    Id = r.Id,
                    LinkshellId = r.LinkshellId,
                    LinkshellName = r.LinkshellName,
                    EntryType = r.EntryType,
                    Category = r.Category,
                    Value = r.Value,
                    Details = r.Details,
                    OccurredAt = r.OccurredAt,
                    CreatedByCharacterName = r.CreatedByCharacterName,
                    CreatedAt = r.CreatedAt,
                    CanManage = canManage
                })
                .ToListAsync();

            totalValue = entries.Sum(e => e.Value);
        }

        ViewBag.CanManage = canManage;
        ViewBag.LinkshellName = user.PrimaryLinkshellName;
        ViewBag.TotalValue = totalValue;
        return View(entries);
    }

    public async Task<IActionResult> AddIncome()
    {
        var user = await _userManager.GetUserAsync(User);
        if (user is null) return Challenge();

        var manageableLinkshells = await GetManageableLinkshellsAsync(user.Id);
        if (manageableLinkshells.Count == 0) return Forbid();

        var defaultLinkshellId = manageableLinkshells.Any(l => l.Id == user.PrimaryLinkshellId)
            ? user.PrimaryLinkshellId ?? manageableLinkshells[0].Id
            : manageableLinkshells[0].Id;

        return View(new ManageRevenueViewModel
        {
            Linkshells = manageableLinkshells,
            LinkshellId = defaultLinkshellId,
            LinkshellName = manageableLinkshells.First(l => l.Id == defaultLinkshellId).LinkshellName,
            OccurredAt = DateTime.UtcNow
        });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> AddIncome(ManageRevenueViewModel model)
    {
        var user = await _userManager.GetUserAsync(User);
        if (user is null) return Challenge();

        var manageableLinkshells = await GetManageableLinkshellsAsync(user.Id);
        var selectedLinkshell = manageableLinkshells.FirstOrDefault(l => l.Id == user.PrimaryLinkshellId)
            ?? manageableLinkshells.FirstOrDefault();
        if (selectedLinkshell is null)
        {
            return Forbid();
        }

        model.LinkshellId = selectedLinkshell.Id;
        model.LinkshellName = selectedLinkshell.LinkshellName;
        ModelState.Remove(nameof(model.LinkshellId));

        if (!ModelState.IsValid)
        {
            model.Linkshells = manageableLinkshells;
            return View(model);
        }

        var membership = await _context.AppUserLinkshells
            .FirstOrDefaultAsync(ul => ul.AppUserId == user.Id && ul.LinkshellId == model.LinkshellId);

        _context.RevenueEntries.Add(new RevenueEntry
        {
            LinkshellId = model.LinkshellId,
            LinkshellName = selectedLinkshell.LinkshellName,
            EntryType = model.EntryType.Trim(),
            Category = model.Category?.Trim(),
            Value = model.Value,
            Details = model.Details?.Trim(),
            OccurredAt = model.OccurredAt == default ? DateTime.UtcNow : model.OccurredAt,
            CreatedByAppUserId = user.Id,
            CreatedByCharacterName = membership?.CharacterName ?? user.CharacterName,
            CreatedAt = DateTime.UtcNow
        });
        await _context.SaveChangesAsync();
        return RedirectToAction(nameof(Index));
    }

    public async Task<IActionResult> Edit(int id)
    {
        var user = await _userManager.GetUserAsync(User);
        if (user is null) return Challenge();

        var entry = await _context.RevenueEntries.FirstOrDefaultAsync(r => r.Id == id);
        if (entry is null) return NotFound();

        if (!await CanManageAsync(user.Id, entry.LinkshellId)) return Forbid();

        var linkshells = await GetManageableLinkshellsAsync(user.Id);
        return View(new ManageRevenueViewModel
        {
            Id = entry.Id,
            LinkshellId = entry.LinkshellId,
            LinkshellName = entry.LinkshellName,
            EntryType = entry.EntryType,
            Category = entry.Category,
            Value = entry.Value,
            Details = entry.Details,
            OccurredAt = entry.OccurredAt,
            CreatedByCharacterName = entry.CreatedByCharacterName,
            CreatedAt = entry.CreatedAt,
            CanManage = true,
            Linkshells = linkshells
        });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Edit(int id, ManageRevenueViewModel model)
    {
        var user = await _userManager.GetUserAsync(User);
        if (user is null) return Challenge();

        var entry = await _context.RevenueEntries.FirstOrDefaultAsync(r => r.Id == id);
        if (entry is null) return NotFound();

        if (!await CanManageAsync(user.Id, entry.LinkshellId)) return Forbid();

        if (!ModelState.IsValid)
        {
            model.Id = entry.Id;
            model.LinkshellId = entry.LinkshellId;
            model.LinkshellName = entry.LinkshellName;
            model.Linkshells = await GetManageableLinkshellsAsync(user.Id);
            return View(model);
        }

        entry.EntryType = model.EntryType.Trim();
        entry.Category = model.Category?.Trim();
        entry.Value = model.Value;
        entry.Details = model.Details?.Trim();
        entry.OccurredAt = model.OccurredAt == default ? entry.OccurredAt : model.OccurredAt;
        await _context.SaveChangesAsync();
        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Delete(int id)
    {
        var user = await _userManager.GetUserAsync(User);
        if (user is null) return Challenge();

        var entry = await _context.RevenueEntries.FirstOrDefaultAsync(r => r.Id == id);
        if (entry is null) return NotFound();

        if (!await CanManageAsync(user.Id, entry.LinkshellId)) return Forbid();

        _context.RevenueEntries.Remove(entry);
        await _context.SaveChangesAsync();
        return RedirectToAction(nameof(Index));
    }

    private async Task<bool> CanManageAsync(string appUserId, int? linkshellId)
    {
        if (!linkshellId.HasValue) return false;
        var membership = await _context.AppUserLinkshells
            .FirstOrDefaultAsync(ul => ul.AppUserId == appUserId && ul.LinkshellId == linkshellId.Value);
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
