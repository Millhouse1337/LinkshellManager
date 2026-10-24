namespace LinkshellManagerDiscordApp.ViewModels;

public sealed class LinkshellPendingSubmissionsViewModel
{
    public int LinkshellId { get; set; }
    public string? LinkshellName { get; set; }
    public List<PendingSubmissionRow> Rows { get; set; } = new();
}

public sealed class PendingSubmissionRow
{
    public int Id { get; set; }

    // "Tod" (the only kind left; attendance is no longer submitted for approval)
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
