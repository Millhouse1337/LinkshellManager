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
        // matching Income RevenueEntry so the treasury updates automatically. The
        // item stays in the list (flagged Sold) as a record; "unsell" reverses it. ---
        public bool IsSold { get; set; }
        public long? SoldPrice { get; set; }
        public DateTime? SoldAt { get; set; }
        public string? SoldByCharacterName { get; set; }
        // The RevenueEntry created for this sale, so unselling can remove exactly it.
        //
        // SUPERSEDED by JournalEntryId below. Both are written for one release so rolling the
        // app binary back is a non-event; the column goes when RevenueEntries does.
        public int? RevenueEntryId { get; set; }

        // The treasury entry recorded for this sale. Unselling REVERSES it rather than deleting
        // it, so the fact that the item was once sold — and for how much — survives.
        public int? JournalEntryId { get; set; }
    }
}
