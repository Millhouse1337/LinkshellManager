using System.ComponentModel.DataAnnotations;
using LinkshellManagerDiscordApp.Models;

namespace LinkshellManagerDiscordApp.ViewModels;

public class ManageItemViewModel
{
    public int Id { get; set; }

    [Required]
    public int LinkshellId { get; set; }

    public string? LinkshellName { get; set; }

    [Required]
    [StringLength(256, MinimumLength = 1)]
    [Display(Name = "Item name")]
    public string ItemName { get; set; } = string.Empty;

    [StringLength(128)]
    [Display(Name = "Item type")]
    public string? ItemType { get; set; }

    [Range(0, int.MaxValue)]
    [Display(Name = "Quantity")]
    public int Quantity { get; set; }

    [StringLength(1024)]
    [Display(Name = "Notes")]
    public string? Notes { get; set; }

    public string? CreatedByCharacterName { get; set; }

    public DateTime CreatedAt { get; set; }

    // Orders the Sold archive, newest sale first.
    public DateTime UpdatedAt { get; set; }

    public bool IsSold { get; set; }
    public long? SoldPrice { get; set; }
    public string? SoldByCharacterName { get; set; }

    public bool CanManage { get; set; }

    public List<Linkshell> Linkshells { get; set; } = new();
}

// The Items card as it appears inside the one Treasury page.
//
// Its own model rather than an IEnumerable plus ViewBag, which is what the standalone Items page
// used: three loose ViewBag keys were fine for a view that owned the whole page, and are a poor
// contract for a component someone else embeds.
public class TreasuryItemsViewModel
{
    public IReadOnlyList<ManageItemViewModel> Items { get; set; } = Array.Empty<ManageItemViewModel>();

    // Leader-or-officer. See TreasuryItemsViewComponent for why this is not CanManageInventory.
    public bool CanManage { get; set; }

    // The Sold archive rather than the stockpile. Sold items are kept — the gil entry points at them
    // and "what did that Osode go for" is a real question — but a stash you can still sell from
    // should not be padded out with things already gone.
    public bool ShowingSold { get; set; }

    public IReadOnlyList<ManageItemViewModel> Stockpile =>
        Items.Where(item => !item.IsSold).ToList();

    // Most recently sold first: the archive only grows, and the newest sale is the interesting one.
    public IReadOnlyList<ManageItemViewModel> Sold =>
        Items.Where(item => item.IsSold).OrderByDescending(item => item.UpdatedAt).ToList();

    public IReadOnlyList<ManageItemViewModel> Shown => ShowingSold ? Sold : Stockpile;

    public long SoldTotal => Sold.Sum(item => item.SoldPrice ?? 0);

    // Names to suggest in the sell modal's "who sold it" box. Suggestions only — the box takes any
    // name, because the seller is the one left holding the gil and gil regularly sits on a mule
    // nobody has added to the roster.
    public IReadOnlyList<string> Roster { get; set; } = Array.Empty<string>();
}
