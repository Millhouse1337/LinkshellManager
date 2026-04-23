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
    }
}
