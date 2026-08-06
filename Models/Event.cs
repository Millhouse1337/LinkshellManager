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

    // Canonical HNM monster name (e.g. "Nidhogg"), matching Tod.MonsterName and
    // PartySetup.AssignedMonsterName, for manually-created HNM signup boards. It's
    // the authoritative monster source for the ToD-driven recurrence (don't parse
    // EventName). Null for non-HNM events.
    [MaxLength(256)]
    public string? AssignedMonsterName { get; set; }

    public string? CreatorUserId { get; set; }

    // Set when the event transitions from queued to live (CommencementStartTime
    // gets stamped). Null while the event is still pending and for legacy rows
    // started before this field was introduced.
    public string? StarterUserId { get; set; }

    public DateTime? StartTime { get; set; }

    public DateTime? EndTime { get; set; }

    public DateTime? CommencementStartTime { get; set; }

    // When true, a background service stamps CommencementStartTime (i.e. "starts"
    // the event) automatically once StartTime is reached, instead of waiting for
    // an officer to click Start. Opt-in per event; never applies to HNM events
    // (those are driven by the in-game addon). Default false.
    public bool AutoStart { get; set; }

    // When true, this event participates in the member activity (Active/Inactive)
    // calculation: at close every attendee defaults to earning "active credit",
    // which leadership can then reconcile. False = the event is ignored entirely
    // by activity tracking. Default true. Copied to EventHistory at close.
    public bool CountsTowardActive { get; set; } = true;

    public double? Duration { get; set; }

    public int? DkpPerHour { get; set; }

    public double? EventDkp { get; set; }

    // Per-camp overrides for the HNM payout amounts. Null (the default) means "use the
    // linkshell's setting", so a camp created without touching them pays exactly what it
    // always did. Set by the Activity's create / edit event form ("Change DKP") when one
    // camp is worth more or less than the linkshell's usual rate — a pop nobody wants to
    // sit, a rare wyrm, and so on.
    //
    // Which ones apply depends on the linkshell's HnmAttendanceMode, because the two modes
    // pay on different shapes:
    //   Standard (HnmStandardCampFinalizer) — Open + Close + Claim + Kill
    //   Wd       (WdCampFinalizer)          — PerWindow × windows + Claim + Kill
    // Claim and Kill are deliberately SHARED by both: a camp runs in exactly one mode, and
    // "what this camp pays for the claim" means the same thing either way. Open/Close are
    // Standard-only and PerWindow is Wd-only; the inapplicable ones stay null.
    public double? HnmOpenBonusOverride { get; set; }

    public double? HnmCloseBonusOverride { get; set; }

    public double? HnmClaimBonusOverride { get; set; }

    public double? HnmKillBonusOverride { get; set; }

    public double? HnmPerWindowOverride { get; set; }

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

    // Idempotency stamp left over from the (removed) event-close sheet append.
    // Retained as a harmless nullable column; no longer written.
    public DateTime? AttInputAppendedAt { get; set; }

    // Set when this event was auto-created from a ToD post (HNM workflow).
    // Links back to the originating Tod row so the HNM dashboard can render
    // the source ToD alongside the queued event. Null for manually-created
    // events.
    public int? SourceTodId { get; set; }

    [ForeignKey(nameof(SourceTodId))]
    public Tod? SourceTod { get; set; }

    // HNM signup-board "defeated / awaiting re-post" state. Set when an officer logs the
    // monster's Time of Death from the board's "Post ToD" button: the board's signups are
    // wiped, StartTime is moved to the predicted repop, and the Discord message is replaced
    // with a "defeated" note. The recurring-board poller clears this and re-posts the SAME
    // board LeadHours before the pop (HnmRepostAt). Null = the board is live/accepting signups.
    public DateTime? HnmDefeatedAt { get; set; }

    // When the defeated board will automatically re-post (= repop − LeadHours), or null if
    // the event has no recurring board (Repeat-on-ToD off) so it won't auto-re-post. Display
    // only; the poller computes the actual window from the ToD + the board's LeadHours.
    public DateTime? HnmRepostAt { get; set; }

    // Multi-window pop counter (1..25) for the window-cycle HNMs (Tiamat / Jormungand /
    // Vrtra), which pop across successive windows after a ToD. The Discord board for those
    // monsters shows "Window N", and HnmWindowAdvanceBackgroundService is the ONLY thing that
    // moves this — there is no manual step button. 1 for every other event; reset to 1 on
    // board re-post.
    public int HnmWindowNumber { get; set; } = 1;

    // The highest window whose turnover has been settled, so a wyrm's roster is cleared exactly
    // once per window even though the advancer polls every few seconds.
    //
    // Only wyrm boards clear a roster at all (HnmConfig.WindowAdvanceWipesRoster); on the
    // kings/dragons settling is bookkeeping only, since their windows are one continuous camp.
    //
    // Null reads as 1: window 1 is the camp's opening roster and is never cleared, so a fresh
    // board falls straight through rather than wiping everyone who just signed up.
    public int? HnmClearedWindow { get; set; }

    // Optional "Day N" label for an HNM signup board — set on the create-event form and
    // shown on the board (Activity + Discord). Free-form day counter (e.g. which day of
    // the camp); null = not shown. Distinct from HnmWindowNumber (the spawn-window cycle).
    public int? DayNumber { get; set; }

    // Manual Check In attendance state. AttendanceMode is stamped "Wd" (HnmAttendanceModes.Wd) at
    // create time for HNM events in a Manual Check In linkshell; null = Standard (every existing/
    // Standard event stays on the current code paths). All Manual Check In behavior is gated on this.
    [MaxLength(16)]
    public string? AttendanceMode { get; set; }

    // Two-phase close for Manual Check In camps. When the officer logs the ToD the camp enters "Awaiting
    // Processing" (WdAwaitingProcessingSince set) instead of finalizing immediately — late
    // x-ins/corrections still work during the grace period. WdFinalizedAt is stamped once DKP
    // has been written (idempotency guard: a second finalize is a no-op). WdClaimed/WdKilled
    // capture whether the camp claimed/killed, driving the claim/kill bonuses at finalize.
    public DateTime? WdAwaitingProcessingSince { get; set; }
    public DateTime? WdFinalizedAt { get; set; }
    public bool WdClaimed { get; set; }
    public bool WdKilled { get; set; }

    // The window the monster popped on (set by the officer at "Pop / End Camp"). Caps credit —
    // nobody is paid past this window even if the window counter auto-advanced further while the
    // group was fighting. Null = fall back to HnmWindowNumber (the current window) at finalize.
    public int? WdPopWindow { get; set; }

    // For a live timed camp, the UTC time the NEXT window opens (= WindowAnchorAt + HnmWindowNumber ×
    // WindowMinutes) — a real clock boundary off the fixed grid, not "one cadence from the last
    // click". Stamped at auto-start and on each manual Prev/Next so the board can show a live
    // countdown. Null when the camp isn't on a timed cadence or is on its final window. Applies to
    // every windowed HNM board now (Standard + Manual Check In), not just Manual Check In — hence the un-prefixed
    // name.
    public DateTime? NextWindowAt { get; set; }

    // The UTC instant window 1 opened for a timed HNM camp — the anchor the window schedule counts
    // from (window N opens at WindowAnchorAt + (N-1) × WindowMinutes). Always the camp's StartTime:
    // seeded from it at auto-start and re-pinned to it on every manual Prev/Next, so the window grid
    // is fixed and an officer clicking late never shifts it. Null ⇒ fall back to StartTime.
    public DateTime? WindowAnchorAt { get; set; }

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
