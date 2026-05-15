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

    // Optional human-friendly label for the snapshot. Set by the addon via
    // `/lsm now <name>` or edited by an officer in the web UI. Null when
    // the capture was anonymous.
    [MaxLength(128)]
    public string? Name { get; set; }

    // Optional association with an Event. Visual metadata only -- linking
    // does NOT credit attendance against the event. ON DELETE SET NULL so a
    // deleted event leaves the snapshot intact but unlinked.
    public int? LinkedEventId { get; set; }

    [ForeignKey(nameof(LinkedEventId))]
    public Event? LinkedEvent { get; set; }

    // Window Events are snapshot-native groups created from /lsm now posts.
    // They are separate from timed Events so multi-alliance HNM/window checks
    // can be merged, deduped, and reviewed without implying timed DKP credit.
    public int? WindowEventId { get; set; }

    [ForeignKey(nameof(WindowEventId))]
    public WindowEvent? WindowEvent { get; set; }

    [MaxLength(32)]
    public string SnapshotStatus { get; set; } = AttendanceSnapshotStatuses.Active;

    public int? DuplicateOfSnapshotId { get; set; }

    [ForeignKey(nameof(DuplicateOfSnapshotId))]
    public AttendanceSnapshot? DuplicateOfSnapshot { get; set; }

    // Idempotency stamp for AttInput appends. Set once after a successful
    // append so retries don't duplicate rows in the sheet.
    public DateTime? AttInputAppendedAt { get; set; }

    public ICollection<AttendanceSnapshotEntry> Entries { get; set; } = new List<AttendanceSnapshotEntry>();
}
