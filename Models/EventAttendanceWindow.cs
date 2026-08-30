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

    // NULLABLE since the archive re-parent. A window has to OUTLIVE the camp it was posted on: at
    // close, EndEventCoreAsync moves every window onto the new EventHistory and clears this FK, so
    // the Event delete that follows cannot cascade the camp's attendance record away. Null here
    // with EventHistoryId set = an archived window; set here = a window on a live camp. Same trick
    // EventLootDetail already uses to survive close.
    public int? EventId { get; set; }

    [ForeignKey(nameof(EventId))]
    public Event? Event { get; set; }

    // Set at event close, when EventId above is cleared. Null while the camp is live -- the Event
    // FK answers then. Cascade on delete: removing an archived event should take its window
    // history with it, unlike the live-camp FK which is deliberately unhooked first.
    public int? EventHistoryId { get; set; }

    [ForeignKey(nameof(EventHistoryId))]
    public EventHistory? EventHistory { get; set; }

    // 1-based ordinal within the parent event (1..GetWindowCount).
    public int SequenceNumber { get; set; }

    // "Open" / "Close" for 2-window HNMs; null for numbered (24-window) HNMs — view layer
    // falls back to "Window {SequenceNumber}". Rows written before the rename still hold
    // "On Time" / "Claim/Kill"; read them through HnmConfig.NormalizeWindowLabel, never raw.
    [MaxLength(64)]
    public string? Label { get; set; }

    public DateTime PostedAt { get; set; }

    [MaxLength(64)]
    public string? PostedBySource { get; set; }

    public double? DkpAmount { get; set; }

    // The officer's explicit "this is the camp's closing window" mark, set from the checkbox on the
    // HNM event's window list.
    //
    // Replaces the old DERIVED close, which was "the highest sequence posted so far". That guess
    // was wrong in the only way that costs DKP: it made EVERY window look like the close while it
    // was the newest one, and HnmCampPricing.DefaultWindowValue quoted the close bonus on every
    // post because of it. The addon then wrote that quote back as an explicit DkpAmount, freezing
    // the close bonus into every window on the camp. See ResolveCloseWindow for the fallback that
    // keeps pre-existing camps paying.
    //
    // At most one window per event carries it — SetClosingWindowAsync clears the others rather
    // than trusting callers to.
    public bool IsClosingWindow { get; set; }

    // True for the roster read the addon's "Post Kill" button files: who was actually standing
    // there when the mob died.
    //
    // Its own row rather than extra attendees folded into the close, because the two rosters differ
    // on purpose — people turn up for the kill who were not at the camp for the window, and an
    // officer needs to see which is which. Deliberately NOT eligible to be the close window
    // (ResolveCloseWindow skips these), or Post Kill would silently move the close bonus off the
    // window the officer marked.
    //
    // The window itself pays nothing; being IN it is what earns the kill bonus. See WindowValue.
    public bool IsKillWindow { get; set; }

    // Idempotency stamp for AttInput appends. Set once after a successful
    // append so retries / reposts don't duplicate rows in the sheet.
    public DateTime? AttInputAppendedAt { get; set; }

    public ICollection<AppUserEventWindow> Attendees { get; set; } = new List<AppUserEventWindow>();
}
