using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace LinkshellManagerDiscordApp.Models;

public class AppUserEventStatusLedger
{
    [Key]
    public int Id { get; set; }

    public int AppUserEventId { get; set; }

    [ForeignKey(nameof(AppUserEventId))]
    public AppUserEvent? AppUserEvent { get; set; }

    public int EventId { get; set; }

    [ForeignKey(nameof(EventId))]
    public Event? Event { get; set; }

    // PHASE4B-DROP: AppUserEventStatusLedgers.AppUserId (+ FK) - no longer written; the row's AppUserEvent already names the member.
    public string? AppUserId { get; set; }

    // PHASE4B-DROP: navigation for AppUserEventStatusLedgers.AppUserId - goes with that column.
    [ForeignKey(nameof(AppUserId))]
    public AppUser? AppUser { get; set; }

    [MaxLength(32)]
    public string ActionType { get; set; } = string.Empty;

    public DateTime OccurredAt { get; set; }

    public bool RequiresVerification { get; set; }

    public DateTime? VerifiedAt { get; set; }

    // PHASE4B-DROP: AppUserEventStatusLedgers.VerifiedBy - no longer written or sent; nothing showed it.
    [MaxLength(256)]
    public string? VerifiedBy { get; set; }

    public DateTime? DeniedAt { get; set; }

    // PHASE4B-DROP: AppUserEventStatusLedgers.DeniedBy - no longer written or sent; nothing showed it.
    [MaxLength(256)]
    public string? DeniedBy { get; set; }

    // PHASE4B-DROP: AppUserEventStatusLedgers.Source - only the removed 'Verify' rows set it. 4B also deletes the old ActionType 'Verify' rows.
    [MaxLength(32)]
    public string? Source { get; set; }

    // PHASE4B-DROP: AppUserEventStatusLedgers.EventAttendanceWindowId (+ FK) - only the removed 'Verify' rows set it.
    public int? EventAttendanceWindowId { get; set; }

    // PHASE4B-DROP: navigation for AppUserEventStatusLedgers.EventAttendanceWindowId - goes with that column.
    [ForeignKey(nameof(EventAttendanceWindowId))]
    public EventAttendanceWindow? EventAttendanceWindow { get; set; }
}
