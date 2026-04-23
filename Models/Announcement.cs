using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace LinkshellManagerDiscordApp.Models
{
    public class Announcement
    {
        [Key]
        public int Id { get; set; }

        public int LinkshellId { get; set; }

        [ForeignKey("LinkshellId")]
        public Linkshell? Linkshell { get; set; }
        public string? LinkshellName { get; set; }

        [Required]
        public required string AnnouncementTitle { get; set; }
        [Required]
        public required string AnnouncementDetails { get; set; }

        public string? CreatedByAppUserId { get; set; }
        public string? CreatedByCharacterName { get; set; }
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    }
}
