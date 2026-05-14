using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace LinkshellManagerDiscordApp.Models;

public class PendingAttendanceWindowMemberSubmission
{
    [Key]
    public int Id { get; set; }

    public int PendingAttendanceWindowSubmissionId { get; set; }

    [ForeignKey(nameof(PendingAttendanceWindowSubmissionId))]
    public PendingAttendanceWindowSubmission? PendingAttendanceWindowSubmission { get; set; }

    [MaxLength(256)]
    public string CharacterName { get; set; } = string.Empty;

    [MaxLength(16)]
    public string? MainJob { get; set; }

    public int? MainJobLevel { get; set; }

    [MaxLength(16)]
    public string? SubJob { get; set; }

    public int? SubJobLevel { get; set; }
}
