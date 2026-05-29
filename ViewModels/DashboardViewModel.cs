using LinkshellManagerDiscordApp.Models;

namespace LinkshellManagerDiscordApp.ViewModels;

public class DashboardViewModel
{
    public string? SelectedLinkshellName { get; set; }
    public List<Linkshell> Linkshells { get; set; } = new();
    public int? SelectedLinkshellId { get; set; }
    public List<AppUserLinkshell> Members { get; set; } = new();
    public List<Event> Events { get; set; } = new();
    public int TotalMembers { get; set; }
    public int UpcomingEvents { get; set; }
    public int CompletedEvents { get; set; }
    public int ItemCount { get; set; }
    public long RevenueTotal { get; set; }
    public int UpcomingAuctionsCount { get; set; }
    public int UpcomingTodsCount { get; set; }
    public bool EnableItems { get; set; } = true;
    public bool EnableRevenue { get; set; } = true;

    public List<TodTrackerEntry> TodTracker { get; set; } = new();
    public List<HnmClaimEntry> HnmClaims { get; set; } = new();
    public int HnmClaimsTotal { get; set; }
    public int HnmClaimsWindowDays { get; set; } = 30;
    public List<RecentActivityEntry> RecentActivity { get; set; } = new();
    public List<NewsUpdateEntry> NewsUpdates { get; set; } = new();
    // ToD repops opening within the next 2 hours, surfaced in the Upcoming
    // Events card alongside scheduled events.
    public List<UpcomingRepopEntry> UpcomingRepops { get; set; } = new();
    public List<RuleSummary> Rules { get; set; } = new();
    public List<AnnouncementSummary> RecentAnnouncements { get; set; } = new();
    public string? CurrentAppUserId { get; set; }
}

public class AnnouncementSummary
{
    public string Title { get; init; } = string.Empty;
    public string? Details { get; init; }
    public string RelativeTime { get; init; } = string.Empty;
    public string? Author { get; init; }
}

public class RuleSummary
{
    public string Title { get; init; } = string.Empty;
    public string? Details { get; init; }
    public string RelativeTime { get; init; } = string.Empty;
    public string? Author { get; init; }
}

public enum TodStatus
{
    Pending,
    Soon,
    Open,
    Expired
}

public class TodTrackerEntry
{
    public string MonsterName { get; init; } = string.Empty;
    public TodStatus Status { get; init; }
    public string StatusLabel => Status switch
    {
        TodStatus.Open => "Open",
        TodStatus.Soon => "Soon",
        TodStatus.Pending => "Pending",
        _ => "Expired"
    };
    public string StatusTagClass => Status switch
    {
        TodStatus.Open => "success",
        TodStatus.Soon => "warning",
        _ => "default"
    };
    public string TimeLabel { get; init; } = string.Empty;
    public string TimeSubLabel { get; init; } = string.Empty;
    public double ProgressPercent { get; init; }
}

public class HnmClaimEntry
{
    public string MonsterName { get; init; } = string.Empty;
    public int Count { get; init; }
    public double Percent { get; init; }
    public string ColorClass { get; init; } = "a";
}

public class RecentActivityEntry
{
    public DateTime When { get; init; }
    public string RelativeTime { get; init; } = string.Empty;
    public string DotClass { get; init; } = string.Empty;
    public string Title { get; init; } = string.Empty;
    public string? Subtitle { get; init; }
    public string? Href { get; init; }
}

public class UpcomingRepopEntry
{
    public string MonsterName { get; init; } = string.Empty;
    public DateTime RepopTime { get; init; }
    // Pretty countdown to the window opening, e.g. "1h 35m".
    public string TimeLabel { get; init; } = string.Empty;
}

public class NewsUpdateEntry
{
    public string Title { get; init; } = string.Empty;
    public string? Subtitle { get; init; }
    public bool IsMine { get; init; }
    public int? Dkp { get; init; }
    public string RelativeTime { get; init; } = string.Empty;
    public string ColorClass { get; init; } = "a";
    // Sort key for the merged multi-source feed (UTC). Not rendered directly.
    public DateTime When { get; init; }
}
