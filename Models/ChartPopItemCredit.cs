using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace LinkshellManagerDiscordApp.Models;

/// <summary>
/// One person credited with farming one pop item.
///
/// This is the ONLY place farming credit is stored. The Farming Credit Ledger's member × boss grid —
/// its Credited / Partial / None states, its per-cell "6 / 8", and the "49 / 63  78%" total — is
/// derived from these rows per request, never stored. The boss card's per-item farmer count and the
/// ledger's per-boss cell are two views of the same fact, and only the finer grain can be the stored
/// one; deriving the coarser view is what makes it impossible for the two halves of the page to
/// disagree.
/// </summary>
public class ChartPopItemCredit
{
    [Key]
    public int Id { get; set; }

    public int ChartPopItemId { get; set; }

    [ForeignKey(nameof(ChartPopItemId))]
    public ChartPopItem? ChartPopItem { get; set; }

    // Deliberately a plain indexed int with NO relationship. Linkshells already cascades into
    // ChartPopItems, which cascades into here; configuring a second Linkshell relationship would be
    // a second cascade path from Linkshells into this table, which Postgres refuses. Same reason and
    // same shape as JournalEntryLine.LinkshellId. These rows still die with the linkshell, via
    // ChartPopItems. It is carried so the ledger can be built without joining back through the item.
    public int LinkshellId { get; set; }

    /// <summary>AppUserLinkshells.Id when the farmer is on the roster; null for an unsynced name.</summary>
    public int? MembershipId { get; set; }

    [MaxLength(256)]
    public string CharacterName { get; set; } = string.Empty;

    /// <summary>Optional free note on this one credit ("farmed 3", "traded for it").</summary>
    [MaxLength(256)]
    public string? Detail { get; set; }

    [MaxLength(450)]
    public string? CreditedByAppUserId { get; set; }

    [MaxLength(256)]
    public string? CreditedByCharacterName { get; set; }

    public DateTime CreditedAt { get; set; } = DateTime.UtcNow;
}
