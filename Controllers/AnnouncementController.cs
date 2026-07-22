using LinkshellManagerDiscordApp.Data;
using LinkshellManagerDiscordApp.Models;
using LinkshellManagerDiscordApp.ViewModels;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace LinkshellManagerDiscordApp.Controllers;

[Authorize]
public class AnnouncementController : Controller
{
    private readonly ApplicationDbContext _context;
    private readonly UserManager<AppUser> _userManager;

    public AnnouncementController(ApplicationDbContext context, UserManager<AppUser> userManager)
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

        var announcements = new List<AnnouncementViewModel>();
        if (linkshellId.HasValue)
        {
            announcements = await _context.Announcements
                .Where(a => a.LinkshellId == linkshellId.Value)
                .OrderByDescending(a => a.CreatedAt)
                .Select(a => new AnnouncementViewModel
                {
                    Id = a.Id,
                    LinkshellId = a.LinkshellId,
                    LinkshellName = a.LinkshellName,
                    AnnouncementTitle = a.AnnouncementTitle,
                    AnnouncementDetails = a.AnnouncementDetails,
                    Category = a.Category,
                    CreatedByCharacterName = a.CreatedByCharacterName,
                    CreatedAt = a.CreatedAt,
                    CanManage = canManage
                })
                .ToListAsync();
        }

        ViewBag.CanManage = canManage;
        ViewBag.LinkshellName = user.PrimaryLinkshellName;
        return View(announcements);
    }

    public async Task<IActionResult> Create()
    {
        var user = await _userManager.GetUserAsync(User);
        if (user is null) return Challenge();

        var manageableLinkshells = await GetManageableLinkshellsAsync(user.Id);
        if (manageableLinkshells.Count == 0) return Forbid();

        var defaultLinkshellId = manageableLinkshells.Any(l => l.Id == user.PrimaryLinkshellId)
            ? user.PrimaryLinkshellId ?? manageableLinkshells[0].Id
            : manageableLinkshells[0].Id;

        var viewModel = new AnnouncementViewModel
        {
            Linkshells = manageableLinkshells,
            LinkshellId = defaultLinkshellId,
            LinkshellName = manageableLinkshells.First(l => l.Id == defaultLinkshellId).LinkshellName
        };

        return View(viewModel);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(AnnouncementViewModel model)
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

        var announcement = new Announcement
        {
            LinkshellId = model.LinkshellId,
            LinkshellName = selectedLinkshell.LinkshellName,
            AnnouncementTitle = model.AnnouncementTitle.Trim(),
            AnnouncementDetails = model.AnnouncementDetails.Trim(),
            Category = string.IsNullOrWhiteSpace(model.Category) ? null : model.Category.Trim(),
            CreatedByAppUserId = user.Id,
            CreatedByCharacterName = membership?.CharacterName ?? user.CharacterName,
            CreatedAt = DateTime.UtcNow
        };
        _context.Announcements.Add(announcement);
        await _context.SaveChangesAsync();
        return RedirectToAction(nameof(Index));
    }

    public async Task<IActionResult> Edit(int id)
    {
        var user = await _userManager.GetUserAsync(User);
        if (user is null) return Challenge();

        var announcement = await _context.Announcements.FirstOrDefaultAsync(a => a.Id == id);
        if (announcement is null) return NotFound();

        if (!await CanManageAsync(user.Id, announcement.LinkshellId)) return Forbid();

        var linkshells = await GetManageableLinkshellsAsync(user.Id);

        return View(new AnnouncementViewModel
        {
            Id = announcement.Id,
            Linkshells = linkshells,
            LinkshellId = announcement.LinkshellId,
            LinkshellName = announcement.LinkshellName,
            AnnouncementTitle = announcement.AnnouncementTitle,
            AnnouncementDetails = announcement.AnnouncementDetails,
            Category = announcement.Category,
            CreatedByCharacterName = announcement.CreatedByCharacterName,
            CreatedAt = announcement.CreatedAt,
            CanManage = true
        });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Edit(int id, AnnouncementViewModel model)
    {
        var user = await _userManager.GetUserAsync(User);
        if (user is null) return Challenge();

        var announcement = await _context.Announcements.FirstOrDefaultAsync(a => a.Id == id);
        if (announcement is null) return NotFound();

        if (!await CanManageAsync(user.Id, announcement.LinkshellId)) return Forbid();

        if (!ModelState.IsValid)
        {
            model.Id = announcement.Id;
            model.Linkshells = await GetManageableLinkshellsAsync(user.Id);
            model.LinkshellId = announcement.LinkshellId;
            model.LinkshellName = announcement.LinkshellName;
            return View(model);
        }

        announcement.AnnouncementTitle = model.AnnouncementTitle.Trim();
        announcement.AnnouncementDetails = model.AnnouncementDetails.Trim();
        announcement.Category = string.IsNullOrWhiteSpace(model.Category) ? null : model.Category.Trim();
        await _context.SaveChangesAsync();
        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Delete(int id)
    {
        var user = await _userManager.GetUserAsync(User);
        if (user is null) return Challenge();

        var announcement = await _context.Announcements.FirstOrDefaultAsync(a => a.Id == id);
        if (announcement is null) return NotFound();

        if (!await CanManageAsync(user.Id, announcement.LinkshellId)) return Forbid();

        _context.Announcements.Remove(announcement);
        await _context.SaveChangesAsync();
        return RedirectToAction(nameof(Index));
    }

    private async Task<bool> CanManageAsync(string appUserId, int? linkshellId)
    {
        if (!linkshellId.HasValue) return false;
        var membership = await _context.AppUserLinkshells
            .FirstOrDefaultAsync(ul => ul.AppUserId == appUserId && ul.LinkshellId == linkshellId.Value);
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
