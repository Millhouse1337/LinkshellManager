using LinkshellManagerDiscordApp.Models;
using LinkshellManagerDiscordApp.Services;
using LinkshellManagerDiscordApp.Utils;
using LinkshellManagerDiscordApp.ViewModels;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace LinkshellManagerDiscordApp.Controllers;

// Discord Activity API for the officer-designed raid-composition planner that
// lives inside the ToD Tracker. Mirrors Controllers/PartySetupController.cs (the
// Razor web controller) but swaps cookie auth for the Activity's
// ResolveAppUserAsync + GetMembershipAsync + CanAsync pattern so Discord
// bearer-token requests work. VIEW + sign-up/withdraw are open to any linkshell
// member; create/edit/delete/assign require the CanManageParties role flag
// (added in the Phase 2 editor section below).
public sealed partial class ActivityDataController
{
    private static readonly HashSet<string> PartySetupValidRoles =
        new(EventJobCatalog.JobTypeOptions, StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> PartySetupValidMainJobs =
        new(EventJobCatalog.MainJobOptions, StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> PartySetupValidSubJobs =
        new(EventJobCatalog.SubJobOptions, StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> PartySetupValidRequirementTypes =
        new(PartySetupSlotRequirementTypes.All, StringComparer.OrdinalIgnoreCase);

    public sealed record ActivityPartySetupListRow(
        int Id,
        string Name,
        string? EventType,
        string? AssignedMonsterName,
        int AllianceCount,
        int PartyCount,
        int SlotCount,
        DateTime UpdatedAt);

    public sealed record ActivityPartySetupListResponse(
        int LinkshellId,
        string? LinkshellName,
        bool CanManage,
        IReadOnlyList<ActivityPartySetupListRow> Items,
        IReadOnlyList<string> MonsterOptions,
        IReadOnlyList<string> RoleOptions,
        IReadOnlyList<string> MainJobOptions,
        IReadOnlyList<string> SubJobOptions);

    public sealed record ActivityPartySetupSlotDto(
        int SlotId,
        int Position,
        string RequirementType,
        string? Role,
        string? MainJob,
        string? SubJob,
        string? Label,
        bool IsPartyLeader,
        string? SignedUpAppUserId,
        string? SignedUpCharacterName,
        string? SignedUpRole,
        string? SignedUpMainJob,
        string? SignedUpSubJob,
        // Per-event: whether the member in this slot is the party's leader.
        // Distinct from IsPartyLeader above (the template's designated-leader slot).
        bool SignedUpIsPartyLeader);

    public sealed record ActivityPartySetupPartyDto(
        int PartyId,
        string Name,
        IReadOnlyList<ActivityPartySetupSlotDto> Slots);

    public sealed record ActivityPartySetupAllianceDto(
        int AllianceId,
        string Name,
        IReadOnlyList<ActivityPartySetupPartyDto> Parties);

    public sealed record ActivityPartySetupDetailDto(
        int Id,
        int LinkshellId,
        string Name,
        string? EventType,
        string? AssignedMonsterName,
        string? Notes,
        bool CanManage,
        IReadOnlyList<ActivityPartySetupAllianceDto> Alliances,
        // Event boards only: members attending WITHOUT a party slot. Null on the
        // reusable template board (no event roster).
        IReadOnlyList<ActivityAlsoAttendingDto>? AlsoAttending = null);

    public sealed record ActivityAlsoAttendingDto(
        string? CharacterName,
        string? Role,
        string? MainJob,
        string? SubJob,
        string? AppUserId = null);

    public sealed record ActivityPartySetupSignUpRequest(string? Role, string? MainJob, string? SubJob, bool AsLeader = false, string? CharacterName = null);

    [HttpGet("party-setups")]
    public async Task<IActionResult> GetPartySetupsAsync([FromQuery] int linkshellId, CancellationToken cancellationToken)
    {
        var appUser = await ResolveAppUserAsync(cancellationToken);
        if (appUser is null) return Unauthorized(new { error = "Sign in to load party setups." });
        if (linkshellId <= 0) return BadRequest(new { error = "A linkshell selection is required." });

        var membership = await GetMembershipAsync(appUser.Id, linkshellId, cancellationToken);
        if (membership is null) return Forbid();

        var canManage = await CanAsync(membership, r => r.CanManageParties, cancellationToken);

        var items = await _dbContext.PartySetups
            .AsNoTracking()
            // OwnerEventId == null → reusable templates only (exclude per-event snapshots).
            .Where(ps => ps.LinkshellId == linkshellId && ps.OwnerEventId == null)
            .OrderByDescending(ps => ps.UpdatedAt)
            .Select(ps => new ActivityPartySetupListRow(
                ps.Id,
                ps.Name,
                ps.EventType,
                ps.AssignedMonsterName,
                ps.Alliances.Count,
                ps.Alliances.SelectMany(a => a.Parties).Count(),
                ps.Alliances.SelectMany(a => a.Parties).SelectMany(p => p.Slots).Count(),
                ps.UpdatedAt))
            .ToListAsync(cancellationToken);

        return Ok(new ActivityPartySetupListResponse(
            linkshellId,
            membership.Linkshell?.LinkshellName,
            canManage,
            items,
            TodManagerViewModel.SupportedMonsters.ToList(),
            EventJobCatalog.JobTypeOptions.ToList(),
            EventJobCatalog.MainJobOptions.ToList(),
            EventJobCatalog.SubJobOptions.ToList()));
    }

    [HttpGet("party-setups/{id:int}")]
    public async Task<IActionResult> GetPartySetupAsync(int id, CancellationToken cancellationToken)
    {
        var appUser = await ResolveAppUserAsync(cancellationToken);
        if (appUser is null) return Unauthorized(new { error = "Sign in to load the party setup." });

        var partySetup = await _dbContext.PartySetups
            .AsNoTracking()
            .Include(ps => ps.Alliances).ThenInclude(a => a.Parties).ThenInclude(p => p.Slots)
            .FirstOrDefaultAsync(ps => ps.Id == id, cancellationToken);
        if (partySetup is null) return NotFound(new { error = "Party setup not found." });

        var membership = await GetMembershipAsync(appUser.Id, partySetup.LinkshellId, cancellationToken);
        if (membership is null) return Forbid();

        var canManage = await CanAsync(membership, r => r.CanManageParties, cancellationToken);

        var alliances = partySetup.Alliances
            .OrderBy(a => a.SortOrder)
            .Select(a => new ActivityPartySetupAllianceDto(
                a.Id,
                string.IsNullOrWhiteSpace(a.Name) ? $"Alliance {a.SortOrder + 1}" : a.Name,
                a.Parties
                    .OrderBy(p => p.SortOrder)
                    .Select(p => new ActivityPartySetupPartyDto(
                        p.Id,
                        string.IsNullOrWhiteSpace(p.Name) ? $"Party {p.SortOrder + 1}" : p.Name!,
                        p.Slots
                            .OrderBy(s => s.SortOrder)
                            .Select(s => new ActivityPartySetupSlotDto(
                                s.Id,
                                s.SortOrder + 1,
                                s.RequirementType,
                                s.Role,
                                s.MainJob,
                                s.SubJob,
                                s.Label,
                                s.IsPartyLeader,
                                s.SignedUpAppUserId,
                                s.SignedUpCharacterName,
                                s.SignedUpRole,
                                s.SignedUpMainJob,
                                s.SignedUpSubJob,
                                false))
                            .ToList()))
                    .ToList()))
            .ToList();

        return Ok(new ActivityPartySetupDetailDto(
            partySetup.Id,
            partySetup.LinkshellId,
            partySetup.Name,
            partySetup.EventType,
            partySetup.AssignedMonsterName,
            partySetup.Notes,
            canManage,
            alliances));
    }

    // Member self-service: claim an open slot. Any linkshell member may sign up;
    // you hold at most one slot per setup (signing up releases any other slot you
    // hold in the same setup so you can switch roles cleanly). Ports
    // PartySetupController.SignUp.
    [HttpPost("party-setups/{id:int}/slots/{slotId:int}/signup")]
    public async Task<IActionResult> SignUpForPartySlotAsync(
        int id,
        int slotId,
        [FromBody] ActivityPartySetupSignUpRequest request,
        CancellationToken cancellationToken)
    {
        var appUser = await ResolveAppUserAsync(cancellationToken);
        if (appUser is null) return Unauthorized(new { error = "Sign in to sign up." });

        var slot = await _dbContext.PartySetupSlots
            .Include(s => s.Party!).ThenInclude(p => p.Alliance!).ThenInclude(a => a.PartySetup!)
            .FirstOrDefaultAsync(s => s.Id == slotId, cancellationToken);
        var setup = slot?.Party?.Alliance?.PartySetup;
        if (slot is null || setup is null || setup.Id != id) return NotFound(new { error = "Slot not found." });

        var membership = await GetMembershipAsync(appUser.Id, setup.LinkshellId, cancellationToken);
        if (membership is null) return Forbid();

        // Snapshot the member's character name in this linkshell (fall back to
        // their profile name) so the panel renders without extra joins.
        var characterName = string.IsNullOrWhiteSpace(membership.CharacterName)
            ? (appUser.CharacterName ?? appUser.UserName ?? "Member")
            : membership.CharacterName;

        var result = await PartySetupSignupService.ClaimSlotAsync(
            _dbContext, slot, setup.Id, appUser.Id, characterName,
            request.Role, request.MainJob, request.SubJob, cancellationToken);
        if (!result.Success)
        {
            return BadRequest(new { error = result.Error });
        }
        await _dbContext.SaveChangesAsync(cancellationToken);

        return Ok(new { success = true });
    }

    // Release a slot. The member who holds it can withdraw themselves; an officer
    // with CanManageParties can clear anyone's sign-up. Ports
    // PartySetupController.Withdraw.
    [HttpPost("party-setups/{id:int}/slots/{slotId:int}/withdraw")]
    public async Task<IActionResult> WithdrawFromPartySlotAsync(int id, int slotId, CancellationToken cancellationToken)
    {
        var appUser = await ResolveAppUserAsync(cancellationToken);
        if (appUser is null) return Unauthorized(new { error = "Sign in to withdraw." });

        var slot = await _dbContext.PartySetupSlots
            .Include(s => s.Party!).ThenInclude(p => p.Alliance!).ThenInclude(a => a.PartySetup!)
            .FirstOrDefaultAsync(s => s.Id == slotId, cancellationToken);
        var setup = slot?.Party?.Alliance?.PartySetup;
        if (slot is null || setup is null || setup.Id != id) return NotFound(new { error = "Slot not found." });

        var membership = await GetMembershipAsync(appUser.Id, setup.LinkshellId, cancellationToken);
        if (membership is null) return Forbid();

        var isHolder = slot.SignedUpAppUserId == appUser.Id;
        if (!isHolder && !await CanAsync(membership, r => r.CanManageParties, cancellationToken))
        {
            return Forbid();
        }

        PartySetupSignupService.ClearSlotSignup(slot);
        await _dbContext.SaveChangesAsync(cancellationToken);
        return Ok(new { success = true });
    }

    // ----- Officer editor (create / edit / delete / assign) -----
    // All require the CanManageParties role flag. Ports the create/edit/delete/
    // assign logic + tree builder from PartySetupController, validating against
    // the same catalogs.

    public sealed record ActivityPartySetupSlotInput(
        int AllianceIndex,
        int PartyIndex,
        int SlotIndex,
        string? AllianceName,
        string? PartyName,
        string RequirementType,
        string? Role,
        string? MainJob,
        string? SubJob,
        bool IsPartyLeader);

    public sealed record ActivityPartySetupEditorRequest(
        int LinkshellId,
        string Name,
        string? EventType,
        string? AssignedMonsterName,
        string? Notes,
        IReadOnlyList<ActivityPartySetupSlotInput> Slots);

    public sealed record ActivityPartySetupAssignRequest(string? MonsterName);

    [HttpPost("party-setups")]
    public async Task<IActionResult> CreatePartySetupAsync(
        [FromBody] ActivityPartySetupEditorRequest request,
        CancellationToken cancellationToken)
    {
        var appUser = await ResolveAppUserAsync(cancellationToken);
        if (appUser is null) return Unauthorized(new { error = "Sign in to manage party setups." });
        if (request.LinkshellId <= 0) return BadRequest(new { error = "A linkshell selection is required." });

        var membership = await GetMembershipAsync(appUser.Id, request.LinkshellId, cancellationToken);
        if (!await CanAsync(membership, r => r.CanManageParties, cancellationToken)) return Forbid();

        var linkshellType = await _dbContext.Linkshells
            .Where(ls => ls.Id == request.LinkshellId)
            .Select(ls => ls.LinkshellType)
            .FirstOrDefaultAsync(cancellationToken);
        if (linkshellType is null) return NotFound(new { error = "Linkshell not found." });

        var normalizedLinkshellType = LinkshellTypes.Normalize(linkshellType);
        var normalizedEventType = NormalizePartySetupEventType(request.EventType, normalizedLinkshellType);

        var validationError = ValidatePartySetupEditor(request, normalizedLinkshellType, normalizedEventType);
        if (validationError is not null) return BadRequest(new { error = validationError });

        var now = DateTime.UtcNow;
        var partySetup = new PartySetup
        {
            LinkshellId = request.LinkshellId,
            Name = request.Name.Trim(),
            EventType = normalizedEventType,
            AssignedMonsterName = IsHnmPartySetupType(normalizedEventType) ? NormalizeMonster(request.AssignedMonsterName) : null,
            Notes = string.IsNullOrWhiteSpace(request.Notes) ? null : request.Notes.Trim(),
            CreatedByAppUserId = appUser.Id,
            CreatedByCharacterName = membership!.CharacterName,
            CreatedAt = now,
            UpdatedAt = now,
            Alliances = BuildPartyTreeFromFlat(request.Slots)
        };

        _dbContext.PartySetups.Add(partySetup);
        await _dbContext.SaveChangesAsync(cancellationToken);

        return Ok(new { id = partySetup.Id });
    }

    [HttpPost("party-setups/{id:int}")]
    public async Task<IActionResult> UpdatePartySetupAsync(
        int id,
        [FromBody] ActivityPartySetupEditorRequest request,
        CancellationToken cancellationToken)
    {
        var appUser = await ResolveAppUserAsync(cancellationToken);
        if (appUser is null) return Unauthorized(new { error = "Sign in to manage party setups." });

        var partySetup = await _dbContext.PartySetups
            .Include(ps => ps.Alliances)
            .FirstOrDefaultAsync(ps => ps.Id == id, cancellationToken);
        if (partySetup is null) return NotFound(new { error = "Party setup not found." });

        var membership = await GetMembershipAsync(appUser.Id, partySetup.LinkshellId, cancellationToken);
        if (!await CanAsync(membership, r => r.CanManageParties, cancellationToken)) return Forbid();

        var linkshellType = await _dbContext.Linkshells
            .Where(ls => ls.Id == partySetup.LinkshellId)
            .Select(ls => ls.LinkshellType)
            .FirstOrDefaultAsync(cancellationToken);
        if (linkshellType is null) return NotFound(new { error = "Linkshell not found." });

        var normalizedLinkshellType = LinkshellTypes.Normalize(linkshellType);
        var normalizedEventType = NormalizePartySetupEventType(request.EventType, normalizedLinkshellType);

        var validationError = ValidatePartySetupEditor(request, normalizedLinkshellType, normalizedEventType);
        if (validationError is not null) return BadRequest(new { error = validationError });

        // Replace the whole tree (mirrors PartySetupController.Edit). Cascade
        // delete clears the old parties/slots.
        _dbContext.PartySetupAlliances.RemoveRange(partySetup.Alliances);

        partySetup.Name = request.Name.Trim();
        partySetup.EventType = normalizedEventType;
        partySetup.AssignedMonsterName = IsHnmPartySetupType(normalizedEventType) ? NormalizeMonster(request.AssignedMonsterName) : null;
        partySetup.Notes = string.IsNullOrWhiteSpace(request.Notes) ? null : request.Notes.Trim();
        partySetup.UpdatedAt = DateTime.UtcNow;
        partySetup.Alliances = BuildPartyTreeFromFlat(request.Slots);

        await _dbContext.SaveChangesAsync(cancellationToken);
        return Ok(new { success = true });
    }

    [HttpPost("party-setups/{id:int}/delete")]
    public async Task<IActionResult> DeletePartySetupAsync(int id, CancellationToken cancellationToken)
    {
        var appUser = await ResolveAppUserAsync(cancellationToken);
        if (appUser is null) return Unauthorized(new { error = "Sign in to manage party setups." });

        var partySetup = await _dbContext.PartySetups.FirstOrDefaultAsync(ps => ps.Id == id, cancellationToken);
        if (partySetup is null) return NotFound(new { error = "Party setup not found." });

        var membership = await GetMembershipAsync(appUser.Id, partySetup.LinkshellId, cancellationToken);
        if (!await CanAsync(membership, r => r.CanManageParties, cancellationToken)) return Forbid();

        _dbContext.PartySetups.Remove(partySetup);
        await _dbContext.SaveChangesAsync(cancellationToken);
        return Ok(new { success = true });
    }

    [HttpPost("party-setups/{id:int}/assign")]
    public async Task<IActionResult> AssignPartySetupAsync(
        int id,
        [FromBody] ActivityPartySetupAssignRequest request,
        CancellationToken cancellationToken)
    {
        var appUser = await ResolveAppUserAsync(cancellationToken);
        if (appUser is null) return Unauthorized(new { error = "Sign in to manage party setups." });

        var partySetup = await _dbContext.PartySetups.FirstOrDefaultAsync(ps => ps.Id == id, cancellationToken);
        if (partySetup is null) return NotFound(new { error = "Party setup not found." });

        var membership = await GetMembershipAsync(appUser.Id, partySetup.LinkshellId, cancellationToken);
        if (!await CanAsync(membership, r => r.CanManageParties, cancellationToken)) return Forbid();

        var trimmed = request.MonsterName?.Trim();
        if (!IsHnmPartySetupType(partySetup.EventType))
        {
            return BadRequest(new { error = "Monster assignment only applies to HNM party setups." });
        }
        if (!string.IsNullOrEmpty(trimmed) && !SupportedTodMonsters.Contains(trimmed))
        {
            return BadRequest(new { error = "That monster is not a supported ToD monster." });
        }

        partySetup.AssignedMonsterName = string.IsNullOrEmpty(trimmed) ? null : trimmed;
        partySetup.UpdatedAt = DateTime.UtcNow;
        await _dbContext.SaveChangesAsync(cancellationToken);
        return Ok(new { success = true });
    }

    private static string? NormalizeMonster(string? monsterName)
    {
        var trimmed = monsterName?.Trim();
        return string.IsNullOrEmpty(trimmed) ? null : trimmed;
    }

    private static string? NormalizePartySetupEventType(string? eventType, string linkshellType)
    {
        if (LinkshellTypes.Normalize(linkshellType) == LinkshellTypes.HnmOnly)
        {
            return "HNM";
        }

        var trimmed = eventType?.Trim();
        return string.IsNullOrEmpty(trimmed) ? null : trimmed;
    }

    private static bool IsHnmPartySetupType(string? eventType)
        => string.Equals(eventType?.Trim(), "HNM", StringComparison.OrdinalIgnoreCase);

    // Ports PartySetupController.NormalizeAndValidate to a single error string
    // (null = valid). The editor sends a RequirementType already derived from
    // the picks (Job > Role > Any), so this validates the same way the web form
    // does. FFXI caps: <=3 parties per alliance, <=6 slots per party.
    private static string? ValidatePartySetupEditor(
        ActivityPartySetupEditorRequest request,
        string linkshellType,
        string? normalizedEventType)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
        {
            return "A name is required.";
        }

        if (string.IsNullOrWhiteSpace(normalizedEventType))
        {
            return "Select an event type.";
        }

        if (LinkshellTypes.Normalize(linkshellType) == LinkshellTypes.SkySeaDynamis && IsHnmPartySetupType(normalizedEventType))
        {
            return "HNM is not available for Sky/Sea/Dynamis linkshells.";
        }

        var monster = request.AssignedMonsterName?.Trim();
        if (IsHnmPartySetupType(normalizedEventType) &&
            !string.IsNullOrEmpty(monster) &&
            !SupportedTodMonsters.Contains(monster))
        {
            return "Select a supported ToD monster, or leave it unassigned.";
        }

        var slots = request.Slots ?? new List<ActivityPartySetupSlotInput>();
        if (slots.Count == 0)
        {
            return "Add at least one alliance with a party and a slot.";
        }

        var overfilledAlliance = slots
            .GroupBy(s => s.AllianceIndex)
            .Any(g => g.Select(s => s.PartyIndex).Distinct().Count() > 3);
        if (overfilledAlliance)
        {
            return "An alliance can have at most 3 parties.";
        }

        var overfilledParty = slots
            .GroupBy(s => new { s.AllianceIndex, s.PartyIndex })
            .Any(g => g.Count() > 6);
        if (overfilledParty)
        {
            return "A party can have at most 6 slots.";
        }

        foreach (var slot in slots)
        {
            var requirement = slot.RequirementType?.Trim() ?? string.Empty;
            if (!PartySetupValidRequirementTypes.Contains(requirement))
            {
                return "Choose Any, Role, or Job for every slot.";
            }

            if (string.Equals(requirement, PartySetupSlotRequirementTypes.Role, StringComparison.OrdinalIgnoreCase))
            {
                if (string.IsNullOrWhiteSpace(slot.Role) || !PartySetupValidRoles.Contains(slot.Role.Trim()))
                {
                    return "Pick a role (Tank/Heal/Support/DPS) on every Role slot.";
                }
            }
            else if (string.Equals(requirement, PartySetupSlotRequirementTypes.Job, StringComparison.OrdinalIgnoreCase))
            {
                if (string.IsNullOrWhiteSpace(slot.MainJob) || !PartySetupValidMainJobs.Contains(slot.MainJob.Trim()))
                {
                    return "Pick a main job on every Job slot.";
                }
                if (!string.IsNullOrWhiteSpace(slot.SubJob) && !PartySetupValidSubJobs.Contains(slot.SubJob.Trim()))
                {
                    return "Pick a valid subjob, or leave it blank.";
                }
            }
        }

        return null;
    }

    // Ports PartySetupController.BuildTreeFromFlat: rebuild the persisted
    // Alliance -> Party -> Slot tree from the flat posted slot list, grouped by
    // the per-row alliance/party indices.
    private static List<PartySetupAlliance> BuildPartyTreeFromFlat(IEnumerable<ActivityPartySetupSlotInput> slots)
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
                    party.Slots.Add(MapPartySlot(slot, slotSort));
                    slotSort++;
                }

                // At most one leader per party: keep the first flagged slot,
                // clear any extras a crafted post slipped in.
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

    // Ports PartySetupController.MapSlot: RequirementType is derived by
    // precedence (Job > Role > Any); "Any Role" is the editor's "anything goes"
    // sentinel and stores as an open Any slot.
    private static PartySetupSlot MapPartySlot(ActivityPartySetupSlotInput input, int sortOrder)
    {
        var role = input.Role?.Trim();
        var mainJob = input.MainJob?.Trim();
        var subJob = input.SubJob?.Trim();

        if (string.Equals(role, "Any Role", StringComparison.OrdinalIgnoreCase))
        {
            role = null;
        }

        var hasMainJob = !string.IsNullOrWhiteSpace(mainJob) && PartySetupValidMainJobs.Contains(mainJob!);
        var hasRole = !string.IsNullOrWhiteSpace(role) && PartySetupValidRoles.Contains(role!);

        var slot = new PartySetupSlot
        {
            SortOrder = sortOrder,
            Label = null,
            IsPartyLeader = input.IsPartyLeader
        };

        if (hasRole)
        {
            slot.Role = role;
        }

        if (hasMainJob)
        {
            slot.RequirementType = PartySetupSlotRequirementTypes.Job;
            slot.MainJob = mainJob;
            slot.SubJob = (!string.IsNullOrWhiteSpace(subJob) && PartySetupValidSubJobs.Contains(subJob!))
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
}
