using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace LinkshellManagerDiscordApp.Models;

public class TodLootDetail
{
    [Key]
    public int Id { get; set; }

    public int? TodId { get; set; }

    [ForeignKey(nameof(TodId))]
    public Tod? Tod { get; set; }

    public string? ItemName { get; set; }

    public string? ItemWinner { get; set; }

    public int? WinningDkpSpent { get; set; }

    public double? ActualDeductedDkp { get; set; }

    // Audit fields populated by LootEditService when an officer corrects this
    // row. Read by the loot-history list to show an "Edited" tag and the most
    // recent reason on hover; the full audit trail lives in DkpLedgerEntry
    // pairs (LootEditRefund + LootEditSpent) tagged with the same reason.
    public DateTime? EditedAt { get; set; }

    [MaxLength(450)]
    public string? EditedByAppUserId { get; set; }

    [MaxLength(256)]
    public string? EditedByCharacterName { get; set; }

    [MaxLength(512)]
    public string? LastEditReason { get; set; }
}
