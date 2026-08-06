using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace LinkshellManagerDiscordApp.Models;

// One treasury transaction. Its Lines carry the two halves of the movement and MUST sum to
// zero (INV-2, asserted in ApplicationDbContext).
//
// A CONFIRMED ENTRY IS IMMUTABLE. No code path updates one (INV-3), which is why there is no
// UpdatedAt here: a wrong confirmed entry is fixed by recording an opposite entry plus a
// replacement, so what was believed and when survives. Only a Draft is editable, and a draft is
// the only thing that can be deleted.
//
// Officers never see the two halves. They pick one plain-English "what happened" option and
// TreasuryTransactionKinds builds the pair.
public class JournalEntry
{
    [Key]
    public int Id { get; set; }

    public int LinkshellId { get; set; }

    [ForeignKey(nameof(LinkshellId))]
    public Linkshell? Linkshell { get; set; }

    // Snapshot for the audit trail, matching RevenueEntry/Item/Announcement. A linkshell rename
    // must not rewrite what this entry said at the time.
    [MaxLength(256)]
    public string? LinkshellName { get; set; }

    // Per-linkshell monotonic, allocated MAX+1 by TreasuryJournalWriter and protected by a
    // unique index — the same scheme as DkpLedgerEntry.Sequence. Tie-breaks entries that share
    // a date.
    public int Sequence { get; set; }

    // "000142" — what an officer writes down or quotes in Discord. Stored rather than computed
    // so the reference they wrote down stays valid.
    [MaxLength(24)]
    public string EntryNumber { get; set; } = string.Empty;

    // When the gil actually moved (UTC). Drives the period a transaction falls in and whether a
    // lock applies. This is what RevenueEntry.OccurredAt meant — and, unlike today, an officer
    // inside Discord can finally set it.
    public DateTime TransactionDate { get; set; } = DateTime.UtcNow;

    // JournalEntryStatuses: Draft | Confirmed
    [MaxLength(16)]
    public string Status { get; set; } = JournalEntryStatuses.Draft;

    // JournalEntryKinds: Standard | Reversal | Correction | Opening | Adjustment | Migration
    [MaxLength(16)]
    public string Kind { get; set; } = JournalEntryKinds.Standard;

    // JournalEntrySources: Manual | ItemSale | GilAuction | Migration
    [MaxLength(16)]
    public string Source { get; set; } = JournalEntrySources.Manual;

    // The TreasuryTransactionKinds key the officer picked ("SoldAnItem"), so the list can show
    // the same plain-English sentence they chose rather than reverse-engineering it from the
    // categories.
    [MaxLength(48)]
    public string? TransactionKind { get; set; }

    [MaxLength(1024)]
    public string? Memo { get; set; }

    // Set on Reversal and Correction entries. NOT a foreign key, deliberately — matching
    // DkpLedgerEntry.SourceTodLootDetailId and friends: immutable history must not participate
    // in anyone else's cascade choreography.
    //
    // There is intentionally no ReversedByJournalEntryId back-pointer, because setting one would
    // be an UPDATE of a confirmed row. "Has this been reversed?" is an indexed EXISTS over
    // ReversesJournalEntryId, so immutability stays absolute.
    public int? ReversesJournalEntryId { get; set; }

    // Required whenever Kind is Reversal or Correction: an entry that asserts an earlier one was
    // wrong has to say why.
    [MaxLength(512)]
    public string? CorrectionReason { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    [MaxLength(450)]
    public string? CreatedByAppUserId { get; set; }   // no FK, matching RevenueEntry/Announcement

    [MaxLength(256)]
    public string? CreatedByCharacterName { get; set; }

    public DateTime? ConfirmedAt { get; set; }

    [MaxLength(450)]
    public string? ConfirmedByAppUserId { get; set; }

    [MaxLength(256)]
    public string? ConfirmedByCharacterName { get; set; }

    // Traceability back to whatever caused a machine-recorded entry. Un-FK'd for the same reason
    // as ReversesJournalEntryId.
    public int? SourceItemId { get; set; }
    public int? SourceAuctionItemId { get; set; }
    public int? SourceAuctionHistoryId { get; set; }

    // --- conversion bridge: permanent, never written by new code ---
    // Which RevenueEntries row this was built from and exactly what strings it carried. A
    // one-way conversion of real gil has to stay auditable row by row, and the pre-conversion
    // classification is itself part of the record.
    public int? LegacyRevenueEntryId { get; set; }

    [MaxLength(32)]
    public string? LegacyEntryType { get; set; }

    [MaxLength(128)]
    public string? LegacyCategory { get; set; }

    public ICollection<JournalEntryLine> Lines { get; set; } = new List<JournalEntryLine>();
}
