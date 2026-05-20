using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace LinkshellManagerDiscordApp.Models;

// One slot within a party. RequirementType is a string discriminator
// ("Any" | "Role" | "Job"), matching the codebase convention for status/type
// columns (WindowEvent.Status, DkpLedgerEntry.EntryType) rather than a
// DB-mapped enum:
//   "Any"  -> open; Role/MainJob/SubJob are null.
//   "Role" -> generic role required; Role is set (Tank/Heal/Support/DPS).
//   "Job"  -> specific job required; MainJob set, SubJob optional (e.g. RDM/NIN).
// Label is an optional free-text annotation ("(GHORN)", "Melee DD"),
// independent of RequirementType.
public class PartySetupSlot
{
    [Key]
    public int Id { get; set; }

    public int PartySetupPartyId { get; set; }

    [ForeignKey(nameof(PartySetupPartyId))]
    public PartySetupParty? Party { get; set; }

    public int SortOrder { get; set; }

    [MaxLength(16)]
    public string RequirementType { get; set; } = "Any";

    [MaxLength(16)]
    public string? Role { get; set; }

    [MaxLength(8)]
    public string? MainJob { get; set; }

    [MaxLength(8)]
    public string? SubJob { get; set; }

    [MaxLength(64)]
    public string? Label { get; set; }

    // At most one slot per party is the designated party leader. Surfaced
    // with a crown in the editor + details views.
    public bool IsPartyLeader { get; set; }

    // Member sign-up. A linkshell member can claim an open slot from the ToD
    // Tracker's inline setup panel. Stored as a plain id + a snapshot of the
    // member's linkshell character name (mirrors the EditedByAppUserId /
    // EditedByCharacterName pattern on TodLootDetail -- no navigation, so
    // there's no FK cascade to reason about). Null = open.
    [MaxLength(450)]
    public string? SignedUpAppUserId { get; set; }

    [MaxLength(256)]
    public string? SignedUpCharacterName { get; set; }

    public DateTime? SignedUpAtUtc { get; set; }

    // What the signing-up member committed to BRING. For each of these:
    //   * If the slot specifies the requirement (Role on a Role slot;
    //     MainJob/SubJob on a Job slot), the sign-up snapshot mirrors the
    //     slot's value -- the member can't override a hard requirement.
    //   * If the slot leaves it open (Any Role, or "Any Tank" with no main
    //     job picked), the member fills it in at sign-up time so the panel
    //     reads "Millhouse - Tank - PLD/NIN" instead of just "Millhouse".
    // Cleared on Withdraw alongside the SignedUp* fields above.
    [MaxLength(16)]
    public string? SignedUpRole { get; set; }

    [MaxLength(8)]
    public string? SignedUpMainJob { get; set; }

    [MaxLength(8)]
    public string? SignedUpSubJob { get; set; }
}
