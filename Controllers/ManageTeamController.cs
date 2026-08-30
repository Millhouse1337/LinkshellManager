using LinkshellManagerDiscordApp.Data;
using LinkshellManagerDiscordApp.Models;
using LinkshellManagerDiscordApp.Services;
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
    private readonly AdminOverrideService _adminOverride;
    private readonly Services.InviteCandidateService _inviteCandidates;
    private readonly Services.MemberActivityService _memberActivity;
    private readonly Services.JobsRosterService _jobsRoster;

    public ManageTeamController(
        ApplicationDbContext context,
        UserManager<AppUser> userManager,
        AdminOverrideService adminOverride,
        Services.InviteCandidateService inviteCandidates,
        Services.MemberActivityService memberActivity,
        Services.JobsRosterService jobsRoster)
    {
        _context = context;
        _userManager = userManager;
        _adminOverride = adminOverride;
        _inviteCandidates = inviteCandidates;
        _memberActivity = memberActivity;
        _jobsRoster = jobsRoster;
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

        // Active/Inactive status is deliberately NOT recomputed here. A GET must not
        // mutate data, and re-deriving + persisting Status on every page load is what
        // made this web roster disagree with the Discord Activity (which simply shows
        // the stored Status). Status is kept current at the authoritative WRITE moments
        // instead — event close, event-history edits, and threshold/enable changes
        // (see the other ApplyComputedStatusAsync callers) — so both surfaces now read
        // the same value. The two streak columns below are computed live (read-only).
        ViewBag.MemberStreaks = await _memberActivity.ComputeStreaksByAppUserAsync(targetId, HttpContext.RequestAborted);

        // Biddable DKP per member (committed − bid locks − pending live-event loot spend),
        // shown under the DKP column so spendable power is clear (matches the Activity roster).
        ViewBag.BiddableDkp = await AuctionDkpService.ComputeBiddableDkpByUserAsync(_context, targetId, HttpContext.RequestAborted);

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

        // Levels/relics/merits are built by the shared JobsRosterService, so this
        // page and the Dashboard roster's "Show Jobs" toggle render the same pills.
        var entries = await _jobsRoster.BuildAsync(targetId, HttpContext.RequestAborted);

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

        // The member's OWN job ratings (self rows: rater == target), keyed by
        // character slot. Drives the relic pill/tooltip AND the read-only gear/skill
        // stars shown on the profile. Scoped to this linkshell, like the rest of the
        // page (ratings don't carry between linkshells).
        var jobCount = EventJobCatalog.MainJobOptions.Length;
        var selfRows = member.AppUserId is null
            ? new List<(int CharacterSlot, int JobIndex, int Gear, int Skill, bool HasRelic, string Names)>()
            : (await _context.JobRatings.AsNoTracking()
                .Where(r => r.LinkshellId == member.LinkshellId
                    && r.RaterAppUserId == r.TargetAppUserId
                    && r.TargetAppUserId == member.AppUserId
                    && r.JobIndex >= 0)
                .Select(r => new { r.CharacterSlot, r.JobIndex, r.Gear, r.Skill, r.HasRelic, r.RelicNames })
                .ToListAsync())
                .Select(r => (r.CharacterSlot, r.JobIndex, r.Gear, r.Skill, r.HasRelic, Names: string.Join(", ", r.RelicNames ?? Array.Empty<string>())))
                .ToList();

        bool[] RelicFlags(int slot)
        {
            var flags = new bool[jobCount];
            foreach (var r in selfRows) { if (r.CharacterSlot == slot && r.HasRelic && r.JobIndex < jobCount) { flags[r.JobIndex] = true; } }
            return flags;
        }
        string[] RelicNames(int slot)
        {
            var names = new string[jobCount];
            for (var i = 0; i < jobCount; i++) { names[i] = string.Empty; }
            foreach (var r in selfRows) { if (r.CharacterSlot == slot && r.HasRelic && r.JobIndex < jobCount) { names[r.JobIndex] = r.Names; } }
            return names;
        }
        int[] GearRatings(int slot)
        {
            var vals = new int[jobCount];
            foreach (var r in selfRows) { if (r.CharacterSlot == slot && r.JobIndex < jobCount) { vals[r.JobIndex] = r.Gear; } }
            return vals;
        }
        int[] SkillRatings(int slot)
        {
            var vals = new int[jobCount];
            foreach (var r in selfRows) { if (r.CharacterSlot == slot && r.JobIndex < jobCount) { vals[r.JobIndex] = r.Skill; } }
            return vals;
        }

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
            Alt2StrongJobs = ProfileJobLevels.ToCatalogFlags(member.AppUser?.Alt2StrongJobs),
            RelicFlags = RelicFlags(0),
            Alt1RelicFlags = RelicFlags(1),
            Alt2RelicFlags = RelicFlags(2),
            RelicNames = RelicNames(0),
            Alt1RelicNames = RelicNames(1),
            Alt2RelicNames = RelicNames(2),
            MeritJobs = ProfileJobLevels.NormalizeMerits(member.MeritJobs),
            Alt1MeritJobs = ProfileJobLevels.NormalizeMerits(member.AppUser?.Alt1MeritJobs),
            Alt2MeritJobs = ProfileJobLevels.NormalizeMerits(member.AppUser?.Alt2MeritJobs),
            GearRatings = GearRatings(0),
            Alt1GearRatings = GearRatings(1),
            Alt2GearRatings = GearRatings(2),
            SkillRatings = SkillRatings(0),
            Alt1SkillRatings = SkillRatings(1),
            Alt2SkillRatings = SkillRatings(2)
        };

        return View(new MemberProfileViewModel
        {
            Entry = entry,
            LinkshellId = member.LinkshellId,
            LinkshellName = member.Linkshell?.LinkshellName,
            TargetAppUserId = member.AppUserId
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

        // Officer streak "Count" override (optional). Mirrors the Discord Activity
        // SetMemberActiveCreditCountAsync: credit and absent overrides are mutually
        // exclusive, and the chosen streak drives Status by the linkshell's
        // thresholds (sticks until the next attendance recompute clears it).
        if (input.StreakCount.HasValue)
        {
            var count = input.StreakCount.Value < 0 ? 0 : input.StreakCount.Value;
            var thresholds = await _context.Linkshells
                .Where(l => l.Id == member.LinkshellId)
                .Select(l => new { l.ActiveAfterAttendances, l.InactiveAfterAbsences })
                .FirstOrDefaultAsync();
            var activeAfter = thresholds?.ActiveAfterAttendances ?? 1;
            if (activeAfter < 1) activeAfter = 1;
            var inactiveAfter = thresholds?.InactiveAfterAbsences ?? 1;
            if (inactiveAfter < 1) inactiveAfter = 1;

            if (string.Equals(input.StreakType, "absent", StringComparison.OrdinalIgnoreCase))
            {
                member.ManualAbsentStreak = count;
                member.ManualActiveCreditStreak = null;
                member.Status = count >= inactiveAfter ? "Inactive" : "Active";
            }
            else
            {
                member.ManualActiveCreditStreak = count;
                member.ManualAbsentStreak = null;
                member.Status = count >= activeAfter ? "Active" : "Inactive";
            }
            // Seed timestamp so the state machine replays only events after this and
            // the manual count accumulates with subsequent attendance.
            member.ManualStreakSetAt = DateTime.UtcNow;
        }

        await _context.SaveChangesAsync();

        return RedirectToAction(nameof(Index), new { selectedLinkshellId = member.LinkshellId });
    }

    private async Task<bool> CanManageAsync(string appUserId, int? linkshellId)
    {
        if (!linkshellId.HasValue) return false;
        var membership = await _context.AppUserLinkshells
            .FirstOrDefaultAsync(ul => ul.AppUserId == appUserId && ul.LinkshellId == linkshellId.Value);
        if (membership is null) return false;
        return LinkshellRanks.IsLeaderOrOfficer(membership.Rank)
               || await _adminOverride.IsActiveForAsync(appUserId, HttpContext.RequestAborted);
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
