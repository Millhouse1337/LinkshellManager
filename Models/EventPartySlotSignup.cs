using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace LinkshellManagerDiscordApp.Models;

// A member's claim on a party-setup slot FOR A SPECIFIC EVENT. Party setups are
// reusable templates, so signups must be per-event — otherwise a claim made for
// one event (or in the standalone view) would show on every event that links the
// same setup. Keyed by (EventId, PartySetupSlotId): one signup per slot per
// event, and a member holds at most one slot per event.
//
// Cascade-deletes from BOTH the Event (event removed → its signups go) and the
// PartySetupSlot (editing a template rebuilds its slots → old signups clear,
// which is the intended "edits reset the roster" behaviour). Postgres allows the
// two cascade paths.
public class EventPartySlotSignup
{
    [Key]
    public int Id { get; set; }

    public int EventId { get; set; }

    [ForeignKey(nameof(EventId))]
    public Event? Event { get; set; }

    public int PartySetupSlotId { get; set; }

    [ForeignKey(nameof(PartySetupSlotId))]
    public PartySetupSlot? PartySetupSlot { get; set; }

    [MaxLength(450)]
    public string? AppUserId { get; set; }

    [MaxLength(256)]
    public string? CharacterName { get; set; }

    [MaxLength(16)]
    public string? Role { get; set; }

    [MaxLength(8)]
    public string? MainJob { get; set; }

    [MaxLength(8)]
    public string? SubJob { get; set; }

    public DateTime SignedUpAtUtc { get; set; }

    // True when this signup is its party's designated leader for this event.
    // At most one signup per party carries this. Set when a member explicitly
    // "signs up as party leader" (first-claim-wins), or auto-assigned to the
    // party's earliest signup once every slot in the party is filled and nobody
    // claimed leadership. Purely a designation (a 👑 on the board) — no perms.
    public bool IsPartyLeader { get; set; }
}
