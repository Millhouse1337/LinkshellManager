using System.ComponentModel.DataAnnotations.Schema;
using System.ComponentModel.DataAnnotations;

namespace LinkshellManager.Models
{
    public class Income
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

        public string? MethodOfIncome { get; set; }

        public int? Value { get; set; }

        public string? Details { get; set; }

        [DataType(DataType.DateTime)]
        public DateTime? TimeStamp { get; set; }
    }
}
