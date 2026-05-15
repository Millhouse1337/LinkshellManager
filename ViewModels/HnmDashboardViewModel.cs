using LinkshellManagerDiscordApp.Models;

namespace LinkshellManagerDiscordApp.ViewModels;

// One row of the streamlined HNM dashboard. Aggregates the latest ToD,
// its auto-created next-repop Event, and any AttendanceSnapshots linked to
// that Event, so the page renders a single self-contained card per monster
// without needing per-card fetches from the view.
public sealed class HnmDashboardRowViewModel
{
    public string MonsterName { get; init; } = string.Empty;
    public string? Zone { get; init; }
    public int WindowCount { get; init; }
    public bool IsDayTracked { get; init; }
    public int? DayCycle { get; init; }

    public HnmTodSummary? LatestTod { get; init; }
    public HnmLinkedEventSummary? LinkedEvent { get; init; }
    public IReadOnlyList<HnmTodSummary> RecentTods { get; init; } = Array.Empty<HnmTodSummary>();
    public IReadOnlyList<HnmLinkedSnapshotSummary> LinkedSnapshots { get; init; } = Array.Empty<HnmLinkedSnapshotSummary>();
}

public sealed class HnmTodSummary
{
    public int Id { get; init; }
    public DateTime? TimeUtc { get; init; }
    public string TimeDisplayUtc { get; init; } = "-";
    public string TimeDisplayLocal { get; init; } = "-";
    public DateTime? RepopTimeUtc { get; init; }
    public string RepopDisplayLocal { get; init; } = "-";
    public string? Cooldown { get; init; }
    public bool? Claim { get; init; }
    public int? DayNumber { get; init; }
}

public sealed class HnmLinkedEventSummary
{
    public int Id { get; init; }
    public string EventName { get; init; } = string.Empty;
    public string? EventLocation { get; init; }
    public DateTime? StartTimeUtc { get; init; }
    public string StartTimeDisplayLocal { get; init; } = "-";
    public bool IsLive { get; init; }
    public bool IsEnded { get; init; }
    public int? DkpPerHour { get; init; }
    public int? WindowCount { get; init; }
    public string? AttInputEntryType { get; init; }
}

public sealed class HnmLinkedSnapshotSummary
{
    public int Id { get; init; }
    public string? Name { get; init; }
    public DateTime CapturedAtUtc { get; init; }
    public string CapturedAtLocalDisplay { get; init; } = "-";
    public string? CapturedByCharacterName { get; init; }
    public int EntryCount { get; init; }
}

public sealed class HnmDashboardViewModel
{
    public int LinkshellId { get; init; }
    public string? LinkshellName { get; init; }
    public bool HnmSectionEnabled { get; init; }
    public bool CanManageEvents { get; init; }
    public IReadOnlyList<HnmDashboardRowViewModel> Rows { get; init; } = Array.Empty<HnmDashboardRowViewModel>();
    public IReadOnlyList<Linkshell> Linkshells { get; init; } = Array.Empty<Linkshell>();
}
