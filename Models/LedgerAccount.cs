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

    [MaxLength(512)]
    public string? Description { get; set; }

    // Exactly one category per linkshell carries this (enforced by a partial unique index). Its
    // balance IS "how much gil does this linkshell have" — the single number that replaced five
    // independently-computed ones.
    public bool IsCash { get; set; }

    // False only for the 3000 "what we're worth" category, which is a reporting line: worth is
    // the residual of what we hold minus what we owe, so nothing ever posts to it. That is what
    // removes the entire "the year-end close didn't run / ran twice" class of failure.
    public bool IsPostable { get; set; } = true;

    // Seeded by LedgerAccountDefaults. Can be renamed, never deleted or renumbered.
    public bool IsSystem { get; set; }

    // Retired categories keep their history but drop out of the pickers.
    public bool IsActive { get; set; } = true;

    public int SortOrder { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    // --- derived, never stored ---

    [NotMapped]
    public string? AccountClass => LedgerAccountClasses.FromNumber(AccountNumber);

    [NotMapped]
    public int NormalSign => LedgerAccountClasses.NormalSign(AccountClass);
}
