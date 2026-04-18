using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace LinkshellManagerDiscordApp.Models;

public class AuctionItem
{
    [Key]
    public int Id { get; set; }

    public int? AuctionId { get; set; }

    [ForeignKey(nameof(AuctionId))]
    public Auction? Auction { get; set; }

    public int? AuctionHistoryId { get; set; }

    [ForeignKey(nameof(AuctionHistoryId))]
    public AuctionHistory? AuctionHistory { get; set; }

    [MaxLength(256)]
    public string? ItemName { get; set; }

    [MaxLength(128)]
    public string? ItemType { get; set; }

    public int? StartingBidDkp { get; set; }

    public List<Bid> Bids { get; set; } = new();

    public int? CurrentHighestBid { get; set; }

    [MaxLength(256)]
    public string? CurrentHighestBidder { get; set; }

    [MaxLength(450)]
    public string? CurrentHighestBidderAppUserId { get; set; }

    public int? EndingBidDkp { get; set; }

    [DataType(DataType.DateTime)]
    public DateTime? StartTime { get; set; }

    [DataType(DataType.DateTime)]
    public DateTime? EndTime { get; set; }

    [MaxLength(32)]
    public string? Status { get; set; }

    [MaxLength(1024)]
    public string? Notes { get; set; }
}
