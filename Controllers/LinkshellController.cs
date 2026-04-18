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

