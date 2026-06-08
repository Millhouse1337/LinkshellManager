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
}
