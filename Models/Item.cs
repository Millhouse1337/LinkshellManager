using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace LinkshellManager.Models
{
    public class Item
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
        public string? ItemName { get; set; }
        public string? ItemType { get; set; }
        public int? Quantity { get; set; }
        public string? Notes { get; set; }
        
        [DataType(DataType.DateTime)]
        public DateTime? TimeStamp { get; set; }
    }
}
