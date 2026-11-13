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

    // Old template sign-up (Phase 5): members used to claim a slot on the
    // TEMPLATE from the ToD Tracker's inline panel. Those sign-ups never reached
    // the event or Discord board, so that path was removed and the panel is now
    // view-only; real sign-ups live in EventPartySlotSignups. Nothing writes
    // these six columns any more except PartySetupSignupReset (sets them to
    // null on each ToD) and UserMergeService (re-points ids); nothing reads them.
    // DROP-LATER: template sign-ups removed in Phase 5; column is never set.
    [MaxLength(450)]
    public string? SignedUpAppUserId { get; set; }

    // DROP-LATER: template sign-ups removed in Phase 5; column is never set.
    [MaxLength(256)]
    public string? SignedUpCharacterName { get; set; }

    // DROP-LATER: template sign-ups removed in Phase 5; column is never set.
    public DateTime? SignedUpAtUtc { get; set; }

    // DROP-LATER: template sign-ups removed in Phase 5; column is never set.
    [MaxLength(16)]
    public string? SignedUpRole { get; set; }

    // DROP-LATER: template sign-ups removed in Phase 5; column is never set.
    [MaxLength(8)]
    public string? SignedUpMainJob { get; set; }

    // DROP-LATER: template sign-ups removed in Phase 5; column is never set.
    [MaxLength(8)]
    public string? SignedUpSubJob { get; set; }
}
