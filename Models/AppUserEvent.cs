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
