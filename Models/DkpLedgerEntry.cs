using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace LinkshellManagerDiscordApp.Models;

public class DkpLedgerEntry
{
    [Key]
    public int Id { get; set; }

    public string? AppUserId { get; set; }

    [ForeignKey(nameof(AppUserId))]
    public AppUser? AppUser { get; set; }

    public int LinkshellId { get; set; }

    [ForeignKey(nameof(LinkshellId))]
    public Linkshell? Linkshell { get; set; }

    public int? EventHistoryId { get; set; }

    [ForeignKey(nameof(EventHistoryId))]
    public EventHistory? EventHistory { get; set; }

    [MaxLength(32)]
    public string EntryType { get; set; } = string.Empty;

    public double Amount { get; set; }

    public int Sequence { get; set; }

    public DateTime OccurredAt { get; set; }

    [MaxLength(256)]
    public string? CharacterName { get; set; }

    [MaxLength(256)]
    public string? EventName { get; set; }

    [MaxLength(256)]
    public string? EventType { get; set; }

    [MaxLength(256)]
    public string? EventLocation { get; set; }

    public DateTime? EventStartTime { get; set; }

    public DateTime? EventEndTime { get; set; }

    [MaxLength(256)]
    public string? ItemName { get; set; }

    [MaxLength(1024)]
    public string? Details { get; set; }

    // Populated when this ledger entry was produced by the Loot History edit
    // flow (EntryType = "LootEditRefund" / "LootEditSpent"). Surfaces in the
    // DKP-history view as an "Edited" tag with the reason on hover. Null on
    // organic earn/spend/refund entries.
    [MaxLength(512)]
    public string? EditReason { get; set; }

    // Optional back-references so a ledger entry can be traced to the loot
    // row it was generated from. Exactly one is set on edit-driven entries,
    // both null on legacy / non-loot entries. Not foreign-key-enforced — the
    // loot row could be deleted later, and we want the ledger to remain
    // immutable regardless.
    public int? SourceTodLootDetailId { get; set; }

    public int? SourceEventLootDetailId { get; set; }

    // Idempotency stamp for the ManualPoints sheet append. Set once after the
    // audit's column + cell are successfully written so a retry / replay
    // doesn't double-add. Null = not yet appended (Audit-type entries only;
    // event-earn / loot-spend rows leave this null permanently).
    public DateTime? SheetAppendedAt { get; set; }
}
