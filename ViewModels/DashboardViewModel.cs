using LinkshellManagerDiscordApp.Models;
using LinkshellManagerDiscordApp.Utils;

namespace LinkshellManagerDiscordApp.ViewModels;

public class DashboardViewModel
{
    public string? SelectedLinkshellName { get; set; }
    public List<Linkshell> Linkshells { get; set; } = new();
    public int? SelectedLinkshellId { get; set; }
    // Cache-busted banner image URL for the selected linkshell, or null when none.
    public string? BannerUrl { get; set; }
    public List<AppUserLinkshell> Members { get; set; } = new();
    // AppUserIds that have actually opened/synced the app (a DiscordActivityUser row
    // points at them) - drives the roster's "App" tag, the same one the Discord
    // Activity roster shows. An AppUserId alone only means an account exists.
    public HashSet<string> SyncedAppUserIds { get; set; } = new(StringComparer.Ordinal);
    // Leveled jobs per roster row (keyed by AppUserLinkshell id) behind the roster's
    // "Show Jobs" toggle. Missing for members who never linked an app account.
    public Dictionary<int, JobsRosterEntry> MemberJobs { get; set; } = new();
    // Catalog job names in display order; every entry's arrays align to it.
    public IReadOnlyList<string> JobCatalog { get; } = EventJobCatalog.MainJobOptions;
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
    public bool EnableToDs { get; set; } = true;
    public bool EnableHnmSection { get; set; } = true;

    public List<TodTrackerEntry> TodTracker { get; set; } = new();
    public List<HnmClaimEntry> HnmClaims { get; set; } = new();
    public int HnmClaimsTotal { get; set; }
    public int HnmClaimsWindowDays { get; set; } = 30;

    // The second tab of the same card: which window of its band each HNM actually pops on.
    // All-time on purpose — a spawn distribution needs volume, and a 30-day slice of a monster
    // that pops twice a week says nothing.
    public List<HnmWindowRow> HnmWindows { get; set; } = new();
    public int HnmWindowsTotal { get; set; }
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
    public string? Category { get; init; }
    public string RelativeTime { get; init; } = string.Empty;
    public string? Author { get; init; }
}

public class RuleSummary
{
    public string Title { get; init; } = string.Empty;
    public string? Details { get; init; }
    public string? Category { get; init; }
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

    // The FAMILY's colour: an NQ and its HQ share one, and are told apart by IsHq.
    public string ColorClass { get; init; } = "a";

    // Was this the stronger half? Drives the lighter ring shade and the HQ badge. HasHqVariant is
    // false for the monsters that have no stronger half — they get no badge at all, rather than a
    // meaningless "NQ" on a Tiamat.
    public bool IsHq { get; init; }
    public bool HasHqVariant { get; init; }
}

// One monster's row on the card's "Window frequency" tab: which window of its spawn band it
// actually pops on. Straight off HnmWindowStatsService — see the notes there on why this counts
// unclaimed pops too, and why the NQ/HQ halves share one row.
public class HnmWindowRow
{
    public string MonsterName { get; init; } = string.Empty;
    public string ColorClass { get; init; } = "a";
    public int TotalPops { get; init; }
    public int WindowCount { get; init; }
    public int PeakWindow { get; init; }
    public double PeakPercent { get; init; }
    public IReadOnlyList<HnmWindowBarEntry> Bars { get; init; } = Array.Empty<HnmWindowBarEntry>();
}

public class HnmWindowBarEntry
{
    public int Window { get; init; }
    public int Count { get; init; }
    public double Percent { get; init; }

    // Height as a share of this monster's BUSIEST window, not of 100% — a monster whose best
    // window holds 30% of its pops would otherwise draw as a row of stubs.
    public double HeightPercent { get; init; }
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
