using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace LinkshellManagerDiscordApp.Models
{
    public class RevenueEntry
    {
        [Key]
        public int Id { get; set; }

        public int LinkshellId { get; set; }

        [ForeignKey("LinkshellId")]
        public Linkshell? Linkshell { get; set; }
        public string? LinkshellName { get; set; }

        [Required]
        public required string EntryType { get; set; }

        public string? Category { get; set; }
        public long Value { get; set; }
        public string? Details { get; set; }
        public DateTime OccurredAt { get; set; } = DateTime.UtcNow;

        public string? CreatedByAppUserId { get; set; }
        public string? CreatedByCharacterName { get; set; }
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    }
}
