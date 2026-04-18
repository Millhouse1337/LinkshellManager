using LinkshellManagerDiscordApp.Models;

namespace LinkshellManagerDiscordApp.ViewModels;

public class AuctionViewModel
{
    public int LinkshellId { get; set; }
    public List<Linkshell> Linkshells { get; set; } = new();
    public Auction Auction { get; set; } = new();
    public List<AuctionItem> AuctionItems { get; set; } = new();
}
