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
}
