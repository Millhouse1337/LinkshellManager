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

    // The name the roster read actually SAW this window, which is not necessarily the member's
    // main: the addon scans whatever character is standing there, and a player on an alt is
    // matched to their account through AppUser.AltCharacterName1/2. Recording the main here (what
    // this used to do) threw away the one fact an officer reading the window wants — which
    // character was actually at camp.
    [MaxLength(256)]
    public string? CharacterName { get; set; }

    // The member's roster main, stored ONLY when CharacterName above is one of their alts; null
    // when they were scanned on their main. That makes it both the display hint ("Athmilk (alt of
    // Edicius)") and the flag for whether to show one at all, with no read-time membership lookup
    // on the polled Activity overview. Denormalized for the same reason as the fields above: the
    // row has to keep identifying a person after the participation is cleared away.
    [MaxLength(256)]
    public string? MainCharacterName { get; set; }

    public int EventAttendanceWindowId { get; set; }

    [ForeignKey(nameof(EventAttendanceWindowId))]
    public EventAttendanceWindow? EventAttendanceWindow { get; set; }

    public DateTime VerifiedAt { get; set; }

    [MaxLength(256)]
    public string? VerifiedBy { get; set; }

    // Which alliance this attendee was posted from, and the identity that alliance was recognised
    // by (its leader's character name, or the first poster's when the game reports no leader).
    //
    // The FFXI client can only see your OWN alliance -- party memory slots 0-17 -- so a camp
    // fielding two alliances needs a poster in each, and without this the server cannot tell their
    // posts apart. The addon has been SENDING an alliance number on every window post since the
    // per-alliance work; AddonAttendanceRequest had no field for it, so the model binder dropped it
    // silently and the whole window path stayed alliance-blind.
    //
    // NULL means "posted before this existed", NOT alliance 1 -- the same distinction
    // AttendanceSnapshot.AllianceNumber draws, and for the same reason.
    public int? AllianceNumber { get; set; }

    [MaxLength(256)]
    public string? AllianceKey { get; set; }

    // Zone the character was scanned in when this window was posted. Stored
    // verbatim from the addon's attendance entry so the addon can rehydrate
    // the same display ("Name (Zone | Job/Sub)") after a reload.
    [MaxLength(64)]
    public string? Zone { get; set; }
}
