using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace LinkshellManagerDiscordApp.Models;

public class EventLootDetail
{
    [Key]
    public int Id { get; set; }

    // The linkshell this loot belongs to.
    //
    // It used to be reachable only through Event/EventHistory, which was fine while every row had
    // one. Loot can now be recorded with NO event at all (the "No event" option on Add loot), and
    // such a row would otherwise be unreachable -- nothing would know whose loot history to list
    // it in. Stamped on every row, event-linked or not, so the history query is one indexed
    // predicate instead of two joins.
    // NULLABLE, and that matters for the migration: existing rows have no linkshell column to
    // backfill from when neither an Event nor an EventHistory survives, and a NOT NULL column
    // defaulted to 0 would fail the foreign key the moment it was added. Every row written from
    // now on sets it.
    public int? LinkshellId { get; set; }

    [ForeignKey(nameof(LinkshellId))]
    public Linkshell? Linkshell { get; set; }

    // The DKP pool this loot was paid from, mirroring TodLootDetail.
    //
    // Event-linked loot can derive a pool from the event type, but a "No event" row has no type to
    // derive from -- and a refund has to credit the pool the debit came out of even if an officer
    // has remapped event types since. Null = the linkshell's default pool.
    public int? DkpPoolId { get; set; }

    [ForeignKey(nameof(DkpPoolId))]
    public DkpPool? DkpPool { get; set; }

    // When this loot was ALREADY charged to the winner, or null if it has not been.
    //
    // Ordinary event loot is debited when the event CLOSES -- SubmitLootDetails only checks that
    // the winner can afford it. Loot added by hand from the Loot System is debited on the spot
    // (the Add loot form says so), and it can be attached to a live event, so without this the
    // close-out loop would charge the same winner a second time.
    //
    // Null on every pre-existing row, which is exactly right: those were all added through the
    // event flow and are still owed their debit at close.
    public DateTime? DkpDebitedAt { get; set; }

    // Nullable so the loot row can survive its parent Event being deleted at
    // close-out time. When the event closes, the EventHistoryId below is
    // populated and EventId is detached so officers can keep editing the loot
    // via the Loot History view even after the event is archived.
    public int? EventId { get; set; }

    [ForeignKey(nameof(EventId))]
    public Event? Event { get; set; }

    // Set on event close so closed-event loot rows are still discoverable.
    // Null while the event is active (the EventId FK is sufficient).
    public int? EventHistoryId { get; set; }

    [ForeignKey(nameof(EventHistoryId))]
    public EventHistory? EventHistory { get; set; }

    public string? ItemName { get; set; }

    public string? ItemWinner { get; set; }

    public int? WinningDkpSpent { get; set; }

    // Stamped at event close (and on every Loot History edit) so refund-then-
    // reapply math stays correct even when a player's balance has drifted
    // since the original debit. Mirrors TodLootDetail.ActualDeductedDkp.
    public double? ActualDeductedDkp { get; set; }

    // Audit fields populated by LootEditService when an officer corrects this
    // row. See TodLootDetail for the field semantics.
    public DateTime? EditedAt { get; set; }

    [MaxLength(450)]
    public string? EditedByAppUserId { get; set; }

    [MaxLength(256)]
    public string? EditedByCharacterName { get; set; }

    [MaxLength(512)]
    public string? LastEditReason { get; set; }
}
