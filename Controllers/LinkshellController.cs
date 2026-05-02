using LinkshellManagerDiscordApp.Data;
using LinkshellManagerDiscordApp.Models;
using LinkshellManagerDiscordApp.ViewModels;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace LinkshellManagerDiscordApp.Controllers;

[Authorize]
public class LinkshellController : Controller
{
    private readonly ApplicationDbContext _context;
    private readonly UserManager<AppUser> _userManager;

    public LinkshellController(ApplicationDbContext context, UserManager<AppUser> userManager)
    {
        _context = context;
        _userManager = userManager;
    }
    public async Task<IActionResult> Index()
    {
        var user = await _userManager.GetUserAsync(User);
        if (user is null)
        {
            return Challenge();
        }

        var linkshells = await _context.AppUserLinkshells
            .Where(link => link.AppUserId == user.Id)
            .Select(link => link.Linkshell!)
            .OrderBy(linkshell => linkshell.LinkshellName)
            .ToListAsync();

        foreach (var linkshell in linkshells)
        {
            linkshell.TotalMembers = await _context.AppUserLinkshells.CountAsync(link => link.LinkshellId == linkshell.Id);
        }

        return View(linkshells);
    }
    public IActionResult Create() => View(new LinkshellViewModel());

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(LinkshellViewModel model)
    {
        if (!ModelState.IsValid)
        {
            return View(model);
        }

        var user = await _userManager.GetUserAsync(User);
        if (user is null)
        {
            return Challenge();
        }

        var linkshell = new Linkshell
        {
            AppUserId = user.Id,
            LinkshellName = model.LinkshellName,
            Details = model.Details,
            Status = "Active"
        };

        _context.Linkshells.Add(linkshell);
        await _context.SaveChangesAsync();

        _context.AppUserLinkshells.Add(new AppUserLinkshell
        {
            AppUserId = user.Id,
            LinkshellId = linkshell.Id,
            CharacterName = user.CharacterName,
            Rank = "Leader",
            Status = "Active",
            LinkshellDkp = 0,
            DateJoined = DateTime.UtcNow
        });

        user.PrimaryLinkshellId ??= linkshell.Id;
        user.PrimaryLinkshellName ??= linkshell.LinkshellName;

        await _context.SaveChangesAsync();
        await _userManager.UpdateAsync(user);

        return RedirectToAction(nameof(Index));
    }
    public async Task<IActionResult> Details(int id)
    {
        var user = await _userManager.GetUserAsync(User);
        if (user is null)
        {
            return Challenge();
        }

        var membership = await GetMembershipAsync(user.Id, id);
        if (membership is null)
        {
            return Forbid();
        }

        var linkshell = await _context.Linkshells
            .Include(ls => ls.AppUserLinkshells)
            .ThenInclude(link => link.AppUser)
            .FirstOrDefaultAsync(ls => ls.Id == id);

        ViewBag.CanEditLinkshell = CanManageLinkshell(membership);
        ViewBag.CanDeleteLinkshell = IsLeader(membership);

        return linkshell is null ? NotFound() : View(linkshell);
    }
    public async Task<IActionResult> Edit(int id)
    {
        var user = await _userManager.GetUserAsync(User);
        if (user is null)
        {
            return Challenge();
        }

        var membership = await GetMembershipAsync(user.Id, id);
        if (!CanManageLinkshell(membership))
        {
            return Forbid();
        }

        var linkshell = await _context.Linkshells.FindAsync(id);
        if (linkshell is null)
        {
            return NotFound();
        }

        return View(new LinkshellViewModel
        {
            LinkshellName = linkshell.LinkshellName ?? string.Empty,
            Details = linkshell.Details
        });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Edit(int id, LinkshellViewModel model)
    {
        if (!ModelState.IsValid)
        {
            return View(model);
        }

        var user = await _userManager.GetUserAsync(User);
        if (user is null)
        {
            return Challenge();
        }

        var membership = await GetMembershipAsync(user.Id, id);
        if (!CanManageLinkshell(membership))
        {
            return Forbid();
        }

        var linkshell = await _context.Linkshells.FindAsync(id);
        if (linkshell is null)
        {
            return NotFound();
        }

        linkshell.LinkshellName = model.LinkshellName;
        linkshell.Details = model.Details;
        await _context.SaveChangesAsync();

        return RedirectToAction(nameof(Index));
    }
    public async Task<IActionResult> Delete(int id)
    {
        var user = await _userManager.GetUserAsync(User);
        if (user is null)
        {
            return Challenge();
        }

        var membership = await GetMembershipAsync(user.Id, id);
        if (!IsLeader(membership))
        {
            return Forbid();
        }

        var linkshell = await _context.Linkshells
            .Include(ls => ls.AppUserLinkshells)
            .Include(ls => ls.Events)
            .FirstOrDefaultAsync(ls => ls.Id == id);

        if (linkshell is null)
        {
            return NotFound();
        }

        var memberCount = linkshell.AppUserLinkshells.Count;
        var activeEventCount = linkshell.Events.Count;
        ViewBag.DeleteBlockedReason = GetDeleteBlockedReason(memberCount, activeEventCount);

        return View(linkshell);
    }

    [HttpPost, ActionName("Delete")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteConfirmed(int id)
    {
        var user = await _userManager.GetUserAsync(User);
        if (user is null)
        {
            return Challenge();
        }

        var membership = await GetMembershipAsync(user.Id, id);
        if (!IsLeader(membership))
        {
            return Forbid();
        }

        var linkshell = await _context.Linkshells
            .Include(ls => ls.AppUserLinkshells)
            .Include(ls => ls.Events)
            .ThenInclude(evt => evt.Jobs)
            .Include(ls => ls.Events)
            .ThenInclude(evt => evt.AppUserEvents)
            .Include(ls => ls.Events)
            .ThenInclude(evt => evt.EventLootDetails)
            .Include(ls => ls.EventHistories)
            .ThenInclude(history => history.AppUserEventHistories)
            .FirstOrDefaultAsync(ls => ls.Id == id);

        if (linkshell is null)
        {
            return NotFound();
        }

        var memberCount = linkshell.AppUserLinkshells.Count;
        var activeEventCount = linkshell.Events.Count;
        var deleteBlockedReason = GetDeleteBlockedReason(memberCount, activeEventCount);
        if (!string.IsNullOrWhiteSpace(deleteBlockedReason))
        {
            ViewBag.DeleteBlockedReason = deleteBlockedReason;
            return View("Delete", linkshell);
        }

        var impactedUserIds = linkshell.AppUserLinkshells
            .Where(link => !string.IsNullOrWhiteSpace(link.AppUserId))
            .Select(link => link.AppUserId!)
            .Distinct()
            .ToList();

        if (impactedUserIds.Count > 0)
        {
            var impactedUsers = await _context.Users
                .Where(appUser => impactedUserIds.Contains(appUser.Id))
                .ToListAsync();

            foreach (var impactedUser in impactedUsers.Where(appUser => appUser.PrimaryLinkshellId == id))
            {
                var fallback = await _context.AppUserLinkshells
                    .Include(link => link.Linkshell)
                    .Where(link => link.AppUserId == impactedUser.Id && link.LinkshellId != id)
                    .OrderBy(link => link.Linkshell!.LinkshellName)
                    .FirstOrDefaultAsync();

                impactedUser.PrimaryLinkshellId = fallback?.LinkshellId;
                impactedUser.PrimaryLinkshellName = fallback?.Linkshell?.LinkshellName;
            }
        }

        var pendingInvites = await _context.Invites
            .Where(invite => invite.LinkshellId == id)
            .ToListAsync();

        if (pendingInvites.Count > 0)
        {
            _context.Invites.RemoveRange(pendingInvites);
        }

        _context.AppUserLinkshells.RemoveRange(linkshell.AppUserLinkshells);
        _context.Jobs.RemoveRange(linkshell.Events.SelectMany(evt => evt.Jobs));
        _context.AppUserEvents.RemoveRange(linkshell.Events.SelectMany(evt => evt.AppUserEvents));
        _context.EventLootDetails.RemoveRange(linkshell.Events.SelectMany(evt => evt.EventLootDetails));
        _context.Events.RemoveRange(linkshell.Events);
        _context.AppUserEventHistories.RemoveRange(linkshell.EventHistories.SelectMany(history => history.AppUserEventHistories));
        _context.EventHistories.RemoveRange(linkshell.EventHistories);
        _context.Linkshells.Remove(linkshell);
        await _context.SaveChangesAsync();

        return RedirectToAction(nameof(Index));
    }

    // Mirrors the Discord Activity's "Customize Linkshell" card on its Configurations
    // tab: loot structure, DKP rounding, and the per-tab feature toggles. Source of
    // truth fields live on the Linkshell entity (LootStructure, DkpRoundingIncrement,
    // and the Enable* booleans).
    [HttpGet]
    public async Task<IActionResult> Customize(int? id)
    {
        var user = await _userManager.GetUserAsync(User);
        if (user is null)
        {
            return Challenge();
        }

        var manageableLinkshells = await _context.AppUserLinkshells
            .Where(link => link.AppUserId == user.Id
                        && (link.Rank == "Leader" || link.Rank == "Officer"))
            .Include(link => link.Linkshell)
            .OrderBy(link => link.Linkshell!.LinkshellName)
            .Select(link => link.Linkshell!)
            .ToListAsync();

        if (manageableLinkshells.Count == 0)
        {
            return View(new LinkshellCustomizeViewModel
            {
                ManageableLinkshells = new List<Linkshell>()
            });
        }

        var target = id.HasValue
            ? manageableLinkshells.FirstOrDefault(link => link.Id == id.Value)
            : manageableLinkshells.First();
        if (target is null)
        {
            return Forbid();
        }

        return View(BuildCustomizeViewModel(target, manageableLinkshells));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Customize(LinkshellCustomizeViewModel model)
    {
        var user = await _userManager.GetUserAsync(User);
        if (user is null)
        {
            return Challenge();
        }

        var membership = await GetMembershipAsync(user.Id, model.LinkshellId);
        if (!CanManageLinkshell(membership))
        {
            return Forbid();
        }

        var linkshell = await _context.Linkshells.FindAsync(model.LinkshellId);
        if (linkshell is null)
        {
            return NotFound();
        }

        // Validate enums against the same vocabulary the Activity uses; bad values
        // would otherwise propagate into a string column nothing reads.
        var allowedLoot = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "Dkp", "LootCouncil", "Hybrid" };
        var allowedRounding = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "Quarter", "Half" };
        if (!allowedLoot.Contains(model.LootStructure ?? string.Empty))
        {
            ModelState.AddModelError(nameof(model.LootStructure), "Invalid loot structure.");
        }
        if (!allowedRounding.Contains(model.DkpRoundingIncrement ?? string.Empty))
        {
            ModelState.AddModelError(nameof(model.DkpRoundingIncrement), "Invalid DKP rounding increment.");
        }

        if (!ModelState.IsValid)
        {
            var manageable = await _context.AppUserLinkshells
                .Where(link => link.AppUserId == user.Id
                            && (link.Rank == "Leader" || link.Rank == "Officer"))
                .Include(link => link.Linkshell)
                .OrderBy(link => link.Linkshell!.LinkshellName)
                .Select(link => link.Linkshell!)
                .ToListAsync();
            model.ManageableLinkshells = manageable;
            model.LinkshellName = linkshell.LinkshellName;
            return View(model);
        }

        linkshell.LootStructure = model.LootStructure!;
        linkshell.DkpRoundingIncrement = model.DkpRoundingIncrement!;
        linkshell.EnableEndgame  = model.EnableEndgame;
        linkshell.EnableHnmSection = model.EnableHnmSection;
        linkshell.EnableMissions = model.EnableMissions;
        linkshell.EnableAuctions = model.EnableAuctions;
        linkshell.EnableToDs     = model.EnableToDs;
        linkshell.EnableEvents   = model.EnableEvents;
        linkshell.EnableDkp      = model.EnableDkp;
        linkshell.EnableItems    = model.EnableItems;
        linkshell.EnableRevenue  = model.EnableRevenue;

        await _context.SaveChangesAsync();
        TempData["CustomizeSaved"] = "Customization saved.";
        return RedirectToAction(nameof(Customize), new { id = linkshell.Id });
    }

    private static LinkshellCustomizeViewModel BuildCustomizeViewModel(
        Linkshell target, IReadOnlyList<Linkshell> manageableLinkshells) =>
        new()
        {
            LinkshellId           = target.Id,
            LinkshellName         = target.LinkshellName,
            LootStructure         = target.LootStructure,
            DkpRoundingIncrement  = target.DkpRoundingIncrement,
            EnableEndgame         = target.EnableEndgame,
            EnableHnmSection      = target.EnableHnmSection,
            EnableMissions        = target.EnableMissions,
            EnableAuctions        = target.EnableAuctions,
            EnableToDs            = target.EnableToDs,
            EnableEvents          = target.EnableEvents,
            EnableDkp             = target.EnableDkp,
            EnableItems           = target.EnableItems,
            EnableRevenue         = target.EnableRevenue,
            ManageableLinkshells  = manageableLinkshells.ToList()
        };

    private async Task<AppUserLinkshell?> GetMembershipAsync(string appUserId, int linkshellId)
    {
        return await _context.AppUserLinkshells
            .Include(link => link.Linkshell)
            .FirstOrDefaultAsync(link => link.AppUserId == appUserId && link.LinkshellId == linkshellId);
    }

    private static bool CanManageLinkshell(AppUserLinkshell? membership)
    {
        if (membership is null || string.IsNullOrWhiteSpace(membership.Rank))
        {
            return false;
        }

        return membership.Rank.Equals("Leader", StringComparison.OrdinalIgnoreCase) ||
               membership.Rank.Equals("Officer", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsLeader(AppUserLinkshell? membership)
        => membership?.Rank?.Equals("Leader", StringComparison.OrdinalIgnoreCase) == true;

    private static string? GetDeleteBlockedReason(int memberCount, int activeEventCount)
    {
        if (memberCount > 1)
        {
            return "Remove the remaining members before deleting this linkshell.";
        }

        if (activeEventCount > 0)
        {
            return "Cancel or finish all queued/live events before deleting this linkshell.";
        }

        return null;
    }
}

