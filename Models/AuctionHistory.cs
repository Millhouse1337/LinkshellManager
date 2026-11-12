using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace LinkshellManagerDiscordApp.Models;

public class AuctionHistory
{
    [Key]
    public int Id { get; set; }

    public int LinkshellId { get; set; }

    [ForeignKey(nameof(LinkshellId))]
    public Linkshell? Linkshell { get; set; }

    [MaxLength(256)]
    public string? AuctionTitle { get; set; }

    [MaxLength(450)]
    public string? CreatedByUserId { get; set; }

    [MaxLength(256)]
    public string? CreatedBy { get; set; }

    [DataType(DataType.DateTime)]
    public DateTime? StartTime { get; set; }

    [DataType(DataType.DateTime)]
    public DateTime? EndTime { get; set; }

    [DataType(DataType.DateTime)]
    public DateTime? StartedAt { get; set; }

    public DateTime ClosedAt { get; set; } = DateTime.UtcNow;

    // PHASE4B-DROP: AuctionHistories.DkpPoolId (+ its FK to DkpPools) - no longer written at close and
    // never read; each winner's ledger row is pinned to the pool that paid. Old rows still hold
    // values, so DkpPoolEditor keeps repointing them until 4B drops the column.
    public int? DkpPoolId { get; set; }

    [ForeignKey(nameof(DkpPoolId))]
    public DkpPool? DkpPool { get; set; }

    // PHASE4B-DROP: AuctionHistories.DiscordChannelId - never written or read (batch C1).
    [MaxLength(32)]
    public string? DiscordChannelId { get; set; }

    public ICollection<AuctionItem> AuctionItems { get; set; } = new List<AuctionItem>();
}
