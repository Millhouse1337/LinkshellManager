using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace LinkshellManagerDiscordApp.Models;

public class Bid
{
    [Key]
    public int Id { get; set; }

    public int AuctionItemId { get; set; }

    [ForeignKey(nameof(AuctionItemId))]
    public AuctionItem? AuctionItem { get; set; }

    [MaxLength(450)]
    public string? AppUserId { get; set; }

    [ForeignKey(nameof(AppUserId))]
    public AppUser? AppUser { get; set; }

    [MaxLength(256)]
    public string CharacterName { get; set; } = string.Empty;

    public int BidAmount { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
