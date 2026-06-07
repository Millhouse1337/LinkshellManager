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
    private readonly Services.InviteCandidateService _inviteCandidates;

    public ManageTeamController(
        ApplicationDbContext context,
        UserManager<AppUser> userManager,
        Services.InviteCandidateService inviteCandidates)
    {
        _context = context;
        _userManager = userManager;
        _inviteCandidates = inviteCandidates;
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

    // Add-members browse. Paginated, searchable, and filterable via the shared
    // InviteCandidateService (same query the Discord Activity's invite panel
    // uses), so the web and Activity surface the same eligible players — minus
    // current members, anyone already invited (from either front-end), and the
    // caller — and honor the linkshell's Discord-server lock. Kept under the
    // SearchPlayers action name so the sidebar/"Add members" links + active
    // state are unchanged.
    public async Task<IActionResult> SearchPlayers(
        int? selectedLinkshellId, string? search, string? filter, int page = 1)
    {
        var user = await _userManager.GetUserAsync(User);
        if (user is null) return Challenge();

        var manageable = await GetManageableLinkshellsAsync(user.Id);
        if (manageable.Count == 0) return Forbid();

        var targetId = selectedLinkshellId.HasValue && manageable.Any(l => l.Id == selectedLinkshellId.Value)
            ? selectedLinkshellId.Value
            : manageable[0].Id;
        var targetLinkshell = manageable.First(l => l.Id == targetId);

        var result = await _inviteCandidates.BrowseAsync(
            targetId,
            user.Id,
            targetLinkshell.DiscordGuildId,
            search,
            filter,
            page,
            ManageTeamViewModel.MembersPageSize,
            HttpContext.RequestAborted);

        return View("PlayerSearch", new ManageTeamViewModel
        {
            Linkshells = manageable,
            SelectedLinkshellId = targetId,
            CanManage = true,
            SearchTerm = search,
            Filter = filter,
            Candidates = result.Items.ToList(),
            PageNumber = result.Page,
            PageSize = result.PageSize,
            TotalCount = result.Total
        });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SendInvite(SendInviteInput input, string? search, string? filter, int page = 1)
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

        // Return to the browse (same linkshell + search/filter/page) so officers
        // can keep inviting without losing their place.
        return RedirectToAction(nameof(SearchPlayers),
            new { selectedLinkshellId = input.LinkshellId, search, filter, page });
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

        // Actual linkshell->user invites can carry the web's "Pending" or the
        // Activity's "PendingInvite" status; recognize both so an invite shows
        // here no matter which front-end sent it. (Join requests —
        // "PendingJoinRequest" is the user-initiated request-to-join, handled in
        // its own section below with Approve/Decline.)
        var inviteStatuses = new[] { "Pending", "PendingInvite" };

        var pendingInvites = await _context.Invites
            .Include(i => i.Linkshell)
            .Include(i => i.AppUser)
            .Where(i => i.AppUserId == user.Id && inviteStatuses.Contains(i.Status))
            .ToListAsync();

        var manageableIds = await _context.AppUserLinkshells
            .Where(ul => ul.AppUserId == user.Id
                         && (ul.Rank == LinkshellRanks.Leader || ul.Rank == LinkshellRanks.Officer))
            .Select(ul => ul.LinkshellId)
            .ToListAsync();

        var sentInvites = await _context.Invites
            .Include(i => i.Linkshell)
            .Include(i => i.AppUser)
            .Where(i => manageableIds.Contains(i.LinkshellId) && inviteStatuses.Contains(i.Status))
            .ToListAsync();

        // User-initiated requests to join a linkshell the caller manages (created
        // via the Discord Activity); officers approve/decline them here.
        var joinRequests = await _context.Invites
            .Include(i => i.Linkshell)
            .Include(i => i.AppUser)
            .Where(i => manageableIds.Contains(i.LinkshellId) && i.Status == "PendingJoinRequest")
            .ToListAsync();

        return View(new ManageTeamViewModel
        {
            PendingInvites = pendingInvites,
            SentInvites = sentInvites,
            JoinRequests = joinRequests,
            CanManage = manageableIds.Count > 0
        });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ApproveJoinRequest(int inviteId)
    {
        var user = await _userManager.GetUserAsync(User);
        if (user is null) return Challenge();

        var invite = await _context.Invites
            .Include(i => i.Linkshell)
            .Include(i => i.AppUser)
            .FirstOrDefaultAsync(i => i.Id == inviteId && i.Status == "PendingJoinRequest");
        if (invite is null) return NotFound();

        if (!await CanManageAsync(user.Id, invite.LinkshellId)) return Forbid();

        var alreadyMember = await _context.AppUserLinkshells
            .AnyAsync(ul => ul.LinkshellId == invite.LinkshellId && ul.AppUserId == invite.AppUserId);
        if (!alreadyMember)
        {
            _context.AppUserLinkshells.Add(new AppUserLinkshell
            {
                AppUserId = invite.AppUserId,
                LinkshellId = invite.LinkshellId,
                LinkshellDkp = 0,
                DateJoined = DateTime.UtcNow,
                CharacterName = invite.AppUser?.CharacterName ?? invite.AppUser?.UserName,
                Rank = LinkshellRanks.Member,
                Status = "Active"
            });
        }

        if (invite.AppUser is not null)
        {
            invite.AppUser.PrimaryLinkshellId ??= invite.LinkshellId;
            invite.AppUser.PrimaryLinkshellName ??= invite.Linkshell?.LinkshellName;
            await _userManager.UpdateAsync(invite.AppUser);
        }

        _context.Invites.Remove(invite);
        await _context.SaveChangesAsync();
        return RedirectToAction(nameof(ViewInvites));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DeclineJoinRequest(int inviteId)
    {
        var user = await _userManager.GetUserAsync(User);
        if (user is null) return Challenge();

        var invite = await _context.Invites
            .FirstOrDefaultAsync(i => i.Id == inviteId && i.Status == "PendingJoinRequest");
        if (invite is null) return NotFound();

        if (!await CanManageAsync(user.Id, invite.LinkshellId)) return Forbid();

        _context.Invites.Remove(invite);
        await _context.SaveChangesAsync();
        return RedirectToAction(nameof(ViewInvites));
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
