using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace LinkshellManagerDiscordApp.Models;

// One row per attendance "window" posted for an HNM event. Non-HNM events
// (HnmConfig.GetWindowCount == 1) typically have no rows here at all — their
// attendance lives directly on AppUserEvent as it always has.
public class EventAttendanceWindow
{
    [Key]
    public int Id { get; set; }

    public int EventId { get; set; }

    [ForeignKey(nameof(EventId))]
    public Event? Event { get; set; }

    // 1-based ordinal within the parent event (1..GetWindowCount).
    public int SequenceNumber { get; set; }

    // "On Time" / "Claim/Kill" for 2-window HNMs; null for numbered (24-window) HNMs
    // — view layer falls back to "Window {SequenceNumber}".
    [MaxLength(64)]
    public string? Label { get; set; }

    public DateTime PostedAt { get; set; }

    [MaxLength(64)]
    public string? PostedBySource { get; set; }

    public double? DkpAmount { get; set; }

    // Idempotency stamp for AttInput appends. Set once after a successful
    // append so retries / reposts don't duplicate rows in the sheet.
    public DateTime? AttInputAppendedAt { get; set; }

    public ICollection<AppUserEventWindow> Attendees { get; set; } = new List<AppUserEventWindow>();
}
