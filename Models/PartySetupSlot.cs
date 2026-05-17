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
}
