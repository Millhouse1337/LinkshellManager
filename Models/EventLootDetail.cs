using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace LinkshellManagerDiscordApp.Models;

public class EventLootDetail
{
    [Key]
    public int Id { get; set; }

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
