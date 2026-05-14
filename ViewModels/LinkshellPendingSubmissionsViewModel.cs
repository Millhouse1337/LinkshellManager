namespace LinkshellManagerDiscordApp.ViewModels;

public sealed class LinkshellPendingSubmissionsViewModel
{
    public int LinkshellId { get; set; }
    public string? LinkshellName { get; set; }
    public List<PendingSubmissionRow> Rows { get; set; } = new();
    public bool CanReviewTods { get; set; }
    public bool CanReviewAttendance { get; set; }
}

public sealed class PendingSubmissionRow
{
    public int Id { get; set; }

    // "Tod" | "AttendanceWindow" | "AttendanceSnapshot"
    public string Type { get; set; } = string.Empty;

    public string? SubmittedByDisplay { get; set; }

    public DateTime SubmittedAtUtc { get; set; }

    public string Summary { get; set; } = string.Empty;
}

public sealed class EditPendingTodViewModel
{
    public int LinkshellId { get; set; }
    public string? LinkshellName { get; set; }
    public int SubmissionId { get; set; }
    public string? SubmittedByDisplay { get; set; }
    public DateTime SubmittedAtUtc { get; set; }

    public string? MonsterName { get; set; }
    public int? DayNumber { get; set; }
    public bool? Claim { get; set; }
    public DateTime? Time { get; set; }
    public string? Cooldown { get; set; }
    public string? Interval { get; set; }
    public DateTime? RepopTime { get; set; }
    public string? ImagePath { get; set; }

    public List<EditPendingTodLootRow> LootRows { get; set; } = new();

    public IReadOnlyList<string> MonsterOptions { get; set; } = Array.Empty<string>();
    public IReadOnlyList<string> CooldownOptions { get; set; } = Array.Empty<string>();
    public IReadOnlyList<string> IntervalOptions { get; set; } = Array.Empty<string>();
    public IReadOnlyList<string> LinkshellMembers { get; set; } = Array.Empty<string>();
}

public sealed class EditPendingTodLootRow
{
    public string? ItemName { get; set; }
    public string? ItemWinner { get; set; }
    public int? WinningDkpSpent { get; set; }
}

public sealed class EditPendingAttendanceWindowViewModel
{
    public int LinkshellId { get; set; }
    public string? LinkshellName { get; set; }
    public int SubmissionId { get; set; }
    public string? SubmittedByDisplay { get; set; }
    public DateTime SubmittedAtUtc { get; set; }

    public int EventId { get; set; }
    public string? EventName { get; set; }
    public int WindowIndex { get; set; }

    public List<EditPendingAttendanceMember> Members { get; set; } = new();
}

public sealed class EditPendingAttendanceMember
{
    public int Id { get; set; }
    public string CharacterName { get; set; } = string.Empty;
    public string? MainJob { get; set; }
    public int? MainJobLevel { get; set; }
    public string? SubJob { get; set; }
    public int? SubJobLevel { get; set; }
    public bool Include { get; set; } = true;
}

public sealed class EditPendingAttendanceSnapshotViewModel
{
    public int LinkshellId { get; set; }
    public string? LinkshellName { get; set; }
    public int SubmissionId { get; set; }
    public string? SubmittedByDisplay { get; set; }
    public DateTime SubmittedAtUtc { get; set; }

    public DateTime CapturedAtUtc { get; set; }
    public string? CapturedByCharacterName { get; set; }
    public string? UtcOffset { get; set; }

    public List<EditPendingSnapshotEntry> Entries { get; set; } = new();
}

public sealed class EditPendingSnapshotEntry
{
    public int Id { get; set; }
    public string CharacterName { get; set; } = string.Empty;
    public string? MainJob { get; set; }
    public int? MainJobLevel { get; set; }
    public string? SubJob { get; set; }
    public int? SubJobLevel { get; set; }
    public string? Zone { get; set; }
    public bool Include { get; set; } = true;
}
