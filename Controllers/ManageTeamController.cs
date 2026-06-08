using LinkshellManagerDiscordApp.Data;
using LinkshellManagerDiscordApp.Models;
using LinkshellManagerDiscordApp.Utils;
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
    private readonly Services.MemberActivityService _memberActivity;

    public ManageTeamController(
        ApplicationDbContext context,
        UserManager<AppUser> userManager,
        Services.InviteCandidateService inviteCandidates,
        Services.MemberActivityService memberActivity)
    {
        _context = context;
        _userManager = userManager;
        _inviteCandidates = inviteCandidates;
        _memberActivity = memberActivity;
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

        // Computed Active/Inactive activity badge (separate from the manual Status),
        // only when the linkshell opts into attendance-based activity tracking.
        var selectedLinkshell = userLinkshells.First(l => l.Id == targetId);
        ViewBag.ActivityTrackingEnabled = selectedLinkshell.EnableActivityTracking;
        ViewBag.MemberActivity = selectedLinkshell.EnableActivityTracking
            ? await _memberActivity.ComputeActiveByAppUserAsync(targetId, HttpContext.RequestAborted)
            : new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

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

    // Read-only roster of every member's leveled jobs (the levels they entered on
    // their Profile), for the linkshell's main + alt characters. Any member can
    // view it; the sidebar link sits under the manager-gated Manage Team group.
    public async Task<IActionResult> JobsRoster(int? selectedLinkshellId)
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
            return View(new JobsRosterViewModel());
        }

        var targetId = selectedLinkshellId
            ?? (userLinkshells.Any(l => l.Id == user.PrimaryLinkshellId) ? user.PrimaryLinkshellId : null)
            ?? userLinkshells[0].Id;

        // Only app-linked members carry profile job data; sheet-only placeholders
        // (no AppUserId) have nothing to show, so leave them out.
        var members = await _context.AppUserLinkshells
            .Include(ul => ul.AppUser)
            .Where(ul => ul.LinkshellId == targetId && ul.AppUserId != null)
            .OrderBy(ul => ul.CharacterName)
            .ToListAsync();

        var entries = members.Select(m => new JobsRosterEntry
        {
            CharacterName = m.CharacterName ?? m.AppUser?.CharacterName ?? m.AppUser?.UserName ?? "Unknown",
            Rank = m.Rank,
            JobLevels = ProfileJobLevels.ToCatalogLevels(m.JobLevels),
            Alt1Name = string.IsNullOrWhiteSpace(m.AppUser?.AltCharacterName1) ? null : m.AppUser!.AltCharacterName1,
            Alt1JobLevels = ProfileJobLevels.ToCatalogLevels(m.AppUser?.Alt1JobLevels),
            Alt2Name = string.IsNullOrWhiteSpace(m.AppUser?.AltCharacterName2) ? null : m.AppUser!.AltCharacterName2,
            Alt2JobLevels = ProfileJobLevels.ToCatalogLevels(m.AppUser?.Alt2JobLevels),
            StrongJobs = ProfileJobLevels.ToCatalogFlags(m.StrongJobs),
            Alt1StrongJobs = ProfileJobLevels.ToCatalogFlags(m.AppUser?.Alt1StrongJobs),
            Alt2StrongJobs = ProfileJobLevels.ToCatalogFlags(m.AppUser?.Alt2StrongJobs)
        }).ToList();

        return View(new JobsRosterViewModel
        {
            Linkshells = userLinkshells,
            SelectedLinkshellId = targetId,
            Entries = entries
        });
    }

    // Read-only profile for a single member (their leveled jobs, main + alts) —
    // opened from the View Team roster. id = the AppUserLinkshell row id. Any
    // member of the same linkshell may view it; built to grow (e.g. crafts later).
    public async Task<IActionResult> MemberProfile(int id)
    {
        var user = await _userManager.GetUserAsync(User);
        if (user is null) return Challenge();

        var member = await _context.AppUserLinkshells
            .Include(ul => ul.AppUser)
            .Include(ul => ul.Linkshell)
            .FirstOrDefaultAsync(ul => ul.Id == id);
        if (member is null) return NotFound();

        // Only people in the same linkshell can view a member's profile.
        var callerIsMember = await _context.AppUserLinkshells
            .AnyAsync(l => l.AppUserId == user.Id && l.LinkshellId == member.LinkshellId);
        if (!callerIsMember) return Forbid();

        var entry = new JobsRosterEntry
        {
            CharacterName = member.CharacterName ?? member.AppUser?.CharacterName ?? member.AppUser?.UserName ?? "Unknown",
            Rank = member.Rank,
            JobLevels = ProfileJobLevels.ToCatalogLevels(member.JobLevels),
            Alt1Name = string.IsNullOrWhiteSpace(member.AppUser?.AltCharacterName1) ? null : member.AppUser!.AltCharacterName1,
            Alt1JobLevels = ProfileJobLevels.ToCatalogLevels(member.AppUser?.Alt1JobLevels),
            Alt2Name = string.IsNullOrWhiteSpace(member.AppUser?.AltCharacterName2) ? null : member.AppUser!.AltCharacterName2,
            Alt2JobLevels = ProfileJobLevels.ToCatalogLevels(member.AppUser?.Alt2JobLevels),
            StrongJobs = ProfileJobLevels.ToCatalogFlags(member.StrongJobs),
            Alt1StrongJobs = ProfileJobLevels.ToCatalogFlags(member.AppUser?.Alt1StrongJobs),
            Alt2StrongJobs = ProfileJobLevels.ToCatalogFlags(member.AppUser?.Alt2StrongJobs)
        };

        return View(new MemberProfileViewModel
        {
            Entry = entry,
            LinkshellId = member.LinkshellId,
            LinkshellName = member.Linkshell?.LinkshellName
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

        // When the linkshell is tied to a Discord server, also surface that
        // server's members (including people who've never used LSM) so officers
        // can add them in one click. Skipped when no server is set (no bot call).
        var discordRoster = string.IsNullOrWhiteSpace(targetLinkshell.DiscordGuildId)
            ? new List<Services.DiscordRosterCandidate>()
            : (await _inviteCandidates.GetDiscordRosterCandidatesAsync(
                targetId, targetLinkshell.DiscordGuildId, HttpContext.RequestAborted)).ToList();

        return View("PlayerSearch", new ManageTeamViewModel
        {
            Linkshells = manageable,
            SelectedLinkshellId = targetId,
            CanManage = true,
            SearchTerm = search,
            Filter = filter,
            Candidates = result.Items.ToList(),
            DiscordRoster = discordRoster,
            PageNumber = result.Page,
            PageSize = result.PageSize,
            TotalCount = result.Total
        });
    }

    // Adds a member straight from the linkshell's Discord server (web parity with
    // the Activity). Existing LSM users join immediately; people without an
    // account get a Discord-keyed invite that auto-joins on first sign-in.
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> AddDiscordMember(
        int linkshellId, string discordUserId, string? search, string? filter, int page = 1)
    {
        var user = await _userManager.GetUserAsync(User);
        if (user is null) return Challenge();

        if (!await CanManageAsync(user.Id, linkshellId)) return Forbid();

        var linkshell = await _context.Linkshells.FindAsync(linkshellId);
        var result = await _inviteCandidates.AddDiscordMemberAsync(
            linkshellId, linkshell?.DiscordGuildId, discordUserId, HttpContext.RequestAborted);

        if (!result.Success)
        {
            TempData["AddMemberError"] = result.Error;
        }

        return RedirectToAction(nameof(SearchPlayers),
            new { selectedLinkshellId = linkshellId, search, filter, page });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SendInvite(SendInviteInput input, string? search, string? filter, int page = 1)
    {
        var user = await _userManager.GetUserAsync(User);
        if (user is null) return Challenge();

        if (!await CanManageAsync(user.Id, input.LinkshellId)) return Forbid();

        var targetUser = await _context.Users.FirstOrDefaultAsync(u => u.Id == input.UserId);
        if (targetUser is null) return NotFound();

        var alreadyMember = await _context.AppUserLinkshells
            .AnyAsync(ul => ul.AppUserId == input.UserId && ul.LinkshellId == input.LinkshellId);

        if (!alreadyMember)
        {
            // Auto-join: inviting a player adds them straight to the roster — no
            // accept step. (Discord-only people without an LSM account still get a
            // pending invite that auto-joins on first sign-in; that path lives in
            // the Activity/DiscordIdentityService and is unchanged.)
            var linkshell = await _context.Linkshells.FirstOrDefaultAsync(l => l.Id == input.LinkshellId);
            _context.AppUserLinkshells.Add(new AppUserLinkshell
            {
                AppUserId = targetUser.Id,
                LinkshellId = input.LinkshellId,
                LinkshellDkp = 0,
                DateJoined = DateTime.UtcNow,
                CharacterName = targetUser.CharacterName ?? targetUser.UserName,
                Rank = LinkshellRanks.Member,
                Status = "Active"
            });

            // Drop any stale pending invite/request for this pair so the roster
            // and the invites page don't show a phantom "pending" entry.
            var stale = await _context.Invites
                .Where(i => i.AppUserId == input.UserId && i.LinkshellId == input.LinkshellId)
                .ToListAsync();
            if (stale.Count > 0) _context.Invites.RemoveRange(stale);

            if (targetUser.PrimaryLinkshellId is null)
            {
                targetUser.PrimaryLinkshellId = input.LinkshellId;
                targetUser.PrimaryLinkshellName = linkshell?.LinkshellName;
                _context.Update(targetUser);
            }

            await _context.SaveChangesAsync();
        }

        // Return to the browse (same linkshell + search/filter/page) so officers
        // can keep adding members without losing their place.
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
