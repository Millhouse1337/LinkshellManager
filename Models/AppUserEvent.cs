using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace LinkshellManagerDiscordApp.Models;

public class AppUserEvent
{
    [Key]
    public int Id { get; set; }

    public string? AppUserId { get; set; }

    [ForeignKey(nameof(AppUserId))]
    public AppUser? AppUser { get; set; }

    // Set INSTEAD of AppUserId for "Outside Party Signup" (a Discord member with no
    // linked LSM account, e.g. a "Sign Up (No Slot)" attendance row). Identity for
    // withdraw / switch / one-per-event is keyed by this Discord snowflake. Such rows
    // earn no DKP and write no history — they're cleared when the event ends.
    [MaxLength(32)]
    public string? DiscordUserId { get; set; }

    public int EventId { get; set; }

    [ForeignKey(nameof(EventId))]
    public Event? Event { get; set; }

    public string? CharacterName { get; set; }

    public string? JobName { get; set; }

    public string? SubJobName { get; set; }

    public string? JobType { get; set; }

    public DateTime? StartTime { get; set; }

    public DateTime? EndTime { get; set; }

    public double? Duration { get; set; }

    public double? EventDkp { get; set; }

    public bool IsQuickJoin { get; set; }

    // Self-declared readiness tags for the DISCORD board, set by the member after they're
    // on it ("🎖️ My Readiness"). The no-slot ("Also Attending") half of the same three
    // flags EventPartySlotSignup carries — a member who holds a slot AND has an attendance
    // row gets both written, so the tags survive an officer benching them. Declarations
    // only: nothing validates them and they grant nothing. See EventReadiness.
    public bool EnfeebReady { get; set; }

    public bool ResistReady { get; set; }

    public bool RelicWeapon { get; set; }

    // Manual Check In attendance: the window this member first CHECKED IN for. Credit at finalize
    // = windows from arrival through their departure (or the camp's pop/last window), inclusive.
    // Re-tapping "Check In" at a later window overwrites this (the "late arrival" correction).
    // Null = hasn't checked in / not a Manual Check In event.
    public int? WdArrivalWindow { get; set; }

    // Manual Check In: the window this member CHECKED OUT in (left mid-camp). Credit runs
    // arrival..departure inclusive. Null = still present (credited through the pop/last window).
    // Cleared when they Check In again (they came back).
    public int? WdDepartureWindow { get; set; }

    public bool? IsVerified { get; set; }

    public string? Proctor { get; set; }

    public bool? IsOnBreak { get; set; }

    // Set when the member used "Withdraw From Event" (vs a normal Take Break): they're
    // parked in the Break Room — timer paused, DKP/attendance/history preserved — but they
    // signalled they don't intend to come back. Drives a "not returning" label in the Break
    // Room. Cleared if they Return from break (they came back after all). See
    // ActivityDataController UnsignAsync / ReturnFromBreakAsync / ForceResumeAsync and the
    // web EventController.Participation TakeBreak/ReturnFromBreak.
    public bool WithdrewFromEvent { get; set; }

    public DateTime? PauseTime { get; set; }

    public DateTime? ResumeTime { get; set; }

    public ICollection<AppUserEventStatusLedger> StatusLedgerEntries { get; set; } = new List<AppUserEventStatusLedger>();

    public ICollection<AppUserEventWindow> AttendedWindows { get; set; } = new List<AppUserEventWindow>();
}
