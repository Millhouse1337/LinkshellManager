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

    // At most one slot per party is the designated party leader. Surfaced
    // with a crown in the editor + details views.
    public bool IsPartyLeader { get; set; }

    // (Six SignedUp* columns used to live here: the old template sign-up, where members claimed a
    // slot on the TEMPLATE from the ToD Tracker's panel. Those sign-ups never reached the event or
    // the Discord board, so Phase 5 removed them; members sign up on an event board instead
    // (EventPartySlotSignup). The DropPartySetupTemplateSignupColumns migration dropped the columns.)
}
