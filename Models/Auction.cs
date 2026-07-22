using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace LinkshellManagerDiscordApp.Models;

public class Auction
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

    // The DKP pool bids are drawn from, chosen by the officer at create time. An auction has
    // no event type of its own, so unlike event loot this can't be derived — it has to be
    // picked. Null = the linkshell's default pool. Locked once a bid exists (a bidder
    // validated against one pool's balance must not be debited from another), and copied to
    // AuctionHistory.DkpPoolId at close because this row is deleted there.
    public int? DkpPoolId { get; set; }

    [ForeignKey(nameof(DkpPoolId))]
    public DkpPool? DkpPool { get; set; }

    // Discord channel id of the per-auction channel created (when the
    // linkshell has bot config). Null if not created. Carried into
    // AuctionHistory on close so the channel can be cleaned up.
    [MaxLength(32)]
    public string? DiscordChannelId { get; set; }

    // Discord message id of the latest bot-posted card for this auction.
    [MaxLength(32)]
    public string? DiscordMessageId { get; set; }

    // Every bot-posted card message id for this auction (the "opened" card plus
    // one per bid), comma-separated. Captured so all of them can be deleted from
    // the channel when the auction closes, leaving just the results summary.
    // Empty/null for webhook-only auctions (those can't be bot-deleted).
    public string? DiscordMessageIds { get; set; }

    public ICollection<AuctionItem> AuctionItems { get; set; } = new List<AuctionItem>();
}
