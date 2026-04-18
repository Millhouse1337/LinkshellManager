using System.ComponentModel.DataAnnotations.Schema;
using System.ComponentModel.DataAnnotations;

namespace LinkshellManager.Models
{
    public class AppUserMessage
    {
        [Key]
        // Primary key
        public int Id { get; set; }

        // Foreign key
        public string? AppUserId { get; set; }

        // Navigation property
        [ForeignKey("AppUserId")]
        public AppUser? AppUser { get; set; }

        // Foreign key
        public int MessageId { get; set; }

        // Navigation property
        [ForeignKey("MessageId")]
        public Message? Message { get; set; }
        public string? CharacterNameSender { get; set; }
        public string? CharacterNameReceiver { get; set; }
        public string? MessageDetails { get; set; }

        [DataType(DataType.DateTime)]
        public DateTime? TimeStamp { get; set; }
    }
}
