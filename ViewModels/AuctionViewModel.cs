using LinkshellManagerDiscordApp.Models;

namespace LinkshellManagerDiscordApp.ViewModels;

public class AuctionViewModel
{
    public int LinkshellId { get; set; }
    public List<Linkshell> Linkshells { get; set; } = new();
    public Auction Auction { get; set; } = new();
    public List<AuctionItem> AuctionItems { get; set; } = new();

    // Inventory items eligible to back an auction item via SourceItemId.
    // Populated server-side in BuildAuctionViewModelAsync; not posted back —
    // the controller validates posted SourceItemIds against the linkshell on
    // submit.
    public List<AuctionSourceItemOption> SourceItems { get; set; } = new();
}

public sealed class AuctionSourceItemOption
{
    public int Id { get; set; }
    public string ItemName { get; set; } = string.Empty;
    public string? ItemType { get; set; }
    public int Quantity { get; set; }
}
