using System.ComponentModel.DataAnnotations;
using LinkshellManagerDiscordApp.Utils;

namespace LinkshellManagerDiscordApp.ViewModels;

// Slot requirement discriminator values. Mirrors the existing constants-class
// convention (e.g. LinkshellTypes / WindowEventStatuses) — stored as a short
// string on PartySetupSlot.RequirementType, not a DB enum.
public static class PartySetupSlotRequirementTypes
{
    public const string Any = "Any";
    public const string Role = "Role";
    public const string Job = "Job";

    public static readonly IReadOnlyList<string> All = new[] { Any, Role, Job };
}

public class PartySetupIndexViewModel
{
    public int LinkshellId { get; set; }
    public string? LinkshellName { get; set; }
    public bool CanManage { get; set; }
    public List<PartySetupListRow> Items { get; set; } = new();

    // Supported monsters for the inline "Assign" select on each row.
    public List<string> MonsterOptions { get; set; } = TodManagerViewModel.SupportedMonsters.ToList();
}

public class PartySetupListRow
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? AssignedMonsterName { get; set; }
    public int AllianceCount { get; set; }
    public int PartyCount { get; set; }
    public int SlotCount { get; set; }
    public DateTime UpdatedAt { get; set; }
}

// Flat editor binding shape. The editor posts a single contiguous Slots[]
// list (one global index, like the proven TodLootDetails[i] pattern); each
// row carries its own Alliance/Party/Slot index + the alliance/party display
// names so the controller can rebuild the persisted tree server-side.
public class PartySetupEditorViewModel
{
    public int Id { get; set; }
    public int LinkshellId { get; set; }
    public string? LinkshellName { get; set; }

    [Required(ErrorMessage = "A name is required.")]
    [MaxLength(128)]
    public string Name { get; set; } = string.Empty;

    [MaxLength(256)]
    public string? AssignedMonsterName { get; set; }

    // Free-text monster name captured when the Assigned Monster dropdown is set
    // to "Other" (HNM linkshells naming a custom pop). Folded into
    // AssignedMonsterName by the controller before validation.
    [MaxLength(256)]
    public string? AssignedMonsterCustom { get; set; }

    // The active linkshell's content style (SkySeaDynamis / HnmOnly / Both).
    // Drives which of the Event Type / Assigned Monster inputs the editor shows.
    public string? LinkshellType { get; set; }

    [MaxLength(1024)]
    public string? Notes { get; set; }

    // Which event type this setup is for. "Any" (the default) means it shows in
    // the Create Event party-setup picker for EVERY event type; a specific type
    // limits it to matching events.
    [MaxLength(64)]
    public string? EventType { get; set; } = "Any";

    public List<PartySetupSlotInput> Slots { get; set; } = new();

    // Option lists (repopulated by the controller on an invalid POST).
    public List<string> EventTypeOptions { get; set; } =
        new() { "Any", "Sky", "Sea", "HENM", "Limbus", "Dynamis", "BCNM", "KSNM" };
    public List<string> MonsterOptions { get; set; } = TodManagerViewModel.SupportedMonsters.ToList();
    public List<string> RoleOptions { get; set; } = EventJobCatalog.JobTypeOptions.ToList();
    public List<string> MainJobOptions { get; set; } = EventJobCatalog.MainJobOptions.ToList();
    public List<string> SubJobOptions { get; set; } = EventJobCatalog.SubJobOptions.ToList();
    public List<string> RequirementTypeOptions { get; set; } = PartySetupSlotRequirementTypes.All.ToList();
}

public class PartySetupSlotInput
{
    public int AllianceIndex { get; set; }
    public int PartyIndex { get; set; }
    public int SlotIndex { get; set; }

    [MaxLength(64)]
    public string? AllianceName { get; set; }

    [MaxLength(64)]
    public string? PartyName { get; set; }

    [MaxLength(16)]
    public string RequirementType { get; set; } = PartySetupSlotRequirementTypes.Any;

    [MaxLength(16)]
    public string? Role { get; set; }

    [MaxLength(8)]
    public string? MainJob { get; set; }

    [MaxLength(8)]
    public string? SubJob { get; set; }

    [MaxLength(64)]
    public string? Label { get; set; }

    public bool IsPartyLeader { get; set; }
}

public class PartySetupDetailsViewModel
{
    public int Id { get; set; }
    public int LinkshellId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? AssignedMonsterName { get; set; }
    public string? Notes { get; set; }
    public bool CanManage { get; set; }
    public List<string> MonsterOptions { get; set; } = TodManagerViewModel.SupportedMonsters.ToList();
    public List<PartySetupAllianceView> Alliances { get; set; } = new();
}

public class PartySetupAllianceView
{
    // Underlying PartySetupAlliance.Id — populated on the editable event board so
    // officer drag-drop / add-party / rename can target it. 0 elsewhere (unused).
    public int AllianceId { get; set; }
    public string Name { get; set; } = string.Empty;
    public List<PartySetupPartyView> Parties { get; set; } = new();
}

public class PartySetupPartyView
{
    // Underlying PartySetupParty.Id — see AllianceId above.
    public int PartyId { get; set; }
    public string Name { get; set; } = string.Empty;
    public List<PartySetupSlotView> Slots { get; set; } = new();
}

public class PartySetupSlotView
{
    // Underlying PartySetupSlot.Id, needed for sign-up / withdraw POSTs from
    // the ToD Tracker's inline panel. 0 on the read-only Details page (not used
    // there).
    public int SlotId { get; set; }
    public int Position { get; set; }
    public string RequirementType { get; set; } = PartySetupSlotRequirementTypes.Any;
    public string? Role { get; set; }
    public string? MainJob { get; set; }
    public string? SubJob { get; set; }
    public string? Label { get; set; }
    public bool IsPartyLeader { get; set; }

    // Member sign-up state (null when the slot is open).
    public string? SignedUpAppUserId { get; set; }
    public string? SignedUpCharacterName { get; set; }
    public bool IsOpen => string.IsNullOrEmpty(SignedUpAppUserId);

    // Per-event: whether the member in this slot is this party's leader (👑).
    // Distinct from IsPartyLeader above (the template's designated-leader slot).
    public bool SignedUpIsPartyLeader { get; set; }

    // What the signing-up member committed to BRING. The fields the slot
    // already pinned (Role on a Role slot; MainJob/SubJob on a Job slot)
    // mirror the slot's values; the fields the slot left open record the
    // member's pick at sign-up time. Used to render
    // "Millhouse - Tank - PLD/NIN" instead of just "Millhouse".
    public string? SignedUpRole { get; set; }
    public string? SignedUpMainJob { get; set; }
    public string? SignedUpSubJob { get; set; }

    // "Tank - PLD/NIN" / "Heal - WHM" / "Tank" (skipping any parts the
    // member didn't commit to). Empty when the member supplied nothing
    // beyond their character name (legacy sign-ups pre-dating this).
    public string SignedUpJobsDisplay
    {
        get
        {
            var parts = new List<string>(3);
            if (!string.IsNullOrWhiteSpace(SignedUpRole)) parts.Add(SignedUpRole!);
            if (!string.IsNullOrWhiteSpace(SignedUpMainJob))
            {
                parts.Add(string.IsNullOrWhiteSpace(SignedUpSubJob)
                    ? SignedUpMainJob!
                    : $"{SignedUpMainJob}/{SignedUpSubJob}");
            }
            return string.Join(" - ", parts);
        }
    }

    // Human-readable requirement. Mirrors the editor's wording so a slot
    // saved as "Any Role" reads "Any Role" on the details/ToD panels, a
    // role-only slot reads "Any Tank" / "Any Heal" / etc. (the same
    // placeholder the editor's Main job dropdown shows), and a job slot
    // reads the picked job ("PLD" or "PLD/NIN").
    public string Display
    {
        get
        {
            string core = RequirementType switch
            {
                PartySetupSlotRequirementTypes.Role => string.IsNullOrWhiteSpace(Role)
                    ? "Any Role"
                    : $"Any {Role}",
                PartySetupSlotRequirementTypes.Job => string.IsNullOrWhiteSpace(MainJob)
                    ? "Any job"
                    : $"{MainJob}/{(string.IsNullOrWhiteSpace(SubJob) ? "Player's Choice" : SubJob)}",
                _ => "Any Role"
            };
            return string.IsNullOrWhiteSpace(Label) ? core : $"{core} ({Label})";
        }
    }
}

// Backing model for the web _PartyBoardPanel partial — the live event party board
// with officer drag-drop + inline editing. Built by EventController.Start (inline)
// and EventController.PartyBoardPartial (the post-edit refresh endpoint).
public class PartyBoardPanelViewModel
{
    public int EventId { get; set; }
    // The event's linkshell — the web board's live-update loop long-polls
    // /api/activity/changes?linkshellId=… so passive viewers refresh on signups/edits.
    public int LinkshellId { get; set; }
    public PartySetupBoardViewModel Board { get; set; } = new();
    public bool CanManageParties { get; set; }
    public string? CurrentUserId { get; set; }
    public bool IsLive { get; set; }
    public List<string> SignupCharacters { get; set; } = new();
    public List<string> RoleOptions { get; set; } = new();
    public List<string> MainJobOptions { get; set; } = new();
    public List<string> SubJobOptions { get; set; } = new();
    public List<AlsoAttendingRow> AlsoAttending { get; set; } = new();
    public string ReturnUrl { get; set; } = string.Empty;

    public class AlsoAttendingRow
    {
        public string? AppUserId { get; set; }
        public string? CharacterName { get; set; }
        public string? JobType { get; set; }
        public string? JobName { get; set; }
        public string? SubJobName { get; set; }
    }
}
