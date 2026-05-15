namespace LinkshellManagerDiscordApp.ViewModels;

public sealed class WindowEventsViewModel
{
    public int LinkshellId { get; set; }
    public string? LinkshellName { get; set; }
    public bool CanManage { get; set; }
    public List<WindowEventRow> OpenEvents { get; set; } = new();
    public List<WindowEventRow> ClosedEvents { get; set; } = new();
    public List<WindowSnapshotRow> UnlinkedSnapshots { get; set; } = new();
}

public sealed class WindowEventRow
{
    public int Id { get; set; }
    public string? Name { get; set; }
    public string Status { get; set; } = string.Empty;
    public DateTime FirstCapturedAtUtc { get; set; }
    public DateTime LastCapturedAtUtc { get; set; }
    public string FirstCapturedDisplay { get; set; } = string.Empty;
    public string LastCapturedDisplay { get; set; } = string.Empty;
    public string? CreatedByCharacterName { get; set; }
    public int SnapshotCount { get; set; }
    public int ActiveSnapshotCount { get; set; }
    public int DuplicateSnapshotCount { get; set; }
    public int IgnoredSnapshotCount { get; set; }
    public int CombinedMemberCount { get; set; }
    public List<WindowSnapshotRow> Snapshots { get; set; } = new();
    public List<WindowCombinedMemberRow> CombinedMembers { get; set; } = new();
}

public sealed class WindowSnapshotRow
{
    public int Id { get; set; }
    public int? WindowEventId { get; set; }
    public string? Name { get; set; }
    public string SnapshotStatus { get; set; } = string.Empty;
    public int? DuplicateOfSnapshotId { get; set; }
    public DateTime CapturedAtUtc { get; set; }
    public string CapturedAtDisplay { get; set; } = string.Empty;
    public string? CapturedByCharacterName { get; set; }
    public string? PrimaryZone { get; set; }
    public int EntryCount { get; set; }
    public List<AttendanceSnapshotEntryRow> Entries { get; set; } = new();
}

public sealed class WindowCombinedMemberRow
{
    public string CharacterName { get; set; } = string.Empty;
    public string? MainJob { get; set; }
    public int? MainJobLevel { get; set; }
    public string? SubJob { get; set; }
    public int? SubJobLevel { get; set; }
    public string? Zone { get; set; }
    public int SnapshotCount { get; set; }
}
