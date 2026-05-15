using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace LinkshellManagerDiscordApp.Models;

public class WindowEvent
{
    [Key]
    public int Id { get; set; }

    public int LinkshellId { get; set; }

    [ForeignKey(nameof(LinkshellId))]
    public Linkshell? Linkshell { get; set; }

    [MaxLength(128)]
    public string? Name { get; set; }

    [MaxLength(128)]
    public string? NormalizedName { get; set; }

    [MaxLength(32)]
    public string Status { get; set; } = WindowEventStatuses.Open;

    public DateTime CreatedAtUtc { get; set; }

    public DateTime FirstCapturedAtUtc { get; set; }

    public DateTime LastCapturedAtUtc { get; set; }

    public DateTime? ClosedAtUtc { get; set; }

    [MaxLength(256)]
    public string? CreatedByCharacterName { get; set; }

    [MaxLength(1024)]
    public string? Notes { get; set; }

    public ICollection<AttendanceSnapshot> Snapshots { get; set; } = new List<AttendanceSnapshot>();
}

public static class WindowEventStatuses
{
    public const string Open = "Open";
    public const string Closed = "Closed";
    public const string Archived = "Archived";
}

public static class AttendanceSnapshotStatuses
{
    public const string Active = "Active";
    public const string PossibleDuplicate = "PossibleDuplicate";
    public const string Duplicate = "Duplicate";
    public const string Ignored = "Ignored";
}
