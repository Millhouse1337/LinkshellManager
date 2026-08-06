using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace LinkshellManagerDiscordApp.Models;

// One seated signup, frozen as it stood when a wyrm board's window turned over. Written by
// EventPartySignupService.ClearWindowRosterAsync immediately BEFORE it wipes the live roster, so
// the "View Previous Window" button can show what the board looked like for the hour that just
// ended. Without this the roster is simply gone — ClearWindowRosterAsync deletes the
// EventPartySlotSignup rows outright, and AppUserEventWindow records who the ADDON SCANNED as
// present, which is a different question from who was seated on the board.
//
// FULLY DENORMALIZED, deliberately. A snapshot has to outlive everything it was taken from:
//   * the EventPartySlotSignup rows are deleted moments later, by definition;
//   * PartySetup templates are reusable and EDITING ONE REBUILDS ITS SLOTS (see
//     EventPartySlotSignup's cascade note), so alliance/party/slot ids can vanish or be
//     recycled under a snapshot taken hours earlier.
// So the display strings and sort orders are copied in, and this table holds no FK to the party
// setup tree at all. The one FK is to the Event, which cascades — when the camp is deleted its
// window history goes with it, which is correct.
public class EventWindowRosterSnapshot
{
    [Key]
    public int Id { get; set; }

    public int EventId { get; set; }

    [ForeignKey(nameof(EventId))]
    public Event? Event { get; set; }

    // The window this roster BELONGED to — i.e. the one that just ended, not the one now open.
    // A board sitting on window 6 renders "View Previous Window" from WindowNumber == 5.
    public int WindowNumber { get; set; }

    public DateTime CapturedAtUtc { get; set; }

    // Grouping, copied as text + position so the snapshot renders in the board's own order
    // without touching the (possibly since-rebuilt) party setup.
    [MaxLength(64)]
    public string? AllianceName { get; set; }

    public int AllianceSortOrder { get; set; }

    [MaxLength(64)]
    public string? PartyName { get; set; }

    public int PartySortOrder { get; set; }

    public int SlotSortOrder { get; set; }

    // The slot's free-text annotation ("(GHORN)", "Melee DD") — part of how the row read on the
    // board, so it belongs in the snapshot rather than being re-derived.
    [MaxLength(64)]
    public string? SlotLabel { get; set; }

    // WHO held the slot. AppUserId is nullable throughout because "outside" Discord-only signups
    // never have one; CharacterName is what actually gets displayed and is always set.
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

    public bool IsPartyLeader { get; set; }

    public bool IsAllianceLeader { get; set; }

    // Whether this signup was pinned with "🔒 Stay Next Window" at capture time — i.e. whether it
    // was about to SURVIVE the wipe this snapshot precedes. Recorded so the previous window can be
    // read back honestly: a locked row appears in both that window and the next.
    public bool WasLocked { get; set; }
}
