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
                                    Label = s.Label
                                }).ToList()
                        }).ToList()
                }).ToList()
        });
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

    private static List<PartySetupSlotInput> SeedSlots()
    {
        var slots = new List<PartySetupSlotInput>();
        for (var slotIndex = 0; slotIndex < 6; slotIndex++)
        {
            slots.Add(new PartySetupSlotInput
            {
                AllianceIndex = 0,
                PartyIndex = 0,
                SlotIndex = slotIndex,
                AllianceName = "Alliance 1",
                PartyName = "Party 1",
                RequirementType = PartySetupSlotRequirementTypes.Any
            });
        }
        return slots;
    }

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
                        Label = slot.Label
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

                alliance.Parties.Add(party);
                partySort++;
            }

            alliances.Add(alliance);
            allianceSort++;
        }

        return alliances;
    }

    private static PartySetupSlot MapSlot(PartySetupSlotInput input, int sortOrder)
    {
        var requirement = ValidRequirementTypes.Contains(input.RequirementType ?? string.Empty)
            ? input.RequirementType!.Trim()
            : PartySetupSlotRequirementTypes.Any;

        var slot = new PartySetupSlot
        {
            SortOrder = sortOrder,
            RequirementType = requirement,
            Label = string.IsNullOrWhiteSpace(input.Label) ? null : input.Label.Trim()
        };

        if (string.Equals(requirement, PartySetupSlotRequirementTypes.Role, StringComparison.OrdinalIgnoreCase))
        {
            slot.Role = input.Role?.Trim();
        }
        else if (string.Equals(requirement, PartySetupSlotRequirementTypes.Job, StringComparison.OrdinalIgnoreCase))
        {
            slot.MainJob = input.MainJob?.Trim();
            slot.SubJob = string.IsNullOrWhiteSpace(input.SubJob) ? null : input.SubJob.Trim();
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
