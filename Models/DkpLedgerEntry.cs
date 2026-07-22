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

    // The DKP pool (wallet) this amount belongs to. Null means "the linkshell's default
    // pool" — that's the state of every row written before pools existed, and of every row
    // in a linkshell that never customized its pools, so it never needs backfilling.
    //
    // Intentionally NOT a foreign key, matching SourceTodLootDetailId / SourceEventLootDetailId
    // below: the ledger is immutable history and must survive whatever happens to the rows it
    // points at.
    public int? DkpPoolId { get; set; }

    // False (the default): DkpPoolId is a CACHE of "which pool does EventType map to right
    // now", and DkpPoolEditor re-stamps it whenever an officer remaps event types. True: the
    // pool was an explicit officer/system choice (an auction's pool, ToD loot, an adjustment,
    // an import) and a remap must never move it.
    //
    // Stored rather than inferred from EntryType because "LootSpent" is genuinely ambiguous —
    // it's written both by event close (derived from the event's type) and by ToD loot (pinned).
    public bool DkpPoolPinned { get; set; }

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

    // Populated on the deduction rows created at AuctionController.CloseAuction
    // time so a single auction close produces one batched column on the
    // ManualPoints sheet instead of N separate columns. Null on non-auction
    // ledger entries.
    public int? SourceAuctionHistoryId { get; set; }

    [ForeignKey(nameof(SourceAuctionHistoryId))]
    public AuctionHistory? SourceAuctionHistory { get; set; }

    // Populated when a Window Event built from attendance snapshots is posted
    // to AttInput. This lets DKP history/audits show the snapshot-derived DKP
    // rows and prevents backfills from creating duplicates.
    public int? SourceWindowEventId { get; set; }

    // For audit adjustments, points at the ledger entry being corrected. This
    // lets subsequent corrections calculate the delta from the current audited
    // value instead of repeatedly comparing against the original amount.
    public int? AuditRelatedLedgerEntryId { get; set; }

    // 1-based row number in the linkshell's AttInput tab for entries that
    // originated from an append-only sheet row. Used when auditing snapshot
    // DKP so the original source row can be corrected in place.
    public int? AttInputRowNumber { get; set; }

    // Idempotency stamp for the ManualPoints sheet append. Set once after the
    // audit's column + cell are successfully written so a retry / replay
    // doesn't double-add. Null = not yet appended (Audit-type entries only;
    // event-earn / loot-spend rows leave this null permanently).
    public DateTime? SheetAppendedAt { get; set; }
}
