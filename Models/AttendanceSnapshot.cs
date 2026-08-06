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

    // Which spawn window this capture was taken in, measured from its Window Event's grid anchor
    // (WindowEvent.WindowGridAnchorUtc) at the monster's own cadence — 10-minute steps on the
    // 7-window kings/dragons, hourly on the 25-window wyrms. Stamped once at capture, so a snapshot
    // keeps the window it was actually taken in rather than being re-derived later against a grid
    // that may have moved.
    //
    // NULL means "this camp has no window grid" — Sky gods, farm NMs and ad-hoc `/lsm now` posts
    // run no cadence, so there is no window to name. Deliberately distinct from window 1.
    public int? WindowNumber { get; set; }

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
