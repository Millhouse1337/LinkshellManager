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

    // Which DKP pool bids are drawn from (posted back). Null = the linkshell's default pool.
    public int? DkpPoolId { get; set; }

    // The linkshell's pools, for the picker. Only rendered when there's more than one — a linkshell
    // that hasn't split its DKP sees the form exactly as before.
    public List<AuctionDkpPoolOption> DkpPools { get; set; } = new();

    // Locked once a bid exists: bidders were validated against the current pool's balance.
    public bool DkpPoolLocked { get; set; }
}

public sealed class AuctionDkpPoolOption
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

public sealed class AuctionSourceItemOption
{
    public int Id { get; set; }
    public string ItemName { get; set; } = string.Empty;
    public string? ItemType { get; set; }
    public int Quantity { get; set; }
}
