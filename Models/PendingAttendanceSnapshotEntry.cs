using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace LinkshellManagerDiscordApp.Models;

public class PendingAttendanceSnapshotEntry
{
    [Key]
    public int Id { get; set; }

    public int PendingAttendanceSnapshotSubmissionId { get; set; }

    [ForeignKey(nameof(PendingAttendanceSnapshotSubmissionId))]
    public PendingAttendanceSnapshotSubmission? PendingAttendanceSnapshotSubmission { get; set; }

    [MaxLength(256)]
    public string CharacterName { get; set; } = string.Empty;

    [MaxLength(16)]
    public string? MainJob { get; set; }

    public int? MainJobLevel { get; set; }

    [MaxLength(16)]
    public string? SubJob { get; set; }

    public int? SubJobLevel { get; set; }

    [MaxLength(128)]
    public string? Zone { get; set; }
}
