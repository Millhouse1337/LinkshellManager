using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace LinkshellManagerDiscordApp.Models;

public class AttendanceSnapshotEntry
{
    [Key]
    public int Id { get; set; }

    public int SnapshotId { get; set; }

    [ForeignKey(nameof(SnapshotId))]
    public AttendanceSnapshot? Snapshot { get; set; }

    [MaxLength(256)]
    public string CharacterName { get; set; } = string.Empty;

    [MaxLength(8)]
    public string? MainJob { get; set; }

    public int? MainJobLevel { get; set; }

    [MaxLength(8)]
    public string? SubJob { get; set; }

    public int? SubJobLevel { get; set; }

    [MaxLength(128)]
    public string? Zone { get; set; }
}
