using LinkshellManagerDiscordApp.Data;
using LinkshellManagerDiscordApp.Models;
using LinkshellManagerDiscordApp.Services;
using LinkshellManagerDiscordApp.ViewModels;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace LinkshellManagerDiscordApp.Controllers;

[Authorize]
public class DashboardController : Controller
{
    private readonly ApplicationDbContext _context;
    private readonly UserManager<AppUser> _userManager;
    private readonly TreasuryBalanceService _treasury;
    private readonly JobsRosterService _jobsRoster;
    private readonly HnmClaimStatsService _hnmClaimStats;

    private const int HnmClaimsWindowDays = 30;

    // (HnmPaletteClasses / HnmNames / BuildHnmClaims lived here: a hand-kept copy of the true-HNM
    // name list and the donut's aggregation. The copy had gone stale against HnmConfig — it never
    // learned the timed NMs on the kings' band, and a plain set lookup could not match the
    // combined "Behemoth/King Behemoth" label an HNM board stores, so those claims were dropped
    // outright. HnmClaimStatsService is the one aggregation now, shared with the Activity.)
    private static readonly TimeSpan SoonThreshold = TimeSpan.FromHours(3);
    private static readonly TimeSpan DefaultSpawnWindow = TimeSpan.FromHours(3);

    public DashboardController(
        ApplicationDbContext context,
        UserManager<AppUser> userManager,
        TreasuryBalanceService treasury,
        JobsRosterService jobsRoster,
        HnmClaimStatsService hnmClaimStats)
    {
        _context = context;
        _userManager = userManager;
        _treasury = treasury;
        _jobsRoster = jobsRoster;
        _hnmClaimStats = hnmClaimStats;
    }

    public async Task<IActionResult> Index(int? linkshellId = null)
    {
        var user = await _userManager.GetUserAsync(User);
        if (user is null)
        {
            return Challenge();
        }

        var linkshells = await _context.AppUserLinkshells
            .Where(link => link.AppUserId == user.Id)
            .Select(link => link.Linkshell!)
            .OrderBy(linkshell => linkshell.LinkshellName)
            .ToListAsync();

        var selectedLinkshellId = linkshellId;
        if (selectedLinkshellId.HasValue && linkshells.All(linkshell => linkshell.Id != selectedLinkshellId.Value))
        {
            selectedLinkshellId = null;
        }

        selectedLinkshellId ??= user.PrimaryLinkshellId;
        if (selectedLinkshellId.HasValue && linkshells.All(linkshell => linkshell.Id != selectedLinkshellId.Value))
        {
            selectedLinkshellId = null;
        }

        selectedLinkshellId ??= linkshells.FirstOrDefault()?.Id;

        var members = selectedLinkshellId.HasValue
            ? await _context.AppUserLinkshells
                .Include(link => link.AppUser)
                .Where(link => link.LinkshellId == selectedLinkshellId.Value)
                .OrderBy(link => link.CharacterName)
                .ToListAsync()
            : new List<AppUserLinkshell>();

        // Roster parity with the Discord Activity dashboard: which members have
        // actually opened the app (the "App" tag — an AppUserId alone only means an
        // account exists), plus each row's leveled jobs for the "Show Jobs" toggle.
        var syncedAppUserIds = await _jobsRoster.GetSyncedAppUserIdsAsync(members, HttpContext.RequestAborted);
        var memberJobs = selectedLinkshellId.HasValue
            ? (await _jobsRoster.BuildForMembersAsync(selectedLinkshellId.Value, members, HttpContext.RequestAborted))
                .ToDictionary(entry => entry.MemberId)
            : new Dictionary<int, JobsRosterEntry>();

        var events = selectedLinkshellId.HasValue
            ? await _context.Events
                .Include(evt => evt.EventLootDetails)
                .Where(evt => evt.LinkshellId == selectedLinkshellId.Value)
                .OrderBy(evt => evt.StartTime)
                .Take(10)
                .ToListAsync()
            : new List<Event>();

        var eventHistories = selectedLinkshellId.HasValue
            ? await _context.EventHistories
                .Where(history => history.LinkshellId == selectedLinkshellId.Value)
                .OrderByDescending(history => history.EndTime ?? history.TimeStamp)
                .Take(20)
                .ToListAsync()
            : new List<EventHistory>();

        var tods = selectedLinkshellId.HasValue
            ? await _context.Tods
                .AsNoTracking()
                .Include(tod => tod.TodLootDetails)
                .Where(tod => tod.LinkshellId == selectedLinkshellId.Value)
                .OrderByDescending(tod => tod.Time ?? tod.TimeStamp)
                .ThenByDescending(tod => tod.Id)
                .Take(200)
                .ToListAsync()
            : new List<Tod>();

        var selectedLinkshell = linkshells.FirstOrDefault(linkshell => linkshell.Id == selectedLinkshellId);
        var selectedLinkshellName = selectedLinkshell?.LinkshellName;

        // Banner: a cheap version-only lookup (no image bytes) → cache-busted URL.
        var bannerUpdatedAt = selectedLinkshellId.HasValue
            ? await _context.LinkshellBanners
                .Where(b => b.LinkshellId == selectedLinkshellId.Value)
                .Select(b => (DateTime?)b.UpdatedAt)
                .FirstOrDefaultAsync()
            : null;
        var bannerUrl = bannerUpdatedAt.HasValue
            ? $"/api/activity/linkshells/{selectedLinkshellId!.Value}/banner?v={bannerUpdatedAt.Value.Ticks}"
            : null;

        var itemCount = selectedLinkshellId.HasValue
            ? await _context.Items.CountAsync(item => item.LinkshellId == selectedLinkshellId.Value && !item.IsSold)
            : 0;

        var revenueTotal = selectedLinkshellId.HasValue
            ? await _treasury.GetCashOnHandAsync(selectedLinkshellId.Value, HttpContext.RequestAborted)
            : 0L;

        var nowUtc = DateTime.UtcNow;
        var upcomingAuctionsCount = selectedLinkshellId.HasValue
            ? await _context.Auctions
                .Where(auction => auction.LinkshellId == selectedLinkshellId.Value
                                   && (auction.EndTime == null || auction.EndTime > nowUtc))
                .CountAsync()
            : 0;

        var upcomingTodsCount = tods.Count(tod => tod.RepopTime.HasValue
            && DateTime.SpecifyKind(tod.RepopTime.Value, DateTimeKind.Utc) > nowUtc);

        var rules = selectedLinkshellId.HasValue
            ? await _context.Rules
                .AsNoTracking()
                .Where(rule => rule.LinkshellId == selectedLinkshellId.Value)
                .OrderByDescending(rule => rule.CreatedAt)
                .Take(5)
                .ToListAsync()
            : new List<Rule>();

        var announcements = selectedLinkshellId.HasValue
            ? await _context.Announcements
                .AsNoTracking()
                .Where(announcement => announcement.LinkshellId == selectedLinkshellId.Value)
                .OrderByDescending(announcement => announcement.CreatedAt)
                .Take(5)
                .ToListAsync()
            : new List<Announcement>();

        var auctions = selectedLinkshellId.HasValue
            ? await _context.Auctions
                .AsNoTracking()
                .Where(auction => auction.LinkshellId == selectedLinkshellId.Value)
                .OrderByDescending(auction => auction.EndTime ?? auction.StartedAt ?? auction.StartTime)
                .Take(10)
                .ToListAsync()
            : new List<Auction>();

        var dkpAudits = selectedLinkshellId.HasValue
            ? await _context.DkpLedgerEntries
                .AsNoTracking()
                .Where(entry => entry.LinkshellId == selectedLinkshellId.Value
                                && (entry.EntryType == "AuditMisc" || entry.EntryType == "AuditAdjustment"))
                .OrderByDescending(entry => entry.OccurredAt)
                .Take(10)
                .ToListAsync()
            : new List<DkpLedgerEntry>();

        var todTracker = BuildTodTracker(tods);
        var upcomingRepops = BuildUpcomingRepops(tods);
        // Off its own query, not the 200-row `tods` page above — that page is the most recent ToDs
        // of any monster, so a busy month of Sky pops used to push the HNM claims out of the chart.
        var hnmStats = await _hnmClaimStats.BuildAsync(selectedLinkshellId);
        var hnmClaims = hnmStats.Last30Days
            .Select(slice => new HnmClaimEntry
            {
                MonsterName = slice.MonsterName,
                Count = slice.Count,
                Percent = slice.Percent,
                ColorClass = slice.ColorClass
            })
            .ToList();
        var hnmTotal = hnmStats.Last30Days.Sum(slice => slice.Count);
        var recentActivity = BuildRecentActivity(events, eventHistories, tods);
        var newsUpdates = BuildNewsUpdates(announcements, rules, auctions, dkpAudits, members);

        return View(new DashboardViewModel
        {
            Linkshells = linkshells,
            SelectedLinkshellId = selectedLinkshellId,
            SelectedLinkshellName = selectedLinkshellName,
            BannerUrl = bannerUrl,
            Members = members,
            SyncedAppUserIds = syncedAppUserIds,
            MemberJobs = memberJobs,
            Events = events,
            TotalMembers = members.Count,
            UpcomingEvents = events.Count(evt => evt.CommencementStartTime is null),
            CompletedEvents = eventHistories.Count,
            ItemCount = itemCount,
            RevenueTotal = revenueTotal,
            UpcomingAuctionsCount = upcomingAuctionsCount,
            UpcomingTodsCount = upcomingTodsCount,
            EnableItems = selectedLinkshell?.EnableItems ?? true,
            EnableRevenue = selectedLinkshell?.EnableRevenue ?? true,
            EnableToDs = selectedLinkshell?.EnableToDs ?? true,
            EnableHnmSection = selectedLinkshell?.EnableHnmSection ?? true,
            TodTracker = todTracker,
            HnmClaims = hnmClaims,
            HnmClaimsTotal = hnmTotal,
            HnmClaimsWindowDays = HnmClaimsWindowDays,
            RecentActivity = recentActivity,
            NewsUpdates = newsUpdates,
            UpcomingRepops = upcomingRepops,
            Rules = rules.Select(rule => new RuleSummary
            {
                Title = rule.RuleTitle,
                Details = rule.RuleDetails,
                Category = rule.Category,
                RelativeTime = FormatRelative(rule.CreatedAt),
                Author = rule.CreatedByCharacterName
            }).ToList(),
            RecentAnnouncements = announcements.Select(announcement => new AnnouncementSummary
            {
                Title = announcement.AnnouncementTitle,
                Details = announcement.AnnouncementDetails,
                Category = announcement.Category,
                RelativeTime = FormatRelative(announcement.CreatedAt),
                Author = announcement.CreatedByCharacterName
            }).ToList(),
            CurrentAppUserId = user.Id
        });
    }

    private static List<TodTrackerEntry> BuildTodTracker(IReadOnlyCollection<Tod> tods)
    {
        var now = DateTime.UtcNow;
        var latestPerMonster = tods
            .Where(tod => !string.IsNullOrWhiteSpace(tod.MonsterName))
            .GroupBy(tod => tod.MonsterName!.Trim(), StringComparer.OrdinalIgnoreCase)
            .Select(group => group.OrderByDescending(tod => tod.Time ?? tod.TimeStamp).First())
            .ToList();

        var entries = new List<TodTrackerEntry>();
        foreach (var tod in latestPerMonster)
        {
            if (!tod.RepopTime.HasValue) continue;
            var repopUtc = DateTime.SpecifyKind(tod.RepopTime.Value, DateTimeKind.Utc);
            var closeUtc = repopUtc + DefaultSpawnWindow;

            TodStatus status;
            string timeLabel;
            string timeSubLabel;
            double progress;

            if (now < repopUtc)
            {
                var delta = repopUtc - now;
                status = delta <= SoonThreshold ? TodStatus.Soon : TodStatus.Pending;
                timeLabel = FormatShortDuration(delta);
                timeSubLabel = "until open";
                var todUtc = tod.Time.HasValue ? DateTime.SpecifyKind(tod.Time.Value, DateTimeKind.Utc) : repopUtc - TimeSpan.FromHours(22);
                var totalCycle = (repopUtc - todUtc).TotalSeconds;
                var elapsed = (now - todUtc).TotalSeconds;
                progress = totalCycle > 0 ? Math.Clamp(elapsed / totalCycle, 0, 1) : 0;
            }
            else if (now < closeUtc)
            {
                var delta = closeUtc - now;
                status = TodStatus.Open;
                timeLabel = FormatShortDuration(delta);
                timeSubLabel = "until close";
                var totalWindow = DefaultSpawnWindow.TotalSeconds;
                var elapsed = (now - repopUtc).TotalSeconds;
                progress = totalWindow > 0 ? Math.Clamp(elapsed / totalWindow, 0, 1) : 0;
            }
            else
            {
                continue;
            }

            entries.Add(new TodTrackerEntry
            {
                MonsterName = tod.MonsterName!.Trim(),
                Status = status,
                TimeLabel = timeLabel,
                TimeSubLabel = timeSubLabel,
                ProgressPercent = Math.Round(progress * 100, 1)
            });
        }

        return entries
            .OrderBy(entry => entry.Status == TodStatus.Open ? 0 : entry.Status == TodStatus.Soon ? 1 : 2)
            .ThenBy(entry => entry.MonsterName)
            .Take(8)
            .ToList();
    }

    // ToD repops opening within the next 2 hours (future windows only — already
    // open windows live in the ToD Tracker card). Latest ToD per monster, soonest
    // first. Surfaced in the Upcoming Events card.
    private static List<UpcomingRepopEntry> BuildUpcomingRepops(IReadOnlyCollection<Tod> tods)
    {
        var now = DateTime.UtcNow;
        var window = now + TimeSpan.FromHours(2);

        var latestPerMonster = tods
            .Where(tod => !string.IsNullOrWhiteSpace(tod.MonsterName))
            .GroupBy(tod => tod.MonsterName!.Trim(), StringComparer.OrdinalIgnoreCase)
            .Select(group => group.OrderByDescending(tod => tod.Time ?? tod.TimeStamp).First())
            .ToList();

        var entries = new List<UpcomingRepopEntry>();
        foreach (var tod in latestPerMonster)
        {
            if (!tod.RepopTime.HasValue) continue;
            var repopUtc = DateTime.SpecifyKind(tod.RepopTime.Value, DateTimeKind.Utc);
            if (repopUtc <= now || repopUtc > window) continue;
            entries.Add(new UpcomingRepopEntry
            {
                MonsterName = tod.MonsterName!.Trim(),
                RepopTime = repopUtc,
                TimeLabel = FormatShortDuration(repopUtc - now)
            });
        }

        return entries.OrderBy(entry => entry.RepopTime).ToList();
    }

    private static List<RecentActivityEntry> BuildRecentActivity(IReadOnlyCollection<Event> activeEvents, IReadOnlyCollection<EventHistory> eventHistories, IReadOnlyCollection<Tod> tods)
    {
        var items = new List<RecentActivityEntry>();

        // Loot entered during a LIVE (still-running) event. Without this, loot
        // logged in the live event system didn't appear in Recent Activity until
        // the event was archived into an EventHistory.
        foreach (var ev in activeEvents)
        {
            if (ev.EventLootDetails.Count == 0) { continue; }
            var lootWhen = ev.CommencementStartTime ?? ev.StartTime ?? DateTime.UtcNow;
            foreach (var loot in ev.EventLootDetails.OrderByDescending(l => l.Id))
            {
                if (string.IsNullOrWhiteSpace(loot.ItemName)) { continue; }
                var winner = (loot.ItemWinner ?? string.Empty).Trim();
                items.Add(new RecentActivityEntry
                {
                    When = lootWhen,
                    RelativeTime = FormatRelative(lootWhen),
                    DotClass = "claim",
                    Title = loot.ItemName!,
                    Subtitle = winner.Length > 0 ? $"{ev.EventName ?? "Event"} · {winner}" : $"{ev.EventName ?? "Event"} drop"
                });
            }
        }

        foreach (var history in eventHistories)
        {
            var when = history.EndTime ?? history.TimeStamp ?? DateTime.UtcNow;
            items.Add(new RecentActivityEntry
            {
                When = when,
                RelativeTime = FormatRelative(when),
                DotClass = "kill",
                Title = $"{history.EventName ?? "Event"} completed",
                Subtitle = history.EventLocation
            });
        }

        foreach (var tod in tods.Take(40))
        {
            var when = tod.Time ?? tod.TimeStamp ?? DateTime.UtcNow;

            // A claimed kill with loot expands to a row per item ("Ridill ·
            // Millhouse"); the loot row stands in for the claim row.
            if (tod.Claim == true && tod.TodLootDetails.Count > 0)
            {
                foreach (var loot in tod.TodLootDetails.OrderByDescending(l => l.Id))
                {
                    if (string.IsNullOrWhiteSpace(loot.ItemName)) continue;
                    var winner = (loot.ItemWinner ?? string.Empty).Trim();
                    items.Add(new RecentActivityEntry
                    {
                        When = when,
                        RelativeTime = FormatRelative(when),
                        DotClass = "claim",
                        Title = loot.ItemName!,
                        Subtitle = winner.Length > 0 ? $"{tod.MonsterName} · {winner}" : $"{tod.MonsterName} drop"
                    });
                }
                continue;
            }

            string title;
            string dotClass;
            if (tod.Claim == true)
            {
                title = $"{tod.MonsterName} claimed";
                dotClass = "claim";
            }
            else if (tod.Claim == false)
            {
                title = $"{tod.MonsterName} defeated — No Claim";
                dotClass = "kill";
            }
            else
            {
                title = $"{tod.MonsterName} defeated — Not Specified";
                dotClass = "kill";
            }
            items.Add(new RecentActivityEntry
            {
                When = when,
                RelativeTime = FormatRelative(when),
                DotClass = dotClass,
                Title = title,
                Subtitle = null
            });
        }

        return items
            .OrderByDescending(item => item.When)
            .Take(12)
            .ToList();
    }

    // "News & Updates" feed — the "newsy" side of the dashboard: new
    // announcements + rules, auction open/close, DKP adjustments, and new
    // members (newest first). Operational stuff (kills/claims/loot/events)
    // lives in Recent Activity instead.
    private static List<NewsUpdateEntry> BuildNewsUpdates(
        IReadOnlyCollection<Announcement> announcements,
        IReadOnlyCollection<Rule> rules,
        IReadOnlyCollection<Auction> auctions,
        IReadOnlyCollection<DkpLedgerEntry> dkpAudits,
        IReadOnlyCollection<AppUserLinkshell> members)
    {
        var items = new List<NewsUpdateEntry>();
        var now = DateTime.UtcNow;

        foreach (var announcement in announcements)
        {
            items.Add(new NewsUpdateEntry
            {
                When = announcement.CreatedAt,
                Title = announcement.AnnouncementTitle,
                Subtitle = string.IsNullOrWhiteSpace(announcement.CreatedByCharacterName)
                    ? "Announcement"
                    : $"Announcement · {announcement.CreatedByCharacterName}",
                RelativeTime = FormatRelative(announcement.CreatedAt),
                ColorClass = "c"
            });
        }

        foreach (var rule in rules)
        {
            items.Add(new NewsUpdateEntry
            {
                When = rule.CreatedAt,
                Title = rule.RuleTitle,
                Subtitle = "Rule updated",
                RelativeTime = FormatRelative(rule.CreatedAt),
                ColorClass = "d"
            });
        }

        foreach (var auction in auctions)
        {
            var title = string.IsNullOrWhiteSpace(auction.AuctionTitle) ? "Auction" : auction.AuctionTitle!;
            if (auction.EndTime is { } endTime && endTime <= now)
            {
                items.Add(new NewsUpdateEntry
                {
                    When = endTime,
                    Title = $"{title} closed",
                    Subtitle = "Auction",
                    RelativeTime = FormatRelative(endTime),
                    ColorClass = "e"
                });
            }
            else if ((auction.StartedAt ?? auction.StartTime) is { } openedAt)
            {
                items.Add(new NewsUpdateEntry
                {
                    When = openedAt,
                    Title = $"{title} opened",
                    Subtitle = "Auction",
                    RelativeTime = FormatRelative(openedAt),
                    ColorClass = "e"
                });
            }
        }

        foreach (var entry in dkpAudits)
        {
            var sign = entry.Amount >= 0 ? "+" : "";
            items.Add(new NewsUpdateEntry
            {
                When = entry.OccurredAt,
                Title = $"{entry.CharacterName ?? "Member"} DKP {sign}{entry.Amount:0.##}",
                Subtitle = entry.EntryType == "AuditAdjustment" ? "DKP correction" : "DKP adjustment",
                RelativeTime = FormatRelative(entry.OccurredAt),
                ColorClass = "f"
            });
        }

        foreach (var member in members.Where(m => m.DateJoined.HasValue))
        {
            items.Add(new NewsUpdateEntry
            {
                When = member.DateJoined!.Value,
                Title = $"{member.CharacterName ?? member.AppUser?.UserName ?? "Member"} joined",
                Subtitle = "New member",
                RelativeTime = FormatRelative(member.DateJoined.Value),
                ColorClass = "a"
            });
        }

        return items
            .OrderByDescending(item => item.When)
            .Take(12)
            .ToList();
    }

    // News & updates icon tints. Deliberately its own short list rather than the donut's — the
    // `.news-icon.donut-seg-*` rules only paint a–f, so this must not reach into the wider
    // palette the donut grew.
    private static readonly string[] NewsIconPaletteClasses = { "a", "b", "c", "d", "e", "f" };

    private static string PickColorFromName(string name)
    {
        if (string.IsNullOrEmpty(name)) return "a";
        var hash = 0;
        foreach (var ch in name) { hash = (hash * 31 + ch) & 0x7fffffff; }
        return NewsIconPaletteClasses[hash % NewsIconPaletteClasses.Length];
    }

    private static string FormatRelative(DateTime when)
    {
        var delta = DateTime.UtcNow - when;
        if (delta.TotalSeconds < 0)
        {
            var ahead = -delta;
            if (ahead.TotalMinutes < 60) return $"in {(int)ahead.TotalMinutes}m";
            if (ahead.TotalHours < 24) return $"in {(int)ahead.TotalHours}h";
            if (ahead.TotalDays < 14) return $"in {(int)ahead.TotalDays}d";
            return when.ToString("MMM d");
        }
        if (delta.TotalMinutes < 60) return $"{(int)delta.TotalMinutes}m ago";
        if (delta.TotalHours < 24)
        {
            var hours = (int)delta.TotalHours;
            var mins = delta.Minutes;
            return $"{hours}h {mins}m";
        }
        if (delta.TotalDays < 14)
        {
            var days = (int)delta.TotalDays;
            var hours = delta.Hours;
            return $"{days}d {hours}h";
        }
        return when.ToString("MMM d");
    }

    private static string FormatShortDuration(TimeSpan span)
    {
        if (span.TotalSeconds < 0) span = TimeSpan.Zero;
        if (span.TotalDays >= 1)
        {
            var days = (int)span.TotalDays;
            var hours = span.Hours;
            return hours > 0 ? $"{days}d {hours}h" : $"{days}d";
        }
        if (span.TotalHours >= 1)
        {
            var hours = (int)span.TotalHours;
            var mins = span.Minutes;
            return mins > 0 ? $"{hours}h {mins}m" : $"{hours}h";
        }
        return $"{(int)span.TotalMinutes}m";
    }
}
