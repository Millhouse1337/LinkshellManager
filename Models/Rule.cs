using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace LinkshellManagerDiscordApp.Models
{
    public class Rule
    {
        [Key]
        public int Id { get; set; }

        public int LinkshellId { get; set; }

        [ForeignKey("LinkshellId")]
        public Linkshell? Linkshell { get; set; }
        // PHASE4B-DROP: Rules.LinkshellName - a copy of the linkshell's name that nothing reads; LinkshellId is the link.
        public string? LinkshellName { get; set; }

        [Required]
        public required string RuleTitle { get; set; }
        [Required]
        public required string RuleDetails { get; set; }

        // Optional short label shown as the colored category tag (e.g. "Overview",
        // "DKP", "Loot"); drives the card's accent color + icon. Null = untagged.
        [MaxLength(32)]
        public string? Category { get; set; }

        public string? CreatedByAppUserId { get; set; }
        public string? CreatedByCharacterName { get; set; }
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    }
}
