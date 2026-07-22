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

    // The DKP pool this loot was paid from. A ToD has a monster, not an event type, so the
    // pool defaults to whichever one "HNM" maps to and the officer can override it. Stamped
    // on the debit and read back on the refund, so removing the loot always credits the pool
    // it came out of even if the officer has remapped event types since.
    // Null = the linkshell's default pool.
    public int? DkpPoolId { get; set; }

    [ForeignKey(nameof(DkpPoolId))]
    public DkpPool? DkpPool { get; set; }

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
