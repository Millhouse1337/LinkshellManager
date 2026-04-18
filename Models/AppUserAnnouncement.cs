using System.ComponentModel.DataAnnotations.Schema;
using System.ComponentModel.DataAnnotations;

namespace LinkshellManager.Models
{
    public class AppUserAnnouncement
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
        public int AnnouncementId { get; set; }

        // Navigation property
        [ForeignKey("AnnouncementId")]
        public Announcement? Announcement { get; set; }

        public string? AnnouncementCreator { get; set; }
    }
}
