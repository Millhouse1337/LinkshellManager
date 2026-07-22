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

    // Copied from Auction.DkpPoolId at close (the Auction row is deleted there), so the
    // closed-auction card can still say which DKP pool the winners paid from.
    // Null = the linkshell's default pool.
    public int? DkpPoolId { get; set; }

    [ForeignKey(nameof(DkpPoolId))]
    public DkpPool? DkpPool { get; set; }

    // Copied from Auction.DiscordChannelId at close time so the auction's
    // Discord channel can have its results posted and then be deleted. Null
    // when no per-auction channel was created.
    [MaxLength(32)]
    public string? DiscordChannelId { get; set; }

    public ICollection<AuctionItem> AuctionItems { get; set; } = new List<AuctionItem>();
}
