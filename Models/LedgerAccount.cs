using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace LinkshellManagerDiscordApp.Models;

// One category in a linkshell's treasury — "Item sales", "Gil paid to members", "Gil on hand".
//
// The account class and its normal side are NOT stored: both are derived from AccountNumber
// (LedgerAccountClasses), so a category whose number and behaviour disagree is not a state this
// schema can reach. Balances are likewise never stored — they are sums over JournalEntryLine
// (TreasuryBalanceService), the same derived-balance doctrine the repo already applies to DKP
// pools in DkpPoolBalanceService.
//
// Seeded per linkshell by LedgerAccountDefaults + LedgerAccountProvisioner. Officers may rename
// their categories and retire ones they have stopped using; they can never delete or renumber
// one that has history, because that would silently rewrite the meaning of every line pointing
// at it.
public class LedgerAccount
{
    [Key]
    public int Id { get; set; }

    public int LinkshellId { get; set; }

    [ForeignKey(nameof(LinkshellId))]
    public Linkshell? Linkshell { get; set; }

    // 1000-5999. IMMUTABLE once created. Never shown to a user — it exists so the class is
    // derivable and so the seeded set has stable identity across renames.
    public int AccountNumber { get; set; }

    [MaxLength(96)]
    public string Name { get; set; } = string.Empty;

    // PHASE4B-DROP: LedgerAccounts.Description - no screen shows it; no longer written or read.
    [MaxLength(512)]
    public string? Description { get; set; }

    // Exactly one category per linkshell carries this (enforced by a partial unique index). Its
    // balance IS "how much gil does this linkshell have" — the single number that replaced five
    // independently-computed ones.
    public bool IsCash { get; set; }

    // PHASE4B-DROP: LedgerAccounts.IsPostable - nothing ever checked it. "Nothing posts to what
    // we're worth" is guaranteed by TreasuryTransactionKinds never naming that category (a test
    // checks it). New rows still get the initializer's true, which the NOT NULL column needs.
    public bool IsPostable { get; set; } = true;

    // PHASE4B-DROP: LedgerAccounts.IsSystem - every category is seeded, nothing reads it.
    public bool IsSystem { get; set; }

    // Retired categories keep their history but drop out of the pickers.
    public bool IsActive { get; set; } = true;

    // PHASE4B-DROP: LedgerAccounts.SortOrder (+ its index) - categories are listed by AccountNumber
    // now; no longer written or read.
    public int SortOrder { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    // --- derived, never stored ---

    [NotMapped]
    public string? AccountClass => LedgerAccountClasses.FromNumber(AccountNumber);

    [NotMapped]
    public int NormalSign => LedgerAccountClasses.NormalSign(AccountClass);
}
