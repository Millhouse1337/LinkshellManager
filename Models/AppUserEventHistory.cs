using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace LinkshellManagerDiscordApp.Models;

public class AppUserEventHistory
{
    [Key]
    public int Id { get; set; }

    public string? AppUserId { get; set; }

    [ForeignKey(nameof(AppUserId))]
    public AppUser? AppUser { get; set; }

    public int EventHistoryId { get; set; }

    [ForeignKey(nameof(EventHistoryId))]
    public EventHistory? EventHistory { get; set; }

    public string? CharacterName { get; set; }

    public string? JobName { get; set; }

    public string? SubJobName { get; set; }

    public string? JobType { get; set; }

    public DateTime? StartTime { get; set; }

    public double? Duration { get; set; }

    public double? EventDkp { get; set; }

    public bool IsQuickJoin { get; set; }

    public bool? IsVerified { get; set; }

    public string? Proctor { get; set; }

    // Per-member "active credit" for this event. Defaults to the event's
    // CountsTowardActive at close (so everyone is credited by default); leadership
    // can uncheck it on the event-history page for members who don't deserve it
    // (e.g. attended one window, skipped the rest). A credited row = an attendance
    // in the member's activity streak; uncredited (or absent) = an absence.
    public bool ActiveCredit { get; set; } = true;

    // How many of the camp's attendance windows this member was scanned in, on a WINDOWED event
    // (HNM Style / Claim-Kill). Null on a timed event, where presence is measured in Duration
    // instead — the two are alternatives, not companions.
    //
    // Computed at close by both end-event paths and, until the archive existed, immediately
    // discarded: it is the numerator of the DKP those events pay (windows x DkpPerWindow), so
    // without it a closed camp's payout could not be explained from its own history row.
    public int? WindowsAttended { get; set; }
}
