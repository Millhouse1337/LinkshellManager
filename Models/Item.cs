using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace LinkshellManagerDiscordApp.Models
{
    public class Item
    {
        [Key]
        public int Id { get; set; }

        public int LinkshellId { get; set; }

        [ForeignKey("LinkshellId")]
        public Linkshell? Linkshell { get; set; }
        // PHASE4B-DROP: Items.LinkshellName - a copy of the linkshell's name that nothing reads; LinkshellId is the link.
        public string? LinkshellName { get; set; }

        [Required]
        public required string ItemName { get; set; }
        public string? ItemType { get; set; }
        public int Quantity { get; set; }
        public string? Notes { get; set; }

        public string? CreatedByAppUserId { get; set; }
        public string? CreatedByCharacterName { get; set; }
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

        // --- Sold state: marking an item sold records the sale price and creates a
        // matching treasury entry so the treasury updates automatically. The
        // item stays in the list (flagged Sold) as a record; "unsell" reverses it. ---
        public bool IsSold { get; set; }
        public long? SoldPrice { get; set; }
        public DateTime? SoldAt { get; set; }
        public string? SoldByCharacterName { get; set; }
        // The old RevenueEntries row for a sale made before the treasury journal existed.
        // Replaced by JournalEntryId below; no code reads or writes it any more.
        // PHASE4B-DROP: Items.RevenueEntryId - goes with the RevenueEntries table.
        public int? RevenueEntryId { get; set; }

        // The treasury entry recorded for this sale. Unselling REVERSES it rather than deleting
        // it, so the fact that the item was once sold — and for how much — survives.
        public int? JournalEntryId { get; set; }
    }
}
