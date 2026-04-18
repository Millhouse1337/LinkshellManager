using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace LinkshellManager.Models
{
    public class Rule
    {
        [Key]
        // Primary key
        public int Id { get; set; }

        // Foreign key
        public int LinkshellId { get; set; }

        // Navigation property
        [ForeignKey("LinkshellId")]
        public Linkshell? Linkshell { get; set; }
        public string? LinkshellName { get; set; }

        [Required]
        public required string RuleTitle { get; set; }
        [Required]
        public required string RuleDetails { get; set; }
    }
}
