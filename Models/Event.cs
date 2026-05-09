using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace LinkshellManagerDiscordApp.Models;

public class Event
{
    [Key]
    public int Id { get; set; }

    public int LinkshellId { get; set; }

    [ForeignKey(nameof(LinkshellId))]
    public Linkshell? Linkshell { get; set; }

    public string? EventName { get; set; }

    public string? EventType { get; set; }

    public string? EventLocation { get; set; }

    public string? CreatorUserId { get; set; }

    // Set when the event transitions from queued to live (CommencementStartTime
    // gets stamped). Null while the event is still pending and for legacy rows
    // started before this field was introduced.
    public string? StarterUserId { get; set; }

    public DateTime? StartTime { get; set; }

    public DateTime? EndTime { get; set; }

    public DateTime? CommencementStartTime { get; set; }

    public double? Duration { get; set; }

    public int? DkpPerHour { get; set; }

    public double? EventDkp { get; set; }

    public string? Details { get; set; }

    // Identifies who created the event so clients can show source-specific
    // controls (e.g. the att addon shows a Cancel button only on rows it
    // created itself). Currently set to "Addon" by AddonApiController; null
    // for everything else (web app, legacy rows). Keep this short — it's an
    // internal discriminator, not a free-form label.
    [MaxLength(32)]
    public string? CreationSource { get; set; }

    // Forces the post-by-window count for events whose name isn't in
    // HnmConfig's curated 24/2/2 lookups. The addon's "Claim/Kill" style
    // sets this to 2 so a user-named event gets the same 2-post UI as a
    // ShortWindowHnm. Null = fall back to name-based detection (default).
    public int? WindowCountOverride { get; set; }

    public ICollection<Job> Jobs { get; set; } = new List<Job>();

    public ICollection<AppUserEvent> AppUserEvents { get; set; } = new List<AppUserEvent>();

    public ICollection<AppUserEventStatusLedger> StatusLedgerEntries { get; set; } = new List<AppUserEventStatusLedger>();

    public ICollection<EventLootDetail> EventLootDetails { get; set; } = new List<EventLootDetail>();

    public ICollection<EventAttendanceWindow> AttendanceWindows { get; set; } = new List<EventAttendanceWindow>();

    public DateTime? TimeStamp { get; set; }
}
