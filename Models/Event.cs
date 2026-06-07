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
    // controls (e.g. the lsm addon shows a Cancel button only on rows it
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

    // Maps this event to a row category in the linkshell's Google Sheet AttInput
    // tab. Examples: "Misc Camp", "Kill", "Kings Camp", "Wyrms Camp". Null means
    // "do not append AttInput rows for this event" -- snapshots / window posts /
    // event-close awards for this event simply skip the sheet.
    [MaxLength(32)]
    public string? AttInputEntryType { get; set; }

    // Idempotency stamp for the event-close AttInput append. Set once after
    // the row batch is successfully written so a retry / re-end doesn't
    // create duplicate AttInput rows. Null = not yet appended.
    public DateTime? AttInputAppendedAt { get; set; }

    // Set when this event was auto-created from a ToD post (HNM workflow).
    // Links back to the originating Tod row so the HNM dashboard can render
    // the source ToD alongside the queued event. Null for manually-created
    // events.
    public int? SourceTodId { get; set; }

    [ForeignKey(nameof(SourceTodId))]
    public Tod? SourceTod { get; set; }

    // Optional link to a pre-built PartySetup (alliances → parties → slots,
    // monster-tagged, per-linkshell). Replaces the old inline "Minimal Party
    // Setup" job rows. SetNull on PartySetup delete so an event isn't
    // cascade-removed when an officer cleans up old setups.
    public int? PartySetupId { get; set; }

    [ForeignKey(nameof(PartySetupId))]
    public PartySetup? PartySetup { get; set; }

    // Set once the event has been announced to a Discord channel by the bot.
    // ChannelId + MessageId let the interactions endpoint edit that same message
    // in place (refresh the signup roster) when someone clicks a job/withdraw.
    // Null until posted (or when no channel is configured for the event's type).
    [MaxLength(20)]
    public string? DiscordChannelId { get; set; }

    [MaxLength(20)]
    public string? DiscordMessageId { get; set; }

    public ICollection<AppUserEvent> AppUserEvents { get; set; } = new List<AppUserEvent>();

    public ICollection<AppUserEventStatusLedger> StatusLedgerEntries { get; set; } = new List<AppUserEventStatusLedger>();

    public ICollection<EventLootDetail> EventLootDetails { get; set; } = new List<EventLootDetail>();

    public ICollection<EventAttendanceWindow> AttendanceWindows { get; set; } = new List<EventAttendanceWindow>();

    public DateTime? TimeStamp { get; set; }
}
