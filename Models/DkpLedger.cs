using System.ComponentModel.DataAnnotations.Schema;
using System.ComponentModel.DataAnnotations;

namespace LinkshellManager.Models
{
    public class DkpLedger
    {
        [Key]
        // Primary key
        public int Id { get; set; }

        // Foreign key
        public int? AppUserLinkshellId { get; set; }

        // Navigation property
        [ForeignKey("AppUserLinkshellId")]
        public AppUserLinkshell? AppUserLinkshell { get; set; }

        public string? DkpType { get; set; }
        public string? TransactionType { get; set; }
        public double? TransactionValue { get; set; }
        public double? PreviousDkp { get; set; }
        public double? NewDkp { get; set; }
        [DataType(DataType.DateTime)]
        public DateTime? TimeStamp { get; set; }

    }
}
