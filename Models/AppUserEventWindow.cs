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

    // NULLABLE, with SetNull on delete (see ApplicationDbContext). A snapshot has to OUTLIVE the
    // participation it was taken from: the 25-window wyrm camps clear their roster every window
    // (EventPartySignupService.ClearWindowRosterAsync), and while this cascaded, every hour's
    // snapshots were deleted along with the participations — so a Standard wyrm camp could only
    // ever pay from its final hour, and automatic per-window snapshots would have thrown away 24
    // of every 25 windows they captured.
    public int? AppUserEventId { get; set; }

    [ForeignKey(nameof(AppUserEventId))]
    public AppUserEvent? AppUserEvent { get; set; }

    // WHO this snapshot recorded, denormalized so the row still identifies a person after the
    // participation is gone. Stamped at creation from the resolved membership. AppUserId is what
    // HnmStandardCampFinalizer folds on to award credit; CharacterName is for display and for
    // account-less ("outside") scans that have no AppUserId.
    [MaxLength(450)]
    public string? AppUserId { get; set; }

    [MaxLength(256)]
    public string? CharacterName { get; set; }

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
