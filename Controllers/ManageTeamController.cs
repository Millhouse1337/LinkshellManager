using LinkshellManagerDiscordApp.Data;
using LinkshellManagerDiscordApp.Models;
using LinkshellManagerDiscordApp.ViewModels;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace LinkshellManagerDiscordApp.Controllers;

[Authorize]
public class ManageTeamController : Controller
{
    private readonly ApplicationDbContext _context;
    private readonly UserManager<AppUser> _userManager;

    public ManageTeamController(ApplicationDbContext context, UserManager<AppUser> userManager)
    {
        _context = context;
        _userManager = userManager;
    }

    public async Task<IActionResult> Index(int? selectedLinkshellId, string? search, int page = 1, bool appSync = true)
    {
        var user = await _userManager.GetUserAsync(User);
        if (user is null) return Challenge();

        var userLinkshells = await _context.AppUserLinkshells
            .Include(ul => ul.Linkshell)
            .Where(ul => ul.AppUserId == user.Id)
            .Select(ul => ul.Linkshell!)
            .Where(l => l != null)
            .OrderBy(l => l.LinkshellName)
            .ToListAsync();

        if (userLinkshells.Count == 0)
        {
            ViewBag.Message = "You are not part of any linkshells.";
            return View(new ManageTeamViewModel());
        }

        var targetId = selectedLinkshellId
            ?? (userLinkshells.Any(l => l.Id == user.PrimaryLinkshellId) ? user.PrimaryLinkshellId : null)
            ?? userLinkshells[0].Id;

        var baseQuery = _context.AppUserLinkshells
            .Include(ul => ul.AppUser)
            .Where(ul => ul.LinkshellId == targetId);

        var totalMembers = await baseQuery.CountAsync();

        var term = search?.Trim();
        var filteredQuery = baseQuery;
        if (!string.IsNullOrWhiteSpace(term))
        {
            // Case-insensitive character-name search. LOWER(..) LIKE LOWER(..)
            // is provider-agnostic and matches the .Contains() convention used
            // elsewhere (SearchPlayers) without its case sensitivity.
            var lowered = term.ToLower();
            filteredQuery = filteredQuery.Where(ul =>
                (ul.CharacterName != null && ul.CharacterName.ToLower().Contains(lowered))
                || (ul.AppUser != null && ul.AppUser.CharacterName != null
                    && ul.AppUser.CharacterName.ToLower().Contains(lowered)));
        }

        // App Sync filter: limits the roster to "fully onboarded" rows -- app
        // account linked AND Status is Active (or null, which the view renders
        // as Active). Defaults ON so the page leads with members the linkshell
        // actually has tracking on; the view's checkbox flips it off to also
        // show Unclaimed / Pending sheet placeholders.
        if (appSync)
        {
            filteredQuery = filteredQuery.Where(ul =>
                ul.AppUserId != null
                && (ul.Status == null || ul.Status == "Active"));
        }

        var totalCount = await filteredQuery.CountAsync();
        const int pageSize = ManageTeamViewModel.MembersPageSize;
        var totalPages = totalCount == 0 ? 1 : (int)Math.Ceiling(totalCount / (double)pageSize);
        var pageNumber = Math.Clamp(page, 1, totalPages);

        var members = await filteredQuery
            .OrderBy(ul => ul.CharacterName)
            .Skip((pageNumber - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync();

        var canManage = await CanManageAsync(user.Id, targetId);

        return View(new ManageTeamViewModel
        {
            Linkshells = userLinkshells,
            Members = members,
            SelectedLinkshellId = targetId,
            CanManage = canManage,
            SearchTerm = term,
            AppSyncOnly = appSync,
            PageNumber = pageNumber,
            PageSize = pageSize,
            TotalCount = totalCount,
            TotalMembers = totalMembers
        });
    }

    public async Task<IActionResult> SearchPlayers()
    {
        var user = await _userManager.GetUserAsync(User);
        if (user is null) return Challenge();

        var manageable = await GetManageableLinkshellsAsync(user.Id);
        if (manageable.Count == 0) return Forbid();

        return View("PlayerSearch", new ManageTeamViewModel
        {
            Linkshells = manageable,
            CanManage = true
        });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SearchPlayers(string? searchTerm)
    {
        var user = await _userManager.GetUserAsync(User);
        if (user is null) return Challenge();

        var manageable = await GetManageableLinkshellsAsync(user.Id);
        if (manageable.Count == 0) return Forbid();

        var players = new List<AppUser>();
        if (!string.IsNullOrWhiteSpace(searchTerm))
        {
            var term = searchTerm.Trim();
            players = await _context.Users
                .Where(u => u.CharacterName != null
                            && u.CharacterName.Contains(term)
                            && u.Id != user.Id)
                .OrderBy(u => u.CharacterName)
                .ToListAsync();
        }

        return View("PlayerSearch", new ManageTeamViewModel
        {
            Linkshells = manageable,
            Players = players,
            SearchTerm = searchTerm,
            CanManage = true
        });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SendInvite(SendInviteInput input)
    {
        var user = await _userManager.GetUserAsync(User);
        if (user is null) return Challenge();

        if (!await CanManageAsync(user.Id, input.LinkshellId)) return Forbid();

        var targetExists = await _context.Users.AnyAsync(u => u.Id == input.UserId);
        if (!targetExists) return NotFound();

        var alreadyMember = await _context.AppUserLinkshells
            .AnyAsync(ul => ul.AppUserId == input.UserId && ul.LinkshellId == input.LinkshellId);
        var alreadyInvited = await _context.Invites
            .AnyAsync(i => i.AppUserId == input.UserId && i.LinkshellId == input.LinkshellId);

        if (!alreadyMember && !alreadyInvited)
        {
            _context.Invites.Add(new Invite
            {
                AppUserId = input.UserId,
                LinkshellId = input.LinkshellId,
                Status = "Pending"
            });
            await _context.SaveChangesAsync();
        }

        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> AcceptInvite(int inviteId)
    {
        var user = await _userManager.GetUserAsync(User);
        if (user is null) return Challenge();

        var invite = await _context.Invites
            .Include(i => i.Linkshell)
            .FirstOrDefaultAsync(i => i.Id == inviteId);
        if (invite is null) return NotFound();
        if (invite.AppUserId != user.Id) return Forbid();

        _context.AppUserLinkshells.Add(new AppUserLinkshell
        {
            AppUserId = invite.AppUserId,
            LinkshellId = invite.LinkshellId,
            LinkshellDkp = 0,
            DateJoined = DateTime.UtcNow,
            CharacterName = user.CharacterName,
            Rank = "Member",
            Status = "Active"
        });
        _context.Invites.Remove(invite);

        if (user.PrimaryLinkshellId is null)
        {
            user.PrimaryLinkshellId = invite.LinkshellId;
            user.PrimaryLinkshellName = invite.Linkshell?.LinkshellName;
            _context.Update(user);
        }

        await _context.SaveChangesAsync();
        return RedirectToAction(nameof(ViewInvites));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DeclineInvite(int inviteId)
    {
        var user = await _userManager.GetUserAsync(User);
        if (user is null) return Challenge();

        var invite = await _context.Invites.FirstOrDefaultAsync(i => i.Id == inviteId);
        if (invite is null) return NotFound();
        if (invite.AppUserId != user.Id) return Forbid();

        _context.Invites.Remove(invite);
        await _context.SaveChangesAsync();
        return RedirectToAction(nameof(ViewInvites));
    }

    public async Task<IActionResult> ViewInvites()
    {
        var user = await _userManager.GetUserAsync(User);
        if (user is null) return Challenge();

        var pendingInvites = await _context.Invites
            .Include(i => i.Linkshell)
            .Include(i => i.AppUser)
            .Where(i => i.AppUserId == user.Id && i.Status == "Pending")
            .ToListAsync();

        var manageableIds = await _context.AppUserLinkshells
            .Where(ul => ul.AppUserId == user.Id
                         && (ul.Rank == "Leader" || ul.Rank == "Officer"))
            .Select(ul => ul.LinkshellId)
            .ToListAsync();

        var sentInvites = await _context.Invites
            .Include(i => i.Linkshell)
            .Include(i => i.AppUser)
            .Where(i => manageableIds.Contains(i.LinkshellId) && i.Status == "Pending")
            .ToListAsync();

        return View(new ManageTeamViewModel
        {
            PendingInvites = pendingInvites,
            SentInvites = sentInvites,
            CanManage = manageableIds.Count > 0
        });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> UndoInvite(int inviteId)
    {
        var user = await _userManager.GetUserAsync(User);
        if (user is null) return Challenge();

        var invite = await _context.Invites.FirstOrDefaultAsync(i => i.Id == inviteId);
        if (invite is null) return NotFound();

        if (!await CanManageAsync(user.Id, invite.LinkshellId)) return Forbid();

        _context.Invites.Remove(invite);
        await _context.SaveChangesAsync();
        return RedirectToAction(nameof(ViewInvites));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ModifyRankStatus(ModifyRankStatusInput input)
    {
        var user = await _userManager.GetUserAsync(User);
        if (user is null) return Challenge();

        var member = await _context.AppUserLinkshells.FirstOrDefaultAsync(ul => ul.Id == input.Id);
        if (member is null) return NotFound();

        if (!await CanManageAsync(user.Id, member.LinkshellId)) return Forbid();

        if (!ModelState.IsValid) return RedirectToAction(nameof(Index), new { selectedLinkshellId = member.LinkshellId });

        member.Rank = input.Rank;
        member.Status = input.Status;
        await _context.SaveChangesAsync();

        return RedirectToAction(nameof(Index), new { selectedLinkshellId = member.LinkshellId });
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
