using LinkshellManagerDiscordApp.Models;

namespace LinkshellManagerDiscordApp.ViewModels;

public class AuctionHistoryViewModel
{
    public AuctionHistory AuctionHistory { get; set; } = new();
    public List<AuctionItem> AuctionItems { get; set; } = new();
    public DateTime? StartTime { get; set; }
    public DateTime? EndTime { get; set; }
}
