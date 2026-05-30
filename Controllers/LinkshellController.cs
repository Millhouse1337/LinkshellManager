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
public class LinkshellController : Controller
{
    private readonly ApplicationDbContext _context;
    private readonly UserManager<AppUser> _userManager;
    private readonly Services.DiscordTodBoardQueue _todBoardQueue;

    public LinkshellController(
        ApplicationDbContext context,
        UserManager<AppUser> userManager,
        Services.DiscordTodBoardQueue todBoardQueue)
    {
        _context = context;
        _userManager = userManager;
        _todBoardQueue = todBoardQueue;
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
            Rank = LinkshellRanks.Leader,
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

        var roles = await EnsureDefaultRolesAsync(target.Id, HttpContext.RequestAborted);
        var membership = await GetMembershipAsync(user.Id, target.Id);
        var vm = BuildCustomizeViewModel(
            target,
            manageableLinkshells,
            CanRole(roles, membership?.Rank, role => role.CanManageRoles));
        vm.DiscordWebhooks = await _context.LinkshellDiscordWebhooks
            .Where(w => w.LinkshellId == target.Id)
            .OrderBy(w => w.Id)
            .Select(w => new DiscordWebhookInput
            {
                Name = w.Name,
                Url = w.Url,
                PostTodBoard = w.PostTodBoard,
                PostDkpSpendLog = w.PostDkpSpendLog,
                PostAttendanceSnapshot = w.PostAttendanceSnapshot,
                PostAuctions = w.PostAuctions,
            })
            .ToListAsync();
        EnsureWebhookRow(vm);
        return View(vm);
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

        // Discord webhooks are optional; each row with a URL must point at a
        // real Discord webhook endpoint so a typo can't silently swallow
        // posts. Rows with a blank URL are ignored (treated as "remove").
        model.DiscordWebhooks ??= new List<DiscordWebhookInput>();
        for (var i = 0; i < model.DiscordWebhooks.Count; i++)
        {
            var url = model.DiscordWebhooks[i].Url?.Trim();
            if (string.IsNullOrEmpty(url))
            {
                continue;
            }
            if (!IsValidDiscordWebhookUrl(url))
            {
                ModelState.AddModelError($"DiscordWebhooks[{i}].Url",
                    "Enter a valid Discord channel webhook URL (https://discord.com/api/webhooks/...), or clear the row.");
            }
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
            var roles = await EnsureDefaultRolesAsync(linkshell.Id, HttpContext.RequestAborted);
            model.CanManageRoles = CanRole(roles, membership?.Rank, role => role.CanManageRoles);
            EnsureWebhookRow(model);
            return View(model);
        }

        linkshell.LinkshellType = LinkshellTypes.Normalize(model.LinkshellType);
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
        // Pipe-separated, trimmed, de-duped — same storage format the Discord
        // Activity writes and TodController reads when filtering the tracker.
        linkshell.HiddenTodMonsters = string.Join('|',
            (model.HiddenTodMonsters ?? new List<string>())
                .Select(name => name?.Trim())
                .Where(name => !string.IsNullOrEmpty(name))
                .Distinct(StringComparer.OrdinalIgnoreCase));
        // Upsert by URL so an existing webhook's TodBoardMessageId survives an
        // edit (otherwise a blunt delete+recreate would orphan the live board
        // message and post a duplicate). Rows whose URL is gone are deleted;
        // unchanged URLs keep their Id + board message id and just refresh
        // Name / PostTodBoard; new URLs are added.
        var existingWebhooks = await _context.LinkshellDiscordWebhooks
            .Where(w => w.LinkshellId == linkshell.Id)
            .ToListAsync();
        var keptUrls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in model.DiscordWebhooks)
        {
            var url = row.Url?.Trim();
            if (string.IsNullOrEmpty(url))
            {
                continue;
            }
            keptUrls.Add(url);
            var name = row.Name?.Trim();
            // The UI is a single-purpose dropdown now; fan it back out to the
            // Post* booleans the DB model / publishers still use. One purpose
            // per channel (or none).
            var purpose = row.Purpose?.Trim();
            var postAttendanceSnapshot = string.Equals(
                purpose, DiscordWebhookInput.PurposeDkpTracking, StringComparison.OrdinalIgnoreCase);
            var postTodBoard = string.Equals(
                purpose, DiscordWebhookInput.PurposePopTracker, StringComparison.OrdinalIgnoreCase);
            var postDkpSpendLog = string.Equals(
                purpose, DiscordWebhookInput.PurposeSpentPoints, StringComparison.OrdinalIgnoreCase);
            var postAuctions = string.Equals(
                purpose, DiscordWebhookInput.PurposeAuctions, StringComparison.OrdinalIgnoreCase);
            var existing = existingWebhooks
                .FirstOrDefault(w => string.Equals(w.Url, url, StringComparison.OrdinalIgnoreCase));
            if (existing is not null)
            {
                existing.Name = string.IsNullOrEmpty(name) ? null : name;
                existing.PostTodBoard = postTodBoard;
                existing.PostDkpSpendLog = postDkpSpendLog;
                existing.PostAttendanceSnapshot = postAttendanceSnapshot;
                existing.PostAuctions = postAuctions;
            }
            else
            {
                _context.LinkshellDiscordWebhooks.Add(new LinkshellDiscordWebhook
                {
                    LinkshellId = linkshell.Id,
                    Name = string.IsNullOrEmpty(name) ? null : name,
                    Url = url,
                    PostTodBoard = postTodBoard,
                    PostDkpSpendLog = postDkpSpendLog,
                    PostAttendanceSnapshot = postAttendanceSnapshot,
                    PostAuctions = postAuctions,
                    CreatedAtUtc = DateTime.UtcNow,
                });
            }
        }
        var removed = existingWebhooks
            .Where(w => !keptUrls.Contains(w.Url))
            .ToList();
        _context.LinkshellDiscordWebhooks.RemoveRange(removed);

        await _context.SaveChangesAsync();

        // Refresh the live ToD board now so toggling a channel on (or editing
        // its name) reflects immediately rather than waiting for the next ToD
        // change. No-op when no board webhook is configured.
        _todBoardQueue.Enqueue(linkshell.Id);
        TempData["CustomizeSaved"] = "Customization saved.";
        return RedirectToAction(nameof(Customize), new { id = linkshell.Id });
    }

    private static LinkshellCustomizeViewModel BuildCustomizeViewModel(
        Linkshell target, IReadOnlyList<Linkshell> manageableLinkshells, bool canManageRoles) =>
        new()
        {
            LinkshellId           = target.Id,
            LinkshellName         = target.LinkshellName,
            LinkshellType         = LinkshellTypes.Normalize(target.LinkshellType),
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
            HiddenTodMonsters     = (target.HiddenTodMonsters ?? string.Empty)
                .Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToList(),
            CanManageRoles        = canManageRoles,
            ManageableLinkshells  = manageableLinkshells.ToList()
        };

    // Discord webhook URL must point at a real Discord webhook endpoint so a
    // typo can't silently swallow snapshot posts.
    private static bool IsValidDiscordWebhookUrl(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri)
        && uri.Scheme == Uri.UriSchemeHttps
        && uri.Host.EndsWith("discord.com", StringComparison.OrdinalIgnoreCase)
        && uri.AbsolutePath.Contains("/api/webhooks/", StringComparison.OrdinalIgnoreCase);

    // The editor always shows at least one (blank) webhook row so there's
    // somewhere to type the first URL.
    private static void EnsureWebhookRow(LinkshellCustomizeViewModel model)
    {
        model.DiscordWebhooks ??= new List<DiscordWebhookInput>();
        if (model.DiscordWebhooks.Count == 0)
        {
            model.DiscordWebhooks.Add(new DiscordWebhookInput());
        }
    }

    private async Task<List<LinkshellRole>> EnsureDefaultRolesAsync(
        int linkshellId,
        CancellationToken cancellationToken)
    {
        var roles = await _context.LinkshellRoles
            .Where(role => role.LinkshellId == linkshellId)
            .AsNoTracking()
            .ToListAsync(cancellationToken);

        var existingNames = new HashSet<string>(
            roles.Select(role => role.Name),
            StringComparer.OrdinalIgnoreCase);

        var missing = LinkshellRoleDefaults.BuildDefaultRoles(linkshellId)
            .Where(role => !existingNames.Contains(role.Name))
            .ToList();

        if (missing.Count > 0)
        {
            await _context.LinkshellRoles.AddRangeAsync(missing, cancellationToken);
            await _context.SaveChangesAsync(cancellationToken);
            roles.AddRange(missing);
        }

        return roles
            .OrderBy(role => role.SortOrder)
            .ThenBy(role => role.Name)
            .ToList();
    }

    private static bool CanRole(
        IReadOnlyList<LinkshellRole> roles,
        string? rank,
        Func<LinkshellRole, bool> selector)
    {
        var rankName = string.IsNullOrWhiteSpace(rank) ? "Member" : rank.Trim();
        var role = roles.FirstOrDefault(role => role.Name.Equals(rankName, StringComparison.OrdinalIgnoreCase))
            ?? roles.FirstOrDefault(role => role.Name.Equals("Member", StringComparison.OrdinalIgnoreCase));

        return role is not null && selector(role);
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

