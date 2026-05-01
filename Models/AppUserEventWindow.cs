using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace LinkshellManagerDiscordApp.Models;

// Join row recording that a user's overall AppUserEvent participation included
// a specific HNM attendance window. A composite unique index on
// (AppUserEventId, EventAttendanceWindowId) keeps re-posts from double-counting.
public class AppUserEventWindow
{
    [Key]
    public int Id { get; set; }

    public int AppUserEventId { get; set; }

    [ForeignKey(nameof(AppUserEventId))]
    public AppUserEvent? AppUserEvent { get; set; }

    public int EventAttendanceWindowId { get; set; }

    [ForeignKey(nameof(EventAttendanceWindowId))]
    public EventAttendanceWindow? EventAttendanceWindow { get; set; }

    public DateTime VerifiedAt { get; set; }

    [MaxLength(256)]
    public string? VerifiedBy { get; set; }

    // Zone the character was scanned in when this window was posted. Stored
    // verbatim from the addon's attendance entry so the addon can rehydrate
    // the same display ("Name (Zone | Job/Sub)") after a reload.
    [MaxLength(64)]
    public string? Zone { get; set; }
}
