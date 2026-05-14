namespace LinkshellManagerDiscordApp.ViewModels;

public sealed class LinkshellAttendanceSnapshotsViewModel
{
    public int LinkshellId { get; set; }
    public string? LinkshellName { get; set; }
    public List<AttendanceSnapshotRow> Snapshots { get; set; } = new();
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
