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
    private readonly GlobalSettingsService _globalSettings;
    private readonly DiscordIdentityService _discordIdentity;
    private readonly DiscordBotClient _discordBot;
    private readonly Services.ChannelRouteEditor _channelRoutes;

    public LinkshellController(
        ApplicationDbContext context,
        UserManager<AppUser> userManager,
        Services.DiscordTodBoardQueue todBoardQueue,
        GlobalSettingsService globalSettings,
        DiscordIdentityService discordIdentity,
        DiscordBotClient discordBot,
        Services.ChannelRouteEditor channelRoutes)
    {
        _context = context;
        _userManager = userManager;
        _todBoardQueue = todBoardQueue;
        _globalSettings = globalSettings;
        _discordIdentity = discordIdentity;
        _discordBot = discordBot;
        _channelRoutes = channelRoutes;
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

    // Switches the user's active (primary) linkshell from the sidebar switcher.
    // The whole shell resolves data off PrimaryLinkshellId, so changing it here
    // re-points the dashboard, nav gating, and every linkshell-scoped page.
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Switch(int linkshellId, string? returnUrl)
    {
        var user = await _userManager.GetUserAsync(User);
        if (user is null)
        {
            return Challenge();
        }

        var selected = await _context.AppUserLinkshells
            .Where(link => link.AppUserId == user.Id && link.LinkshellId == linkshellId)
            .Select(link => link.Linkshell)
            .FirstOrDefaultAsync();

        // Only linkshells the user actually belongs to can be made active.
        if (selected is null)
        {
            return Forbid();
        }

        user.PrimaryLinkshellId = selected.Id;
        user.PrimaryLinkshellName = selected.LinkshellName;
        await _userManager.UpdateAsync(user);

        // Stay on the current page (local-only) so the switch feels in-place;
        // fall back to the dashboard when there's no safe return target.
        if (!string.IsNullOrWhiteSpace(returnUrl) && Url.IsLocalUrl(returnUrl))
        {
            return Redirect(returnUrl);
        }

        return RedirectToAction("Index", "Dashboard");
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
                        && (link.Rank == LinkshellRanks.Leader || link.Rank == LinkshellRanks.Officer))
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
        vm.DiscordGuildId = target.DiscordGuildId;
        vm.DiscordGuildName = target.DiscordGuildName;
        vm.DiscussionChannelId = target.DiscussionChannelId;
        vm.GuildLocked = target.LockToDiscordGuild;
        vm.EligibleGuilds = await BuildEligibleGuildsAsync(user.Id, HttpContext.RequestAborted);
        await PopulateDiscordChannelsAsync(vm, target, HttpContext.RequestAborted);
        // Hide the Game Addon pairing card while a super admin has globally
        // disabled the addon (the pairing-code endpoints reject requests anyway).
        vm.AddonGloballyDisabled = await _globalSettings.IsAddonGloballyDisabledAsync(HttpContext.RequestAborted);
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

        if (!ModelState.IsValid)
        {
            var manageable = await _context.AppUserLinkshells
                .Where(link => link.AppUserId == user.Id
                            && (link.Rank == LinkshellRanks.Leader || link.Rank == LinkshellRanks.Officer))
                .Include(link => link.Linkshell)
                .OrderBy(link => link.Linkshell!.LinkshellName)
                .Select(link => link.Linkshell!)
                .ToListAsync();
            model.ManageableLinkshells = manageable;
            model.LinkshellName = linkshell.LinkshellName;
            var roles = await EnsureDefaultRolesAsync(linkshell.Id, HttpContext.RequestAborted);
            model.CanManageRoles = CanRole(roles, membership?.Rank, role => role.CanManageRoles);
            return View(model);
        }

        linkshell.LinkshellType = LinkshellTypes.Normalize(model.LinkshellType);
        linkshell.LootStructure = model.LootStructure!;
        linkshell.DkpRoundingIncrement = model.DkpRoundingIncrement!;
        // Resolve normalises to a known theme key (or the default), so an unknown
        // posted value can never reach the renderer.
        linkshell.EventBoardTheme = EventBoardThemes.Resolve(model.EventBoardTheme);
        linkshell.EnableEndgame  = model.EnableEndgame;
        linkshell.EnableHnmSection = model.EnableHnmSection;
        linkshell.EnableMissions = model.EnableMissions;
        linkshell.EnableAuctions = model.EnableAuctions;
        linkshell.EnableToDs     = model.EnableToDs;
        linkshell.EnableEvents   = model.EnableEvents;
        linkshell.EnableDkp      = model.EnableDkp;
        linkshell.EnableItems    = model.EnableItems;
        linkshell.EnableRevenue  = model.EnableRevenue;
        linkshell.EnableActivityTracking = model.EnableActivityTracking;
        // Clamp to >= 1 so the streak rule can't be configured into a no-op.
        linkshell.InactiveAfterAbsences   = Math.Max(1, model.InactiveAfterAbsences);
        linkshell.ActiveAfterAttendances  = Math.Max(1, model.ActiveAfterAttendances);
        // Pipe-separated, trimmed, de-duped — same storage format the Discord
        // Activity writes and TodController reads when filtering the tracker.
        linkshell.HiddenTodMonsters = string.Join('|',
            (model.HiddenTodMonsters ?? new List<string>())
                .Select(name => name?.Trim())
                .Where(name => !string.IsNullOrEmpty(name))
                .Distinct(StringComparer.OrdinalIgnoreCase));
        await _context.SaveChangesAsync();

        // Enabling tracking or changing the thresholds can change who's Inactive →
        // recompute member statuses now (no-op when tracking is off).
        await new Services.MemberActivityService(_context).ApplyComputedStatusAsync(linkshell.Id, HttpContext.RequestAborted);

        // Refresh the live ToD board now so toggling a channel on (or editing
        // its name) reflects immediately rather than waiting for the next ToD
        // change. No-op when no board webhook is configured.
        _todBoardQueue.Enqueue(linkshell.Id);
        TempData["CustomizeSaved"] = "Customization saved.";
        return RedirectToAction(nameof(Customize), new { id = linkshell.Id });
    }

    // --- Discord server association + optional access lock (two separate steps).
    // SetGuild associates the linkshell with a server (powers roster/invite/
    // channel features) WITHOUT restricting access. SetGuildLock then optionally
    // restricts viewing to that server. The web verifies the target via the bot
    // token: a leader can only set a server they (and the bot) are both in. ---

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SetGuild(int linkshellId, string? guildId)
    {
        var user = await _userManager.GetUserAsync(User);
        if (user is null)
        {
            return Challenge();
        }

        var membership = await GetMembershipAsync(user.Id, linkshellId);
        if (!CanManageLinkshell(membership))
        {
            return Forbid();
        }

        var linkshell = await _context.Linkshells.FindAsync(linkshellId);
        if (linkshell is null)
        {
            return NotFound();
        }

        var trimmed = guildId?.Trim() ?? string.Empty;
        if (trimmed.Length == 0 || trimmed.Length > 20 || !trimmed.All(char.IsDigit))
        {
            TempData["CustomizeError"] = "Enter the numeric Discord server ID (digits only).";
            return RedirectToAction(nameof(Customize), new { id = linkshellId });
        }

        var discordUserId = await ResolveDiscordUserIdAsync(user.Id);
        if (string.IsNullOrWhiteSpace(discordUserId))
        {
            TempData["CustomizeError"] = "Sign in with Discord before setting this linkshell's server.";
            return RedirectToAction(nameof(Customize), new { id = linkshellId });
        }

        // A successful check also proves the bot is in that guild (the bot token
        // can't read members of a guild it isn't in), so the name we read back
        // from the bot's guild list below is trustworthy.
        if (!await _discordIdentity.IsUserInGuildAsync(trimmed, discordUserId, HttpContext.RequestAborted))
        {
            TempData["CustomizeError"] =
                "Couldn't verify that server. Make sure both you and the LSM bot are members of it, then try again.";
            return RedirectToAction(nameof(Customize), new { id = linkshellId });
        }

        var botGuilds = await _discordIdentity.ListBotGuildsAsync(HttpContext.RequestAborted);
        var resolvedName = botGuilds?.FirstOrDefault(guild => guild.Id == trimmed)?.Name;

        linkshell.DiscordGuildId = trimmed;
        linkshell.DiscordGuildName = string.IsNullOrWhiteSpace(resolvedName) ? null : resolvedName;
        await _context.SaveChangesAsync();

        TempData["CustomizeSaved"] = "Discord server set. It now powers roster, invites, and channel posting (access is not restricted unless you turn on the lock below).";
        return RedirectToAction(nameof(Customize), new { id = linkshellId });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ClearGuild(int linkshellId)
    {
        var user = await _userManager.GetUserAsync(User);
        if (user is null)
        {
            return Challenge();
        }

        var membership = await GetMembershipAsync(user.Id, linkshellId);
        if (!CanManageLinkshell(membership))
        {
            return Forbid();
        }

        var linkshell = await _context.Linkshells.FindAsync(linkshellId);
        if (linkshell is null)
        {
            return NotFound();
        }

        linkshell.DiscordGuildId = null;
        linkshell.DiscordGuildName = null;
        linkshell.LockToDiscordGuild = false;
        await _context.SaveChangesAsync();

        TempData["CustomizeSaved"] = "Discord server cleared.";
        return RedirectToAction(nameof(Customize), new { id = linkshellId });
    }

    // Sets (or clears, when blank) the channel post-event discussion comments
    // mirror to. Just stores the id; the bot posts there on each new comment.
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SetDiscussionChannel(int linkshellId, string? discussionChannelId)
    {
        var user = await _userManager.GetUserAsync(User);
        if (user is null) return Challenge();

        var membership = await GetMembershipAsync(user.Id, linkshellId);
        if (!CanManageLinkshell(membership)) return Forbid();

        var linkshell = await _context.Linkshells.FindAsync(linkshellId);
        if (linkshell is null) return NotFound();

        var trimmed = discussionChannelId?.Trim();
        if (!string.IsNullOrEmpty(trimmed) && (trimmed.Length > 20 || !trimmed.All(char.IsDigit)))
        {
            TempData["CustomizeError"] = "Enter the numeric Discord channel ID (digits only), or leave blank to clear.";
            return RedirectToAction(nameof(Customize), new { id = linkshellId });
        }

        linkshell.DiscussionChannelId = string.IsNullOrEmpty(trimmed) ? null : trimmed;
        await _context.SaveChangesAsync();

        TempData["CustomizeSaved"] = string.IsNullOrEmpty(trimmed)
            ? "Discussion channel cleared — comments stay in-app."
            : "Discussion channel set. New post-event comments will mirror there.";
        return RedirectToAction(nameof(Customize), new { id = linkshellId });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SetGuildLock(int linkshellId, bool locked)
    {
        var user = await _userManager.GetUserAsync(User);
        if (user is null)
        {
            return Challenge();
        }

        var membership = await GetMembershipAsync(user.Id, linkshellId);
        if (!CanManageLinkshell(membership))
        {
            return Forbid();
        }

        var linkshell = await _context.Linkshells.FindAsync(linkshellId);
        if (linkshell is null)
        {
            return NotFound();
        }

        if (locked && string.IsNullOrWhiteSpace(linkshell.DiscordGuildId))
        {
            TempData["CustomizeError"] = "Set a Discord server first, then turn on the lock.";
            return RedirectToAction(nameof(Customize), new { id = linkshellId });
        }

        linkshell.LockToDiscordGuild = locked;
        await _context.SaveChangesAsync();

        TempData["CustomizeSaved"] = locked
            ? "Access locked — members can only open this linkshell from the set Discord server."
            : "Access unlocked — members can open this linkshell from anywhere.";
        return RedirectToAction(nameof(Customize), new { id = linkshellId });
    }

    // The caller's Discord user id, resolved from the Discord<->LSM link row
    // created at sign-in. Null if the account was never linked to Discord.
    private async Task<string?> ResolveDiscordUserIdAsync(string appUserId) =>
        await _context.DiscordActivityUsers
            .Where(link => link.IdentityUserId == appUserId)
            .Select(link => link.DiscordUserId)
            .FirstOrDefaultAsync();

    // Servers the lock pick-list offers: those where BOTH the LSM bot and the
    // caller are members. Empty when the bot token isn't configured, the bot is
    // in no servers, or the caller isn't linked to Discord — the view then falls
    // back to a manual numeric-ID field.
    private async Task<List<DiscordGuildOption>> BuildEligibleGuildsAsync(
        string appUserId, CancellationToken cancellationToken)
    {
        var discordUserId = await ResolveDiscordUserIdAsync(appUserId);
        if (string.IsNullOrWhiteSpace(discordUserId))
        {
            return new List<DiscordGuildOption>();
        }

        var botGuilds = await _discordIdentity.ListBotGuildsAsync(cancellationToken);
        if (botGuilds is null || botGuilds.Count == 0)
        {
            return new List<DiscordGuildOption>();
        }

        // Cap the per-guild membership checks so a bot that's in many servers
        // doesn't fan out into dozens of Discord calls on a page load. The
        // common case (a handful of servers) yields the exact "servers you're
        // in" list; beyond the cap the manual-ID field remains available.
        const int maxChecks = 25;
        var eligible = new List<DiscordGuildOption>();
        foreach (var guild in botGuilds.Take(maxChecks))
        {
            if (await _discordIdentity.IsUserInGuildAsync(guild.Id, discordUserId, cancellationToken))
            {
                eligible.Add(new DiscordGuildOption(guild.Id, guild.Name));
            }
        }
        return eligible;
    }

    // --- Discord channel posting config (Phase 2). The bot posts event/auction/
    // loot announcements DIRECTLY to channels (so they can carry interactive
    // components), addressed by channel id per purpose. ---

    private async Task PopulateDiscordChannelsAsync(
        LinkshellCustomizeViewModel vm, Linkshell target, CancellationToken cancellationToken)
    {
        var guildForChannels = target.DiscordGuildId;
        vm.DiscordChannelGuildId = guildForChannels;

        if (!string.IsNullOrWhiteSpace(guildForChannels))
        {
            var available = await _discordBot.ListTextChannelsAsync(guildForChannels, cancellationToken);
            if (available is not null)
            {
                vm.AvailableChannels = available
                    .Select(channel => new DiscordChannelOption(channel.Id, channel.Name))
                    .ToList();
            }
        }

        vm.ChannelRoutes = await _context.LinkshellChannelRoutes
            .AsNoTracking()
            .Where(route => route.LinkshellId == target.Id)
            .OrderBy(route => route.Id)
            .Select(route => new ChannelRouteInput
            {
                Id = route.Id,
                Name = route.Name,
                ChannelId = route.ChannelId,
                PostEvents = route.PostEvents,
                PostLoot = route.PostLoot,
                PostAuctions = route.PostAuctions,
                PostAttendance = route.PostAttendance,
                PostTodBoard = route.PostTodBoard,
                EventTypeFilter = (route.EventTypeFilter ?? string.Empty)
                    .Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .ToList(),
            })
            .ToListAsync(cancellationToken);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SaveDiscordChannels(
        int linkshellId, [FromForm] List<ChannelRouteInput>? channelRoutes)
    {
        var user = await _userManager.GetUserAsync(User);
        if (user is null)
        {
            return Challenge();
        }

        var membership = await GetMembershipAsync(user.Id, linkshellId);
        if (!CanManageLinkshell(membership))
        {
            return Forbid();
        }

        var linkshell = await _context.Linkshells.FindAsync(linkshellId);
        if (linkshell is null)
        {
            return NotFound();
        }

        var guildForChannels = linkshell.DiscordGuildId;
        var available = string.IsNullOrWhiteSpace(guildForChannels)
            ? null
            : await _discordBot.ListTextChannelsAsync(guildForChannels, HttpContext.RequestAborted);

        var edits = (channelRoutes ?? new List<ChannelRouteInput>())
            .Select(r => new Services.ChannelRouteEdit(
                r.Id == 0 ? null : r.Id, r.Name, r.ChannelId,
                r.PostEvents, r.PostLoot, r.PostAuctions, r.PostAttendance, r.PostTodBoard,
                r.EventTypeFilter))
            .ToList();

        var error = await _channelRoutes.SaveAsync(linkshellId, edits, available, HttpContext.RequestAborted);
        TempData[error is null ? "CustomizeSaved" : "CustomizeError"] =
            error ?? "Discord channels updated.";
        return RedirectToAction(nameof(Customize), new { id = linkshellId });
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
            EventBoardTheme       = EventBoardThemes.Resolve(target.EventBoardTheme),
            EnableEndgame         = target.EnableEndgame,
            EnableHnmSection      = target.EnableHnmSection,
            EnableMissions        = target.EnableMissions,
            EnableAuctions        = target.EnableAuctions,
            EnableToDs            = target.EnableToDs,
            EnableEvents          = target.EnableEvents,
            EnableDkp             = target.EnableDkp,
            EnableItems           = target.EnableItems,
            EnableRevenue         = target.EnableRevenue,
            EnableActivityTracking = target.EnableActivityTracking,
            InactiveAfterAbsences  = target.InactiveAfterAbsences,
            ActiveAfterAttendances = target.ActiveAfterAttendances,
            HiddenTodMonsters     = (target.HiddenTodMonsters ?? string.Empty)
                .Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToList(),
            CanManageRoles        = canManageRoles,
            ManageableLinkshells  = manageableLinkshells.ToList()
        };

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
        return LinkshellRanks.IsLeaderOrOfficer(membership?.Rank);
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

