using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace LinkshellManagerDiscordApp.Models;

public class PendingAttendanceWindowSubmission
{
    [Key]
    public int Id { get; set; }

    public int LinkshellId { get; set; }

    [ForeignKey(nameof(LinkshellId))]
    public Linkshell? Linkshell { get; set; }

    public string? SubmittedByAppUserId { get; set; }

    [ForeignKey(nameof(SubmittedByAppUserId))]
    public AppUser? SubmittedBy { get; set; }

    public DateTime SubmittedAtUtc { get; set; }

    [MaxLength(512)]
    public string? ReviewNotes { get; set; }

    public int EventId { get; set; }

    [ForeignKey(nameof(EventId))]
    public Event? Event { get; set; }

    public int WindowIndex { get; set; }

    public ICollection<PendingAttendanceWindowMemberSubmission> Members { get; set; } = new List<PendingAttendanceWindowMemberSubmission>();
}
