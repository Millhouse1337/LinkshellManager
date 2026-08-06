using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace LinkshellManagerDiscordApp.Models;

// One half of a treasury transaction. Append-only.
//
// Amount is SIGNED, and positive means "added to this category". Combined with the category's
// normal side (LedgerAccountClasses.NormalSign) that is enough to say what happened without a
// second column and without an EntryType string:
//
//   Sold an item for 4,000,000     Gil on hand  +4,000,000    Item sales  -4,000,000
//   Paid a member 800,000          Gil to mbrs   +800,000     Gil on hand   -800,000
//
// Every entry's lines sum to zero, which is what makes "gil on hand" provable rather than
// asserted. Gil is whole numbers, so long is exact and there is no epsilon anywhere in this
// feature — unlike the double-based DKP ledger.
public class JournalEntryLine
{
    [Key]
    public int Id { get; set; }

    public int JournalEntryId { get; set; }

    [ForeignKey(nameof(JournalEntryId))]
    public JournalEntry? JournalEntry { get; set; }

    // Denormalized from the header so every balance query touches ONE table. A plain indexed int
    // rather than a second relationship to Linkshell — two cascade paths from Linkshells into
    // the same row is a constraint Postgres will refuse.
    public int LinkshellId { get; set; }

    public int LedgerAccountId { get; set; }

    [ForeignKey(nameof(LedgerAccountId))]
    public LedgerAccount? LedgerAccount { get; set; }

    // The category as it was AT THE TIME. Renaming "Item sales" must not rewrite what last
    // year's entries say — same reasoning as RevenueEntry.LinkshellName and
    // DkpLedgerEntry.CharacterName.
    public int AccountNumber { get; set; }

    [MaxLength(96)]
    public string AccountName { get; set; } = string.Empty;

    public long Amount { get; set; }

    // Denormalized from the header, so gil-in-and-out for a date range is a single index scan.
    // Cannot drift: both tables are insert-only.
    public DateTime TransactionDate { get; set; }

    public int LineNumber { get; set; }   // 1-based

    [MaxLength(256)]
    public string? LineMemo { get; set; }

    // Who the gil came from or went to. Today this information is buried in free text, which is
    // why "who did we pay" is unanswerable without reading every note.
    [MaxLength(450)]
    public string? CounterpartyAppUserId { get; set; }

    [MaxLength(256)]
    public string? CounterpartyCharacterName { get; set; }

    // --- derived, never stored ---

    // The amount as a user should read it: gil received on a money-in category is a positive
    // number even though it is stored negative.
    [NotMapped]
    public long PresentedAmount => Amount * LedgerAccountClasses.NormalSignForNumber(AccountNumber);
}
