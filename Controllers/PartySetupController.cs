using LinkshellManagerDiscordApp.Data;
using LinkshellManagerDiscordApp.Models;
using LinkshellManagerDiscordApp.Utils;
using LinkshellManagerDiscordApp.ViewModels;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace LinkshellManagerDiscordApp.Controllers;

// Officer-designed raid-composition planner that lives inside the ToD Tracker.
// All linkshell members can VIEW setups (Index/Details); creating, editing,
// deleting and assigning require the CanManageParties role flag.
[Authorize]
public class PartySetupController : Controller
{
    private static readonly HashSet<string> SupportedMonsters =
        new(TodManagerViewModel.SupportedMonsters, StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> ValidRoles =
        new(EventJobCatalog.JobTypeOptions, StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> ValidMainJobs =
        new(EventJobCatalog.MainJobOptions, StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> ValidSubJobs =
        new(EventJobCatalog.SubJobOptions, StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> ValidRequirementTypes =
        new(PartySetupSlotRequirementTypes.All, StringComparer.OrdinalIgnoreCase);

    private readonly ApplicationDbContext _context;
    private readonly UserManager<AppUser> _userManager;

    public PartySetupController(ApplicationDbContext context, UserManager<AppUser> userManager)
    {
        _context = context;
        _userManager = userManager;
    }

    [HttpGet]
    public async Task<IActionResult> Index()
    {
        var user = await RequireCurrentUserAsync();
        if (user is null) return Challenge();

        var linkshellId = await ResolveActiveLinkshellIdAsync(user);
        var linkshellName = linkshellId > 0
            ? await _context.Linkshells.AsNoTracking()
                .Where(ls => ls.Id == linkshellId)
                .Select(ls => ls.LinkshellName)
                .FirstOrDefaultAsync()
            : null;

        var items = linkshellId > 0
            ? await _context.PartySetups.AsNoTracking()
                .Where(ps => ps.LinkshellId == linkshellId)
                .OrderByDescending(ps => ps.UpdatedAt)
                .Select(ps => new PartySetupListRow
                {
                    Id = ps.Id,
                    Name = ps.Name,
                    AssignedMonsterName = ps.AssignedMonsterName,
                    AllianceCount = ps.Alliances.Count,
                    PartyCount = ps.Alliances.SelectMany(a => a.Parties).Count(),
                    SlotCount = ps.Alliances.SelectMany(a => a.Parties).SelectMany(p => p.Slots).Count(),
                    UpdatedAt = ps.UpdatedAt
                })
                .ToListAsync()
            : new List<PartySetupListRow>();

        return View(new PartySetupIndexViewModel
        {
            LinkshellId = linkshellId,
            LinkshellName = linkshellName,
            CanManage = linkshellId > 0 && await ResolveCanManagePartiesAsync(user.Id, linkshellId),
            Items = items
        });
    }

    [HttpGet]
    public async Task<IActionResult> Details(int id)
    {
        var user = await RequireCurrentUserAsync();
        if (user is null) return Challenge();

        var partySetup = await LoadTreeAsync(id);
        if (partySetup is null) return NotFound();
        if (!await HasLinkshellAccessAsync(user.Id, partySetup.LinkshellId)) return Forbid();

        return View(new PartySetupDetailsViewModel
        {
            Id = partySetup.Id,
            LinkshellId = partySetup.LinkshellId,
            Name = partySetup.Name,
            AssignedMonsterName = partySetup.AssignedMonsterName,
            Notes = partySetup.Notes,
            CanManage = await ResolveCanManagePartiesAsync(user.Id, partySetup.LinkshellId),
            Alliances = partySetup.Alliances
                .OrderBy(a => a.SortOrder)
                .Select(a => new PartySetupAllianceView
                {
                    Name = string.IsNullOrWhiteSpace(a.Name) ? $"Alliance {a.SortOrder + 1}" : a.Name,
                    Parties = a.Parties
                        .OrderBy(p => p.SortOrder)
                        .Select(p => new PartySetupPartyView
                        {
                            Name = string.IsNullOrWhiteSpace(p.Name) ? $"Party {p.SortOrder + 1}" : p.Name!,
                            Slots = p.Slots
                                .OrderBy(s => s.SortOrder)
                                .Select(s => new PartySetupSlotView
                                {
                                    Position = s.SortOrder + 1,
                                    RequirementType = s.RequirementType,
                                    Role = s.Role,
                                    MainJob = s.MainJob,
                                    SubJob = s.SubJob,
                                    Label = s.Label,
                                    IsPartyLeader = s.IsPartyLeader
                                }).ToList()
                        }).ToList()
                }).ToList()
        });
    }

    // Member self-service: claim an open slot in an assigned setup from the
    // ToD Tracker's inline panel. Any linkshell member may sign up; you hold
    // at most one slot per setup (signing up moves you off any other slot in
    // the same setup so you can switch roles cleanly). `id` = PartySetup id,
    // `slotId` = PartySetupSlot id.
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SignUp(int id, int slotId)
    {
        var user = await RequireCurrentUserAsync();
        if (user is null) return Challenge();

        var slot = await _context.PartySetupSlots
            .Include(s => s.Party!).ThenInclude(p => p.Alliance!).ThenInclude(a => a.PartySetup!)
            .FirstOrDefaultAsync(s => s.Id == slotId);
        var setup = slot?.Party?.Alliance?.PartySetup;
        if (slot is null || setup is null || setup.Id != id) return NotFound();
        if (!await HasLinkshellAccessAsync(user.Id, setup.LinkshellId)) return Forbid();

        if (!string.IsNullOrEmpty(slot.SignedUpAppUserId) && slot.SignedUpAppUserId != user.Id)
        {
            TempData["PartySetupMessage"] =
                $"That slot was just taken by {slot.SignedUpCharacterName ?? "another member"}.";
            return RedirectBack(setup.Id);
        }

        // Snapshot the member's character name in this linkshell (fall back to
        // their profile name) so the panel renders without extra joins.
        var characterName = await _context.AppUserLinkshells
            .Where(l => l.AppUserId == user.Id && l.LinkshellId == setup.LinkshellId)
            .Select(l => l.CharacterName)
            .FirstOrDefaultAsync();
        if (string.IsNullOrWhiteSpace(characterName))
        {
            characterName = user.CharacterName ?? user.UserName ?? "Member";
        }

        // One slot per setup: release any other slot in this setup the member
        // currently holds before claiming the new one.
        var heldElsewhere = await _context.PartySetupSlots
            .Where(s => s.SignedUpAppUserId == user.Id
                        && s.Party!.Alliance!.PartySetupId == setup.Id
                        && s.Id != slot.Id)
            .ToListAsync();
        foreach (var held in heldElsewhere)
        {
            held.SignedUpAppUserId = null;
            held.SignedUpCharacterName = null;
            held.SignedUpAtUtc = null;
        }

        slot.SignedUpAppUserId = user.Id;
        slot.SignedUpCharacterName = characterName;
        slot.SignedUpAtUtc = DateTime.UtcNow;
        await _context.SaveChangesAsync();

        TempData["PartySetupMessage"] = $"Signed up as {characterName} for {setup.Name}.";
        return RedirectBack(setup.Id);
    }

    // Release a slot. The member who holds it can withdraw themselves; an
    // officer with CanManageParties can clear anyone's sign-up.
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Withdraw(int id, int slotId)
    {
        var user = await RequireCurrentUserAsync();
        if (user is null) return Challenge();

        var slot = await _context.PartySetupSlots
            .Include(s => s.Party!).ThenInclude(p => p.Alliance!).ThenInclude(a => a.PartySetup!)
            .FirstOrDefaultAsync(s => s.Id == slotId);
        var setup = slot?.Party?.Alliance?.PartySetup;
        if (slot is null || setup is null || setup.Id != id) return NotFound();
        if (!await HasLinkshellAccessAsync(user.Id, setup.LinkshellId)) return Forbid();

        var isHolder = slot.SignedUpAppUserId == user.Id;
        if (!isHolder && !await ResolveCanManagePartiesAsync(user.Id, setup.LinkshellId))
        {
            return Forbid();
        }

        slot.SignedUpAppUserId = null;
        slot.SignedUpCharacterName = null;
        slot.SignedUpAtUtc = null;
        await _context.SaveChangesAsync();

        TempData["PartySetupMessage"] = "Slot is open again.";
        return RedirectBack(setup.Id);
    }

    // Return to the ToD Tracker with a fragment so the JS re-opens that
    // setup's inline panel (it's collapsed by default after a full reload).
    private IActionResult RedirectBack(int setupId)
    {
        return Redirect($"{Url.Action("Index", "Tod")}#tod-setup-{setupId}");
    }

    [HttpGet]
    public async Task<IActionResult> Create()
    {
        var user = await RequireCurrentUserAsync();
        if (user is null) return Challenge();

        var linkshellId = await ResolveActiveLinkshellIdAsync(user);
        if (linkshellId <= 0 || !await ResolveCanManagePartiesAsync(user.Id, linkshellId)) return Forbid();

        var model = new PartySetupEditorViewModel
        {
            LinkshellId = linkshellId,
            LinkshellName = await _context.Linkshells.AsNoTracking()
                .Where(ls => ls.Id == linkshellId)
                .Select(ls => ls.LinkshellName)
                .FirstOrDefaultAsync(),
            Slots = SeedSlots()
        };
        return View(model);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(PartySetupEditorViewModel model)
    {
        var user = await RequireCurrentUserAsync();
        if (user is null) return Challenge();

        var linkshellId = await ResolveActiveLinkshellIdAsync(user);
        if (linkshellId <= 0 || !await ResolveCanManagePartiesAsync(user.Id, linkshellId)) return Forbid();

        NormalizeAndValidate(model);
        if (!ModelState.IsValid)
        {
            model.LinkshellId = linkshellId;
            return View(model);
        }

        var characterName = await _context.AppUserLinkshells.AsNoTracking()
            .Where(link => link.AppUserId == user.Id && link.LinkshellId == linkshellId)
            .Select(link => link.CharacterName)
            .FirstOrDefaultAsync();

        var now = DateTime.UtcNow;
        var partySetup = new PartySetup
        {
            LinkshellId = linkshellId,
            Name = model.Name.Trim(),
            AssignedMonsterName = NormalizeMonster(model.AssignedMonsterName),
            Notes = string.IsNullOrWhiteSpace(model.Notes) ? null : model.Notes.Trim(),
            CreatedByAppUserId = user.Id,
            CreatedByCharacterName = characterName,
            CreatedAt = now,
            UpdatedAt = now,
            Alliances = BuildTreeFromFlat(model.Slots)
        };

        _context.PartySetups.Add(partySetup);
        await _context.SaveChangesAsync();

        TempData["PartySetupMessage"] = $"Party setup \"{partySetup.Name}\" created.";
        return RedirectToAction(nameof(Index));
    }

    [HttpGet]
    public async Task<IActionResult> Edit(int id)
    {
        var user = await RequireCurrentUserAsync();
        if (user is null) return Challenge();

        var partySetup = await LoadTreeAsync(id);
        if (partySetup is null) return NotFound();
        if (!await HasLinkshellAccessAsync(user.Id, partySetup.LinkshellId)) return Forbid();
        if (!await ResolveCanManagePartiesAsync(user.Id, partySetup.LinkshellId)) return Forbid();

        var model = new PartySetupEditorViewModel
        {
            Id = partySetup.Id,
            LinkshellId = partySetup.LinkshellId,
            LinkshellName = await _context.Linkshells.AsNoTracking()
                .Where(ls => ls.Id == partySetup.LinkshellId)
                .Select(ls => ls.LinkshellName)
                .FirstOrDefaultAsync(),
            Name = partySetup.Name,
            AssignedMonsterName = partySetup.AssignedMonsterName,
            Notes = partySetup.Notes,
            Slots = FlattenTree(partySetup)
        };
        return View(nameof(Create), model);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Edit(int id, PartySetupEditorViewModel model)
    {
        var user = await RequireCurrentUserAsync();
        if (user is null) return Challenge();

        var partySetup = await _context.PartySetups
            .Include(ps => ps.Alliances)
            .FirstOrDefaultAsync(ps => ps.Id == id);
        if (partySetup is null) return NotFound();
        if (!await HasLinkshellAccessAsync(user.Id, partySetup.LinkshellId)) return Forbid();
        if (!await ResolveCanManagePartiesAsync(user.Id, partySetup.LinkshellId)) return Forbid();

        NormalizeAndValidate(model);
        if (!ModelState.IsValid)
        {
            model.Id = partySetup.Id;
            model.LinkshellId = partySetup.LinkshellId;
            return View(nameof(Create), model);
        }

        // Replace the whole tree (mirrors TodController.Edit removing old loot
        // then adding the new set). Cascade delete clears parties/slots.
        _context.PartySetupAlliances.RemoveRange(partySetup.Alliances);

        partySetup.Name = model.Name.Trim();
        partySetup.AssignedMonsterName = NormalizeMonster(model.AssignedMonsterName);
        partySetup.Notes = string.IsNullOrWhiteSpace(model.Notes) ? null : model.Notes.Trim();
        partySetup.UpdatedAt = DateTime.UtcNow;
        partySetup.Alliances = BuildTreeFromFlat(model.Slots);

        await _context.SaveChangesAsync();

        TempData["PartySetupMessage"] = $"Party setup \"{partySetup.Name}\" updated.";
        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Delete(int id)
    {
        var user = await RequireCurrentUserAsync();
        if (user is null) return Challenge();

        var partySetup = await _context.PartySetups.FirstOrDefaultAsync(ps => ps.Id == id);
        if (partySetup is null) return NotFound();
        if (!await HasLinkshellAccessAsync(user.Id, partySetup.LinkshellId)) return Forbid();
        if (!await ResolveCanManagePartiesAsync(user.Id, partySetup.LinkshellId)) return Forbid();

        _context.PartySetups.Remove(partySetup);
        await _context.SaveChangesAsync();

        TempData["PartySetupMessage"] = $"Party setup \"{partySetup.Name}\" deleted.";
        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Assign(int id, string? monsterName)
    {
        var user = await RequireCurrentUserAsync();
        if (user is null) return Challenge();

        var partySetup = await _context.PartySetups.FirstOrDefaultAsync(ps => ps.Id == id);
        if (partySetup is null) return NotFound();
        if (!await HasLinkshellAccessAsync(user.Id, partySetup.LinkshellId)) return Forbid();
        if (!await ResolveCanManagePartiesAsync(user.Id, partySetup.LinkshellId)) return Forbid();

        var trimmed = monsterName?.Trim();
        if (!string.IsNullOrEmpty(trimmed) && !SupportedMonsters.Contains(trimmed))
        {
            TempData["PartySetupMessage"] = "That monster is not a supported ToD monster.";
            return RedirectToAction(nameof(Index));
        }

        partySetup.AssignedMonsterName = string.IsNullOrEmpty(trimmed) ? null : trimmed;
        partySetup.UpdatedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync();

        TempData["PartySetupMessage"] = string.IsNullOrEmpty(trimmed)
            ? $"Cleared monster assignment for \"{partySetup.Name}\"."
            : $"Assigned \"{partySetup.Name}\" to {trimmed}.";
        return RedirectToAction(nameof(Index));
    }

    // --- Helpers ---

    private async Task<PartySetup?> LoadTreeAsync(int id)
    {
        return await _context.PartySetups
            .AsNoTracking()
            .Include(ps => ps.Alliances).ThenInclude(a => a.Parties).ThenInclude(p => p.Slots)
            .FirstOrDefaultAsync(ps => ps.Id == id);
    }

    // A brand-new setup starts with an empty Alliance 1 / Party 1 (no slots);
    // the editor scaffolds that shell client-side and the user adds slots on
    // demand. No seed rows are posted until the user clicks "Add slot".
    private static List<PartySetupSlotInput> SeedSlots() => new();

    private static List<PartySetupSlotInput> FlattenTree(PartySetup partySetup)
    {
        var slots = new List<PartySetupSlotInput>();
        var alliances = partySetup.Alliances.OrderBy(a => a.SortOrder).ToList();
        for (var ai = 0; ai < alliances.Count; ai++)
        {
            var alliance = alliances[ai];
            var parties = alliance.Parties.OrderBy(p => p.SortOrder).ToList();
            for (var pi = 0; pi < parties.Count; pi++)
            {
                var party = parties[pi];
                var partySlots = party.Slots.OrderBy(s => s.SortOrder).ToList();
                for (var si = 0; si < partySlots.Count; si++)
                {
                    var slot = partySlots[si];
                    slots.Add(new PartySetupSlotInput
                    {
                        AllianceIndex = ai,
                        PartyIndex = pi,
                        SlotIndex = si,
                        AllianceName = string.IsNullOrWhiteSpace(alliance.Name) ? $"Alliance {ai + 1}" : alliance.Name,
                        PartyName = string.IsNullOrWhiteSpace(party.Name) ? $"Party {pi + 1}" : party.Name,
                        RequirementType = slot.RequirementType,
                        Role = slot.Role,
                        MainJob = slot.MainJob,
                        SubJob = slot.SubJob,
                        Label = slot.Label,
                        IsPartyLeader = slot.IsPartyLeader
                    });
                }
            }
        }
        return slots;
    }

    // Rebuild the persisted Alliance -> Party -> Slot tree from the flat
    // posted slot list. Grouping by the per-row Alliance/Party indices keeps
    // the editor binding to a single contiguous Slots[] collection (the
    // proven TodLootDetails[i] pattern) while still storing a clean tree.
    private static List<PartySetupAlliance> BuildTreeFromFlat(IEnumerable<PartySetupSlotInput> slots)
    {
        var alliances = new List<PartySetupAlliance>();
        var allianceGroups = slots
            .GroupBy(s => s.AllianceIndex)
            .OrderBy(g => g.Key)
            .ToList();

        var allianceSort = 0;
        foreach (var allianceGroup in allianceGroups)
        {
            var first = allianceGroup.First();
            var alliance = new PartySetupAlliance
            {
                SortOrder = allianceSort,
                Name = string.IsNullOrWhiteSpace(first.AllianceName)
                    ? $"Alliance {allianceSort + 1}"
                    : first.AllianceName!.Trim()
            };

            var partySort = 0;
            foreach (var partyGroup in allianceGroup.GroupBy(s => s.PartyIndex).OrderBy(g => g.Key))
            {
                var firstParty = partyGroup.First();
                var party = new PartySetupParty
                {
                    SortOrder = partySort,
                    Name = string.IsNullOrWhiteSpace(firstParty.PartyName)
                        ? $"Party {partySort + 1}"
                        : firstParty.PartyName!.Trim()
                };

                var slotSort = 0;
                foreach (var slot in partyGroup.OrderBy(s => s.SlotIndex))
                {
                    party.Slots.Add(MapSlot(slot, slotSort));
                    slotSort++;
                }

                // At most one leader per party: keep the first flagged slot
                // (by display order), clear any extras a crafted post slipped in.
                var leaderSeen = false;
                foreach (var s in party.Slots.OrderBy(s => s.SortOrder))
                {
                    if (!s.IsPartyLeader) continue;
                    if (leaderSeen) s.IsPartyLeader = false;
                    else leaderSeen = true;
                }

                alliance.Parties.Add(party);
                partySort++;
            }

            alliances.Add(alliance);
            allianceSort++;
        }

        return alliances;
    }

    // Each slot exposes Role + Main job + Sub job dropdowns. The stored
    // requirement is derived by precedence: a specific main job => Job
    // (with optional sub job); else a generic role => Role; else Any.
    // Invalid/blank picks fall through safely. Label is unused by this UI.
    private static PartySetupSlot MapSlot(PartySetupSlotInput input, int sortOrder)
    {
        var role = input.Role?.Trim();
        var mainJob = input.MainJob?.Trim();
        var subJob = input.SubJob?.Trim();

        // "Any Role" is the editor's explicit "anything goes" sentinel. It is
        // not a real role (not in ValidRoles) — store it as an open Any slot
        // with no role/job constraints rather than a Role requirement.
        if (string.Equals(role, "Any Role", StringComparison.OrdinalIgnoreCase))
        {
            role = null;
        }

        var hasMainJob = !string.IsNullOrWhiteSpace(mainJob) && ValidMainJobs.Contains(mainJob!);
        var hasRole = !string.IsNullOrWhiteSpace(role) && ValidRoles.Contains(role!);

        var slot = new PartySetupSlot
        {
            SortOrder = sortOrder,
            Label = null,
            IsPartyLeader = input.IsPartyLeader
        };

        // The three dropdowns (Role / Main job / Sub job) are independent: a
        // slot can be e.g. "Tank + PLD". Persist the picked Role whenever it's
        // valid, regardless of whether a job is also chosen, so it round-trips
        // back into the editor. RequirementType is still derived by precedence
        // (Job > Role > Any) for the read-only Details requirement string.
        if (hasRole)
        {
            slot.Role = role;
        }

        if (hasMainJob)
        {
            slot.RequirementType = PartySetupSlotRequirementTypes.Job;
            slot.MainJob = mainJob;
            slot.SubJob = (!string.IsNullOrWhiteSpace(subJob) && ValidSubJobs.Contains(subJob!))
                ? subJob
                : null;
        }
        else if (hasRole)
        {
            slot.RequirementType = PartySetupSlotRequirementTypes.Role;
        }
        else
        {
            slot.RequirementType = PartySetupSlotRequirementTypes.Any;
        }

        return slot;
    }

    private void NormalizeAndValidate(PartySetupEditorViewModel model)
    {
        if (string.IsNullOrWhiteSpace(model.Name))
        {
            ModelState.AddModelError(nameof(model.Name), "A name is required.");
        }

        var monster = model.AssignedMonsterName?.Trim();
        if (!string.IsNullOrEmpty(monster) && !SupportedMonsters.Contains(monster))
        {
            ModelState.AddModelError(nameof(model.AssignedMonsterName), "Select a supported ToD monster, or leave it unassigned.");
        }

        model.Slots ??= new List<PartySetupSlotInput>();
        if (model.Slots.Count == 0)
        {
            ModelState.AddModelError(string.Empty, "Add at least one alliance with a party and a slot.");
            return;
        }

        // FFXI alliances are at most 3 parties. The editor enforces this in
        // the UI; guard server-side too so a crafted post can't exceed it.
        var overfilledAlliance = model.Slots
            .GroupBy(s => s.AllianceIndex)
            .Any(g => g.Select(s => s.PartyIndex).Distinct().Count() > 3);
        if (overfilledAlliance)
        {
            ModelState.AddModelError(string.Empty, "An alliance can have at most 3 parties.");
            return;
        }

        // An FFXI party is at most 6 slots. The editor caps this in the UI;
        // guard server-side too so a crafted post can't exceed it.
        var overfilledParty = model.Slots
            .GroupBy(s => new { s.AllianceIndex, s.PartyIndex })
            .Any(g => g.Count() > 6);
        if (overfilledParty)
        {
            ModelState.AddModelError(string.Empty, "A party can have at most 6 slots.");
            return;
        }

        for (var i = 0; i < model.Slots.Count; i++)
        {
            var slot = model.Slots[i];
            var requirement = slot.RequirementType?.Trim() ?? string.Empty;
            if (!ValidRequirementTypes.Contains(requirement))
            {
                ModelState.AddModelError($"Slots[{i}].RequirementType", "Choose Any, Role, or Job.");
                continue;
            }

            if (string.Equals(requirement, PartySetupSlotRequirementTypes.Role, StringComparison.OrdinalIgnoreCase))
            {
                if (string.IsNullOrWhiteSpace(slot.Role) || !ValidRoles.Contains(slot.Role.Trim()))
                {
                    ModelState.AddModelError($"Slots[{i}].Role", "Pick a role (Tank/Heal/Support/DPS).");
                }
            }
            else if (string.Equals(requirement, PartySetupSlotRequirementTypes.Job, StringComparison.OrdinalIgnoreCase))
            {
                if (string.IsNullOrWhiteSpace(slot.MainJob) || !ValidMainJobs.Contains(slot.MainJob.Trim()))
                {
                    ModelState.AddModelError($"Slots[{i}].MainJob", "Pick a main job.");
                }
                if (!string.IsNullOrWhiteSpace(slot.SubJob) && !ValidSubJobs.Contains(slot.SubJob.Trim()))
                {
                    ModelState.AddModelError($"Slots[{i}].SubJob", "Pick a valid subjob, or leave it blank.");
                }
            }
        }
    }

    private static string? NormalizeMonster(string? monsterName)
    {
        var trimmed = monsterName?.Trim();
        return string.IsNullOrEmpty(trimmed) ? null : trimmed;
    }

    private async Task<AppUser?> RequireCurrentUserAsync()
    {
        return await _userManager.GetUserAsync(User);
    }

    private async Task<bool> HasLinkshellAccessAsync(string userId, int linkshellId)
    {
        return await _context.AppUserLinkshells.AnyAsync(link => link.AppUserId == userId && link.LinkshellId == linkshellId);
    }

    private async Task<int> ResolveActiveLinkshellIdAsync(AppUser user)
    {
        var linkshellIds = await _context.AppUserLinkshells
            .Where(link => link.AppUserId == user.Id)
            .OrderBy(link => link.Linkshell!.LinkshellName)
            .Select(link => link.LinkshellId)
            .ToListAsync();

        if (user.PrimaryLinkshellId.HasValue && linkshellIds.Contains(user.PrimaryLinkshellId.Value))
        {
            return user.PrimaryLinkshellId.Value;
        }

        return linkshellIds.FirstOrDefault();
    }

    private async Task<LinkshellRole?> GetEffectiveRoleAsync(string appUserId, int linkshellId)
    {
        var rank = await _context.AppUserLinkshells
            .Where(m => m.AppUserId == appUserId && m.LinkshellId == linkshellId)
            .Select(m => m.Rank)
            .FirstOrDefaultAsync();
        if (rank is null) return null;
        var rankName = string.IsNullOrWhiteSpace(rank) ? "Member" : rank.Trim();
        return await _context.LinkshellRoles
            .AsNoTracking()
            .FirstOrDefaultAsync(r => r.LinkshellId == linkshellId && r.Name == rankName);
    }

    private async Task<bool> ResolveCanManagePartiesAsync(string appUserId, int linkshellId)
    {
        var role = await GetEffectiveRoleAsync(appUserId, linkshellId);
        return role?.CanManageParties == true;
    }
}
