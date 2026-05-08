using LinkshellManagerDiscordApp.Data;
using LinkshellManagerDiscordApp.Models;
using LinkshellManagerDiscordApp.ViewModels;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace LinkshellManagerDiscordApp.Controllers;

[Authorize]
public class RuleController : Controller
{
    private readonly ApplicationDbContext _context;
    private readonly UserManager<AppUser> _userManager;

    public RuleController(ApplicationDbContext context, UserManager<AppUser> userManager)
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

        var rules = new List<RuleViewModel>();
        if (linkshellId.HasValue)
        {
            rules = await _context.Rules
                .Where(r => r.LinkshellId == linkshellId.Value)
                .OrderByDescending(r => r.CreatedAt)
                .Select(r => new RuleViewModel
                {
                    Id = r.Id,
                    LinkshellId = r.LinkshellId,
                    LinkshellName = r.LinkshellName,
                    RuleTitle = r.RuleTitle,
                    RuleDetails = r.RuleDetails,
                    CreatedByCharacterName = r.CreatedByCharacterName,
                    CreatedAt = r.CreatedAt,
                    CanManage = canManage
                })
                .ToListAsync();
        }

        ViewBag.CanManage = canManage;
        ViewBag.LinkshellName = user.PrimaryLinkshellName;
        return View(rules);
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

        var viewModel = new RuleViewModel
        {
            Linkshells = manageableLinkshells,
            LinkshellId = defaultLinkshellId,
            LinkshellName = manageableLinkshells.First(l => l.Id == defaultLinkshellId).LinkshellName
        };

        return View(viewModel);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(RuleViewModel model)
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

        var rule = new Rule
        {
            LinkshellId = model.LinkshellId,
            LinkshellName = selectedLinkshell.LinkshellName,
            RuleTitle = model.RuleTitle.Trim(),
            RuleDetails = model.RuleDetails.Trim(),
            CreatedByAppUserId = user.Id,
            CreatedByCharacterName = membership?.CharacterName ?? user.CharacterName,
            CreatedAt = DateTime.UtcNow
        };
        _context.Rules.Add(rule);
        await _context.SaveChangesAsync();
        return RedirectToAction(nameof(Index));
    }

    public async Task<IActionResult> Edit(int id)
    {
        var user = await _userManager.GetUserAsync(User);
        if (user is null) return Challenge();

        var rule = await _context.Rules.FirstOrDefaultAsync(r => r.Id == id);
        if (rule is null) return NotFound();

        if (!await CanManageAsync(user.Id, rule.LinkshellId)) return Forbid();

        var linkshells = await GetManageableLinkshellsAsync(user.Id);

        return View(new RuleViewModel
        {
            Id = rule.Id,
            Linkshells = linkshells,
            LinkshellId = rule.LinkshellId,
            LinkshellName = rule.LinkshellName,
            RuleTitle = rule.RuleTitle,
            RuleDetails = rule.RuleDetails,
            CreatedByCharacterName = rule.CreatedByCharacterName,
            CreatedAt = rule.CreatedAt,
            CanManage = true
        });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Edit(int id, RuleViewModel model)
    {
        var user = await _userManager.GetUserAsync(User);
        if (user is null) return Challenge();

        var rule = await _context.Rules.FirstOrDefaultAsync(r => r.Id == id);
        if (rule is null) return NotFound();

        if (!await CanManageAsync(user.Id, rule.LinkshellId)) return Forbid();

        if (!ModelState.IsValid)
        {
            model.Id = rule.Id;
            model.Linkshells = await GetManageableLinkshellsAsync(user.Id);
            model.LinkshellId = rule.LinkshellId;
            model.LinkshellName = rule.LinkshellName;
            return View(model);
        }

        rule.RuleTitle = model.RuleTitle.Trim();
        rule.RuleDetails = model.RuleDetails.Trim();
        await _context.SaveChangesAsync();
        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Delete(int id)
    {
        var user = await _userManager.GetUserAsync(User);
        if (user is null) return Challenge();

        var rule = await _context.Rules.FirstOrDefaultAsync(r => r.Id == id);
        if (rule is null) return NotFound();

        if (!await CanManageAsync(user.Id, rule.LinkshellId)) return Forbid();

        _context.Rules.Remove(rule);
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
