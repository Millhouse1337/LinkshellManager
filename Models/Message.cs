using System.ComponentModel.DataAnnotations.Schema;
using System.ComponentModel.DataAnnotations;

namespace LinkshellManager.Models
{
    public class Message
    {
        [Key]
        // Primary key
        public int Id { get; set; }

        // Foreign key
        public string? AppUserId { get; set; }

        // Navigation property
        [ForeignKey("AppUserId")]
        public AppUser? AppUser { get; set; }
        public string? CharacterNameSender { get; set; }
        public String? MessageDetails { get; set; }

        [DataType(DataType.DateTime)]
        public DateTime? TimeStamp { get; set; }

        public ICollection<AppUserMessage>? AppUserMessages { get; set; }
    }
}
