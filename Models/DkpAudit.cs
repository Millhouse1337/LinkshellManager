using System.ComponentModel.DataAnnotations.Schema;
using System.ComponentModel.DataAnnotations;

namespace LinkshellManager.Models
{
    public class DkpAudit
    {
        [Key]
        // Primary key
        public int Id { get; set; }

        // Foreign key
        public int? AppUserLinkshellId { get; set; }

        // Navigation property
        [ForeignKey("AppUserLinkshellId")]
        public AppUserLinkshell? AppUserLinkshell { get; set; }
        public double? PreviousDkp { get; set; }
        public double? NewDkp { get; set; }
        public string? Details { get; set; }

    }
}
