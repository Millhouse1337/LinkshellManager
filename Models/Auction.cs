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

    // Discord channel id of the per-auction channel created (when the
    // linkshell has bot config). Null if not created. Carried into
    // AuctionHistory on close so the channel can be cleaned up.
    [MaxLength(32)]
    public string? DiscordChannelId { get; set; }

    // Discord message id of the bot-posted "auction opened" message. Stored so a
    // bid can edit that same message in place (current high bid per item) instead
    // of spamming a new message per bid. Null when posted via webhook (which
    // can't be edited) or when no bot channel is configured.
    [MaxLength(32)]
    public string? DiscordMessageId { get; set; }

    public ICollection<AuctionItem> AuctionItems { get; set; } = new List<AuctionItem>();
}
