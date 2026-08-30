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
    private readonly AdminOverrideService _adminOverride;
    private readonly Services.DiscordTodBoardQueue _todBoardQueue;
    private readonly GlobalSettingsService _globalSettings;
    private readonly DiscordIdentityService _discordIdentity;
    private readonly DiscordBotClient _discordBot;
    private readonly Services.ChannelRouteEditor _channelRoutes;

    private readonly Services.DkpPoolResolver _dkpPools;
    private readonly Services.DkpPoolEditor _dkpPoolEditor;
    private readonly Services.DkpPoolEventTypeCatalog _dkpPoolEventTypes;
    private readonly Services.LinkshellMonsterTimingProvisioner _monsterTimingProvisioner;
    private readonly Services.MonsterTimingEditor _monsterTimingEditor;

    public LinkshellController(
        ApplicationDbContext context,
        UserManager<AppUser> userManager,
        AdminOverrideService adminOverride,
        Services.DiscordTodBoardQueue todBoardQueue,
        GlobalSettingsService globalSettings,
        DiscordIdentityService discordIdentity,
        DiscordBotClient discordBot,
        Services.ChannelRouteEditor channelRoutes,
        Services.DkpPoolResolver dkpPools,
        Services.DkpPoolEditor dkpPoolEditor,
        Services.DkpPoolEventTypeCatalog dkpPoolEventTypes,
        Services.LinkshellMonsterTimingProvisioner monsterTimingProvisioner,
        Services.MonsterTimingEditor monsterTimingEditor)
    {
        _context = context;
        _userManager = userManager;
        _adminOverride = adminOverride;
        _todBoardQueue = todBoardQueue;
        _globalSettings = globalSettings;
        _discordIdentity = discordIdentity;
        _discordBot = discordBot;
        _channelRoutes = channelRoutes;
        _dkpPools = dkpPools;
        _dkpPoolEditor = dkpPoolEditor;
        _dkpPoolEventTypes = dkpPoolEventTypes;
        _monsterTimingProvisioner = monsterTimingProvisioner;
        _monsterTimingEditor = monsterTimingEditor;
    }

    // Fills the DKP-pools card: the linkshell's pools, plus one assignment row per assignable event
    // type. Assignments bind by pool INDEX, so an officer can add a pool and move event types into
    // it in a single save — a new pool has no id to bind to yet.
    // The linkshell's per-monster setups for the Monster Setups card. Loading is what SEEDS the
    // catalog, exactly as the Activity's GET does — both converge on the same provisioner so a
    // linkshell ends up with one catalog whichever surface it opens first.
    private async Task LoadMonsterTimingInputsAsync(LinkshellCustomizeViewModel model, int linkshellId)
    {
        var rows = await _monsterTimingProvisioner.EnsureSeededAsync(linkshellId, HttpContext.RequestAborted);
        model.MonsterTimings = rows.Select(MonsterTimingInput.From).ToList();
        model.MonsterTimingCategories = Services.MonsterTimingDefaults.Categories.ToList();
        model.MonsterTimingMaxWindows = Services.HnmConfig.MaxWindow;
    }

    private async Task LoadDkpPoolInputsAsync(LinkshellCustomizeViewModel model, int linkshellId)
    {
        var map = await _dkpPools.GetMapAsync(linkshellId, HttpContext.RequestAborted);
        var catalog = await _dkpPoolEventTypes.LoadAsync(linkshellId, HttpContext.RequestAborted);

        model.DkpPools = map.Pools
            .Select(pool => new DkpPoolInput
            {
                Id = pool.Id,
                Name = pool.Name,
                IsDefault = pool.IsDefault,
                Accent = Models.DkpPoolAccents.Resolve(pool.Accent),
            })
            .ToList();

        var indexByPoolId = model.DkpPools
            .Select((pool, index) => (pool.Id, index))
            .ToDictionary(item => item.Id, item => item.index);
        var defaultPoolIndex = model.DkpPools.FindIndex(pool => pool.IsDefault);

        model.DkpPoolAssignments = catalog
            .Select(option =>
            {
                var poolId = map.Resolve(option.Key);
                // An event type with no explicit mapping shows as Default (-1) rather than
                // pre-selected onto the default pool — otherwise saving the form would silently
                // materialize a mapping row for every event type in the catalog. A type explicitly
                // mapped to the default pool collapses to -1 too: the default group is the "Default"
                // option in the picker, never a numbered one.
                var isMapped = map.PoolIdByNormalizedEventType.ContainsKey(
                    Models.DkpPoolEventType.Normalize(option.Key));
                var poolIndex = indexByPoolId.GetValueOrDefault(poolId, -1);
                if (!isMapped || poolIndex == defaultPoolIndex)
                {
                    poolIndex = -1;
                }
                return new DkpPoolAssignmentInput
                {
                    EventType = option.Key,
                    PoolIndex = poolIndex,
                    EarnedTotal = option.EarnedTotal,
                    IsCustom = option.IsCustom,
                };
            })
            .ToList();
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

        ViewBag.CanEditLinkshell = await CanManageLinkshellAsync(membership);
        ViewBag.CanDeleteLinkshell = await IsLeaderAsync(membership);

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
        if (!await CanManageLinkshellAsync(membership))
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
        if (!await CanManageLinkshellAsync(membership))
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
        if (!await IsLeaderAsync(membership))
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
        if (!await IsLeaderAsync(membership))
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

        // With no explicit ?id, configure the user's ACTIVE (primary) linkshell — the
        // sidebar switcher is the single way to change which one you're editing (the
        // in-page "which linkshell" picker was removed in favor of a confirm-on-save).
        var target = id.HasValue
            ? manageableLinkshells.FirstOrDefault(link => link.Id == id.Value)
            : (manageableLinkshells.FirstOrDefault(link => link.Id == user.PrimaryLinkshellId)
               ?? manageableLinkshells.First());
        if (target is null)
        {
            return Forbid();
        }

        var roles = await EnsureDefaultRolesAsync(target.Id, HttpContext.RequestAborted);
        var membership = await GetMembershipAsync(user.Id, target.Id);
        var vm = BuildCustomizeViewModel(
            target,
            manageableLinkshells,
            await CanRoleAsync(roles, membership, role => role.CanManageRoles));
        vm.DiscordGuildId = target.DiscordGuildId;
        vm.DiscordGuildName = target.DiscordGuildName;
        vm.DiscussionChannelId = target.DiscussionChannelId;
        vm.GuildLocked = target.LockToDiscordGuild;
        var bannerUpdatedAt = await _context.LinkshellBanners
            .Where(b => b.LinkshellId == target.Id)
            .Select(b => (DateTime?)b.UpdatedAt)
            .FirstOrDefaultAsync();
        vm.BannerUrl = bannerUpdatedAt.HasValue
            ? $"/api/activity/linkshells/{target.Id}/banner?v={bannerUpdatedAt.Value.Ticks}"
            : null;
        vm.EligibleGuilds = await BuildEligibleGuildsAsync(user.Id, HttpContext.RequestAborted);
        await PopulateDiscordChannelsAsync(vm, target, HttpContext.RequestAborted);
        await LoadDkpPoolInputsAsync(vm, target.Id);
        await LoadMonsterTimingInputsAsync(vm, target.Id);
        // Hide the Game Addon pairing card while a super admin has globally
        // disabled the addon (the pairing-code endpoints reject requests anyway).
        vm.AddonGloballyDisabled = await _globalSettings.IsAddonGloballyDisabledAsync(HttpContext.RequestAborted);
        vm.ClaimShieldGloballyDisabled = await _globalSettings.IsClaimShieldGloballyDisabledAsync(HttpContext.RequestAborted);
        return View(vm);
    }

    // Standalone Permissions page (roles + permission matrix). Same card that used to live
    // on Customize; the JS drives everything via the shared ActivityData roles API.
    [HttpGet]
    public async Task<IActionResult> Permissions(int? id)
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
            return View(new LinkshellCustomizeViewModel { ManageableLinkshells = new List<Linkshell>() });
        }

        var target = id.HasValue
            ? manageableLinkshells.FirstOrDefault(link => link.Id == id.Value)
            : (manageableLinkshells.FirstOrDefault(link => link.Id == user.PrimaryLinkshellId)
               ?? manageableLinkshells.First());
        if (target is null)
        {
            return Forbid();
        }

        var roles = await EnsureDefaultRolesAsync(target.Id, HttpContext.RequestAborted);
        var membership = await GetMembershipAsync(user.Id, target.Id);
        var vm = BuildCustomizeViewModel(
            target, manageableLinkshells, await CanRoleAsync(roles, membership, role => role.CanManageRoles));
        return View(vm);
    }

    // Standalone Game Addon page (pairing tokens + the stylized "about the addon" section).
    [HttpGet]
    public async Task<IActionResult> GameAddon(int? id)
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
            return View(new LinkshellCustomizeViewModel { ManageableLinkshells = new List<Linkshell>() });
        }

        var target = id.HasValue
            ? manageableLinkshells.FirstOrDefault(link => link.Id == id.Value)
            : (manageableLinkshells.FirstOrDefault(link => link.Id == user.PrimaryLinkshellId)
               ?? manageableLinkshells.First());
        if (target is null)
        {
            return Forbid();
        }

        var roles = await EnsureDefaultRolesAsync(target.Id, HttpContext.RequestAborted);
        var membership = await GetMembershipAsync(user.Id, target.Id);
        var vm = BuildCustomizeViewModel(
            target, manageableLinkshells, await CanRoleAsync(roles, membership, role => role.CanManageRoles));
        vm.AddonGloballyDisabled = await _globalSettings.IsAddonGloballyDisabledAsync(HttpContext.RequestAborted);
        vm.ClaimShieldGloballyDisabled = await _globalSettings.IsClaimShieldGloballyDisabledAsync(HttpContext.RequestAborted);
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
        if (!await CanManageLinkshellAsync(membership))
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
            model.CanManageRoles = await CanRoleAsync(roles, membership, role => role.CanManageRoles);
            return View(model);
        }

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
        linkshell.OutsidePartySignupEnabled = model.OutsidePartySignupEnabled;
        linkshell.UseComponentsV2Boards = model.UseComponentsV2Boards;
        // Clamp to >= 1 so the streak rule can't be configured into a no-op.
        linkshell.InactiveAfterAbsences   = Math.Max(1, model.InactiveAfterAbsences);
        linkshell.ActiveAfterAttendances  = Math.Max(1, model.ActiveAfterAttendances);
        // HiddenTodMonsters is deliberately NOT touched here: the web Customize page no longer
        // offers those switches, so this form posts nothing for them and a blind assign would
        // wipe whatever the Discord Activity stored. That column is still read by the ToD
        // tracker and the ToD board publisher.
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

    // --- DKP import (Excel/CSV): a migrating linkshell brings its existing DKP in.
    // Two steps. PreviewDkpImport parses the upload and shows a classified review
    // (update / create / ambiguous) without changing anything; CommitDkpImport
    // applies it. Mirrors the ToD image-upload attributes for multipart limits. ---

    // A blank/sample .xlsx showing the exact columns the importer expects, so a
    // migrating linkshell (with no members to export yet) has something to fill in.
    [HttpGet]
    public IActionResult DownloadDkpTemplate()
    {
        return File(SampleDkpTemplateWorkbook.Build(), SampleDkpTemplateWorkbook.ContentType, SampleDkpTemplateWorkbook.FileName);
    }

    // --- Dashboard banner: officer-uploaded image shown atop the dashboard
    // (web + Activity). Stored in the LinkshellBanner table; served anonymously
    // by ActivityDataController.GetLinkshellBanner so <img> works in the iframe. ---

    private static readonly Dictionary<string, string> BannerContentTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        [".png"] = "image/png",
        [".jpg"] = "image/jpeg",
        [".jpeg"] = "image/jpeg",
        [".webp"] = "image/webp",
        [".gif"] = "image/gif",
    };

    [HttpPost]
    [ValidateAntiForgeryToken]
    [RequestSizeLimit(5_000_000)]
    [RequestFormLimits(MultipartBodyLengthLimit = 5_000_000)]
    public async Task<IActionResult> UploadBanner(int linkshellId, IFormFile? bannerFile, CancellationToken cancellationToken)
    {
        var user = await _userManager.GetUserAsync(User);
        if (user is null)
        {
            return Challenge();
        }

        var membership = await GetMembershipAsync(user.Id, linkshellId);
        if (!await CanManageLinkshellAsync(membership))
        {
            return Forbid();
        }

        if (bannerFile is null || bannerFile.Length == 0)
        {
            TempData["CustomizeError"] = "Choose an image to upload.";
            return RedirectToAction(nameof(Customize), new { id = linkshellId });
        }

        var ext = System.IO.Path.GetExtension(bannerFile.FileName);
        if (string.IsNullOrEmpty(ext) || !BannerContentTypes.TryGetValue(ext, out var contentType))
        {
            TempData["CustomizeError"] = "Unsupported image type. Use PNG, JPG, WEBP, or GIF.";
            return RedirectToAction(nameof(Customize), new { id = linkshellId });
        }

        byte[] bytes;
        await using (var stream = bannerFile.OpenReadStream())
        await using (var buffer = new MemoryStream())
        {
            await stream.CopyToAsync(buffer, cancellationToken);
            bytes = buffer.ToArray();
        }

        // Magic-byte sanity check so a renamed non-image can't be stored.
        if (!LooksLikeImage(bytes))
        {
            TempData["CustomizeError"] = "That file doesn't look like a valid image.";
            return RedirectToAction(nameof(Customize), new { id = linkshellId });
        }

        var banner = await _context.LinkshellBanners.FirstOrDefaultAsync(b => b.LinkshellId == linkshellId, cancellationToken);
        if (banner is null)
        {
            banner = new LinkshellBanner { LinkshellId = linkshellId };
            _context.LinkshellBanners.Add(banner);
        }
        banner.ImageData = bytes;
        banner.ContentType = contentType;
        banner.UpdatedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync(cancellationToken);

        TempData["CustomizeSaved"] = "Banner updated.";
        return RedirectToAction(nameof(Customize), new { id = linkshellId });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> RemoveBanner(int linkshellId, CancellationToken cancellationToken)
    {
        var user = await _userManager.GetUserAsync(User);
        if (user is null)
        {
            return Challenge();
        }

        var membership = await GetMembershipAsync(user.Id, linkshellId);
        if (!await CanManageLinkshellAsync(membership))
        {
            return Forbid();
        }

        var banner = await _context.LinkshellBanners.FirstOrDefaultAsync(b => b.LinkshellId == linkshellId, cancellationToken);
        if (banner is not null)
        {
            _context.LinkshellBanners.Remove(banner);
            await _context.SaveChangesAsync(cancellationToken);
        }

        TempData["CustomizeSaved"] = "Banner removed.";
        return RedirectToAction(nameof(Customize), new { id = linkshellId });
    }

    // Lightweight magic-byte check for the formats we accept (PNG/JPEG/WEBP/GIF).
    private static bool LooksLikeImage(byte[] b)
    {
        if (b.Length < 12) { return false; }
        // PNG
        if (b[0] == 0x89 && b[1] == 0x50 && b[2] == 0x4E && b[3] == 0x47) { return true; }
        // JPEG
        if (b[0] == 0xFF && b[1] == 0xD8 && b[2] == 0xFF) { return true; }
        // GIF
        if (b[0] == (byte)'G' && b[1] == (byte)'I' && b[2] == (byte)'F') { return true; }
        // WEBP: "RIFF"...."WEBP"
        if (b[0] == (byte)'R' && b[1] == (byte)'I' && b[2] == (byte)'F' && b[3] == (byte)'F'
            && b[8] == (byte)'W' && b[9] == (byte)'E' && b[10] == (byte)'B' && b[11] == (byte)'P') { return true; }
        return false;
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    [RequestSizeLimit(8_000_000)]
    [RequestFormLimits(MultipartBodyLengthLimit = 8_000_000)]
    public async Task<IActionResult> PreviewDkpImport(
        int linkshellId,
        IFormFile? importFile,
        [FromServices] DkpImportService import,
        CancellationToken cancellationToken)
    {
        var user = await _userManager.GetUserAsync(User);
        if (user is null)
        {
            return Challenge();
        }

        var membership = await GetMembershipAsync(user.Id, linkshellId);
        if (!await CanManageLinkshellAsync(membership))
        {
            return Forbid();
        }

        var linkshell = await _context.Linkshells.FindAsync(linkshellId);
        if (linkshell is null)
        {
            return NotFound();
        }

        if (importFile is null || importFile.Length == 0)
        {
            TempData["CustomizeError"] = "Choose an .xlsx or .csv file to import.";
            return RedirectToAction(nameof(Customize), new { id = linkshellId });
        }

        List<DkpImportRow> parsed;
        try
        {
            await using var stream = importFile.OpenReadStream();
            var grid = XlsxImportReader.Read(stream, importFile.FileName);
            parsed = import.ParseImport(grid);
        }
        catch (Exception ex)
        {
            TempData["CustomizeError"] = $"Could not read the file: {ex.Message}";
            return RedirectToAction(nameof(Customize), new { id = linkshellId });
        }

        if (parsed.Count == 0)
        {
            TempData["CustomizeError"] = "No member rows found. Make sure the sheet has a Member Name column and Current DKP values.";
            return RedirectToAction(nameof(Customize), new { id = linkshellId });
        }

        var preview = await import.BuildPreviewAsync(linkshellId, parsed, importFile.FileName, cancellationToken);
        return View("DkpImportPreview", new DkpImportPreviewViewModel
        {
            LinkshellId = linkshellId,
            LinkshellName = linkshell.LinkshellName ?? "Linkshell",
            Preview = preview,
            RowsJson = System.Text.Json.JsonSerializer.Serialize(parsed),
        });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> CommitDkpImport(
        int linkshellId,
        string rowsJson,
        string? sourceLabel,
        [FromServices] DkpImportService import,
        CancellationToken cancellationToken)
    {
        var user = await _userManager.GetUserAsync(User);
        if (user is null)
        {
            return Challenge();
        }

        var membership = await GetMembershipAsync(user.Id, linkshellId);
        if (!await CanManageLinkshellAsync(membership))
        {
            return Forbid();
        }

        List<DkpImportRow>? parsed;
        try
        {
            parsed = System.Text.Json.JsonSerializer.Deserialize<List<DkpImportRow>>(rowsJson ?? string.Empty);
        }
        catch
        {
            parsed = null;
        }
        if (parsed is null || parsed.Count == 0)
        {
            TempData["CustomizeError"] = "The import session expired — please re-upload the file.";
            return RedirectToAction(nameof(Customize), new { id = linkshellId });
        }

        var result = await import.CommitAsync(linkshellId, parsed, sourceLabel ?? "DKP import", cancellationToken);

        var parts = new List<string>();
        if (result.Updated > 0) { parts.Add($"{result.Updated} updated"); }
        if (result.Created > 0) { parts.Add($"{result.Created} new member{(result.Created == 1 ? "" : "s")} created"); }
        if (result.Ambiguous > 0) { parts.Add($"{result.Ambiguous} skipped (ambiguous)"); }
        TempData["CustomizeSaved"] = parts.Count > 0
            ? $"DKP import complete — {string.Join(", ", parts)}."
            : "DKP import complete — no changes were needed.";
        return RedirectToAction(nameof(Customize), new { id = linkshellId });
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
        if (!await CanManageLinkshellAsync(membership))
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
        if (!await CanManageLinkshellAsync(membership))
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
        if (!await CanManageLinkshellAsync(membership)) return Forbid();

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
        if (!await CanManageLinkshellAsync(membership))
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
                PostDkpSheet = route.PostDkpSheet,
                EventTypeFilter = (route.EventTypeFilter ?? string.Empty)
                    .Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .ToList(),
                HnmMonsterFilter = (route.HnmMonsterFilter ?? string.Empty)
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
        if (!await CanManageLinkshellAsync(membership))
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

        // The web form now carries the same per-monster HNM picker as the Activity, so the
        // posted filter is authoritative (empty = every HNM goes to this route).
        // ChannelRouteEditor canonicalises the names against the linkshell's catalog.
        var edits = (channelRoutes ?? new List<ChannelRouteInput>())
            .Select(r => new Services.ChannelRouteEdit(
                r.Id == 0 ? null : r.Id, r.Name, r.ChannelId,
                r.PostEvents, r.PostLoot, r.PostAuctions, r.PostAttendance, r.PostTodBoard, r.PostDkpSheet,
                r.EventTypeFilter,
                r.HnmMonsterFilter))
            .ToList();

        var error = await _channelRoutes.SaveAsync(linkshellId, edits, available, HttpContext.RequestAborted);
        TempData[error is null ? "CustomizeSaved" : "CustomizeError"] =
            error ?? "Discord channels updated.";
        return RedirectToAction(nameof(Customize), new { id = linkshellId });
    }

    // Save the DKP pools + the event-type partition. Both this and the Activity's
    // POST /linkshells/{id}/dkp-pools converge on DkpPoolEditor, so there is one implementation of
    // the save — and one implementation of the ledger re-stamp that re-derives every unpinned row's
    // pool from the new mapping.
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SaveDkpPools(
        int linkshellId,
        [FromForm] List<DkpPoolInput>? dkpPools,
        [FromForm] List<DkpPoolAssignmentInput>? dkpPoolAssignments,
        // The default-pool radios share one name and post the winning row's INDEX, so the browser
        // guarantees exactly one default. A per-row checkbox could be ticked twice.
        [FromForm] int dkpPoolDefaultIndex = 0)
    {
        var user = await _userManager.GetUserAsync(User);
        if (user is null)
        {
            return Challenge();
        }

        var membership = await GetMembershipAsync(user.Id, linkshellId);
        if (!await CanManageLinkshellAsync(membership))
        {
            return Forbid();
        }

        var pools = dkpPools ?? new List<DkpPoolInput>();
        var assignments = dkpPoolAssignments ?? new List<DkpPoolAssignmentInput>();

        // Invert the per-event-type <select> back into "which event types does each pool own".
        // Because each event type has exactly ONE select, the partition is guaranteed by the form's
        // shape — there is no way for the officer to double-assign one.
        var typesByPoolIndex = new Dictionary<int, List<string>>();
        foreach (var assignment in assignments)
        {
            if (assignment.PoolIndex < 0
                || assignment.PoolIndex >= pools.Count
                || string.IsNullOrWhiteSpace(assignment.EventType))
            {
                continue; // Unassigned → falls through to the default pool.
            }
            if (!typesByPoolIndex.TryGetValue(assignment.PoolIndex, out var list))
            {
                list = new List<string>();
                typesByPoolIndex[assignment.PoolIndex] = list;
            }
            list.Add(assignment.EventType.Trim());
        }

        var edits = pools
            .Select((pool, index) => new Services.DkpPoolEdit(
                pool.Id == 0 ? null : pool.Id,
                pool.Name,
                index == dkpPoolDefaultIndex,
                index,
                pool.Accent,
                typesByPoolIndex.GetValueOrDefault(index) ?? new List<string>()))
            .ToList();

        var error = await _dkpPoolEditor.SaveAsync(linkshellId, edits, HttpContext.RequestAborted);
        TempData[error is null ? "CustomizeSaved" : "CustomizeError"] =
            error ?? "DKP pools updated.";
        return RedirectToAction(nameof(Customize), new { id = linkshellId });
    }

    // The web half of the Monster Setups card. Converges on the SAME MonsterTimingEditor the
    // Activity endpoint uses, so there is exactly one implementation of "save the monster setups"
    // and the two surfaces cannot validate differently.
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SaveMonsterTimings(
        int linkshellId,
        [FromForm] List<MonsterTimingInput>? monsterTimings)
    {
        var user = await _userManager.GetUserAsync(User);
        if (user is null)
        {
            return Challenge();
        }

        var membership = await GetMembershipAsync(user.Id, linkshellId);
        if (!await CanManageLinkshellAsync(membership))
        {
            return Forbid();
        }

        var edits = (monsterTimings ?? new List<MonsterTimingInput>())
            .Select(row => new Services.MonsterTimingEdit(
                row.Id == 0 ? null : row.Id,
                row.MonsterName,
                row.Windows,
                row.CadenceValue,
                row.CadenceUnit,
                row.CooldownValue,
                row.CooldownUnit,
                row.Category,
                // The web form posts a real checkbox for every row, so a bound false here means
                // "unticked" rather than "not sent" — pass it through as an explicit value.
                row.ClaimShieldEnabled))
            .ToList();

        var error = await _monsterTimingEditor.SaveAsync(linkshellId, edits, HttpContext.RequestAborted);
        TempData[error is null ? "CustomizeSaved" : "CustomizeError"] =
            error ?? "Monster setups updated.";
        return RedirectToAction(nameof(Customize), new { id = linkshellId });
    }

    private static LinkshellCustomizeViewModel BuildCustomizeViewModel(
        Linkshell target, IReadOnlyList<Linkshell> manageableLinkshells, bool canManageRoles) =>
        new()
        {
            LinkshellId           = target.Id,
            LinkshellName         = target.LinkshellName,
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
            OutsidePartySignupEnabled = target.OutsidePartySignupEnabled,
            UseComponentsV2Boards = target.UseComponentsV2Boards,
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

    // The named-permission check against a preloaded set of role rows. The membership
    // is what scopes it — a null membership never resolves, so the admin override
    // below cannot reach a linkshell the user has not joined.
    private async Task<bool> CanRoleAsync(
        IReadOnlyList<LinkshellRole> roles,
        AppUserLinkshell? membership,
        Func<LinkshellRole, bool> selector)
    {
        if (membership is null) return false;
        if (await _adminOverride.IsActiveForAsync(membership.AppUserId, HttpContext.RequestAborted))
        {
            return true;
        }

        var rank = membership.Rank;
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

    // Both gates below reject a null membership first, then fall through to the
    // app-wide admin override. See AdminOverrideService for why that order matters.
    private async Task<bool> CanManageLinkshellAsync(AppUserLinkshell? membership)
    {
        if (membership is null) return false;
        return LinkshellRanks.IsLeaderOrOfficer(membership.Rank)
               || await _adminOverride.IsActiveForAsync(membership.AppUserId, HttpContext.RequestAborted);
    }

    private async Task<bool> IsLeaderAsync(AppUserLinkshell? membership)
    {
        if (membership is null) return false;
        return membership.Rank?.Equals("Leader", StringComparison.OrdinalIgnoreCase) == true
               || await _adminOverride.IsActiveForAsync(membership.AppUserId, HttpContext.RequestAborted);
    }

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

