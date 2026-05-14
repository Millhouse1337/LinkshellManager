using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace LinkshellManagerDiscordApp.Models;

public class PendingAttendanceSnapshotSubmission
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

    public DateTime CapturedAtUtc { get; set; }

    [MaxLength(256)]
    public string? CapturedByCharacterName { get; set; }

    [MaxLength(8)]
    public string? UtcOffset { get; set; }

    public int EntryCount { get; set; }

    public ICollection<PendingAttendanceSnapshotEntry> Entries { get; set; } = new List<PendingAttendanceSnapshotEntry>();
}
