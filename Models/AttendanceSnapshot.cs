using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace LinkshellManagerDiscordApp.Models;

public class AttendanceSnapshot
{
    [Key]
    public int Id { get; set; }

    public int LinkshellId { get; set; }

    [ForeignKey(nameof(LinkshellId))]
    public Linkshell? Linkshell { get; set; }

    public DateTime CapturedAtUtc { get; set; }

    [MaxLength(256)]
    public string? CapturedByCharacterName { get; set; }

    [MaxLength(8)]
    public string? UtcOffset { get; set; }

    public int EntryCount { get; set; }

    public DateTime CreatedAtUtc { get; set; }

    public ICollection<AttendanceSnapshotEntry> Entries { get; set; } = new List<AttendanceSnapshotEntry>();
}
