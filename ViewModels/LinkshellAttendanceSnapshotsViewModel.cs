namespace LinkshellManagerDiscordApp.ViewModels;

public sealed class LinkshellAttendanceSnapshotsViewModel
{
    public int LinkshellId { get; set; }
    public string? LinkshellName { get; set; }
    public List<AttendanceSnapshotRow> Snapshots { get; set; } = new();
    public bool CanRename { get; set; }

    // Same officer/leader gate covers linking and quick-creating events.
    public bool CanManageEvents { get; set; }

    // Queued + live (non-ended) events for this LS. Powers the
    // "Link to event" dropdown on each unlinked snapshot card.
    public List<SelectableEventOption> SelectableEvents { get; set; } = new();
}

public sealed class SelectableEventOption
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public bool IsLive { get; set; }
}

public sealed class AttendanceSnapshotRow
{
    public int Id { get; set; }
    public DateTime CapturedAtUtc { get; set; }
    public string? CapturedByCharacterName { get; set; }
    public string? UtcOffset { get; set; }
    public int EntryCount { get; set; }
    public string? PrimaryZone { get; set; }
    public List<AttendanceSnapshotEntryRow> Entries { get; set; } = new();

    // Pre-rendered display strings so the view doesn't have to know how to
    // format ordinal-suffix dates or look up a zone abbreviation.
    public string CapturedAtUtcDisplay { get; set; } = string.Empty;
    public string? CapturedAtLocalDisplay { get; set; }

    // Optional user-supplied label. Set via /lsm now <name> when capturing or
    // via the inline-rename UI on the snapshots page.
    public string? Name { get; set; }

    // Optional event association. Display-only -- linking does not credit
    // attendance. Null when the snapshot stands alone.
    public int? LinkedEventId { get; set; }
    public string? LinkedEventName { get; set; }
    public bool LinkedEventIsLive { get; set; }
}


public sealed class AttendanceSnapshotEntryRow
{
    public string CharacterName { get; set; } = string.Empty;
    public string? MainJob { get; set; }
    public int? MainJobLevel { get; set; }
    public string? SubJob { get; set; }
    public int? SubJobLevel { get; set; }
    public string? Zone { get; set; }
}
