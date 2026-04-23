using LinkshellManagerDiscordApp.Data;
using LinkshellManagerDiscordApp.Models;
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

    private static readonly string[] HnmPaletteClasses = { "a", "b", "c", "d", "e", "f" };
    private const int HnmClaimsWindowDays = 30;
    private static readonly TimeSpan SoonThreshold = TimeSpan.FromHours(3);
    private static readonly TimeSpan DefaultSpawnWindow = TimeSpan.FromHours(3);

    public DashboardController(ApplicationDbContext context, UserManager<AppUser> userManager)
    {
        _context = context;
        _userManager = userManager;
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

        var events = selectedLinkshellId.HasValue
            ? await _context.Events
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

        var selectedLinkshellName = linkshells.FirstOrDefault(linkshell => linkshell.Id == selectedLinkshellId)?.LinkshellName;

        var todTracker = BuildTodTracker(tods);
        var hnmClaims = BuildHnmClaims(tods, out var hnmTotal);
        var recentActivity = BuildRecentActivity(eventHistories, tods);
        var newsUpdates = BuildNewsUpdates(tods, user.Id);

        return View(new DashboardViewModel
        {
            Linkshells = linkshells,
            SelectedLinkshellId = selectedLinkshellId,
            SelectedLinkshellName = selectedLinkshellName,
            Members = members,
            Events = events,
            TotalMembers = members.Count,
            UpcomingEvents = events.Count(evt => evt.CommencementStartTime is null),
            CompletedEvents = eventHistories.Count,
            TodTracker = todTracker,
            HnmClaims = hnmClaims,
            HnmClaimsTotal = hnmTotal,
            HnmClaimsWindowDays = HnmClaimsWindowDays,
            RecentActivity = recentActivity,
            NewsUpdates = newsUpdates,
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

    private static List<HnmClaimEntry> BuildHnmClaims(IReadOnlyCollection<Tod> tods, out int total)
    {
        var cutoff = DateTime.UtcNow - TimeSpan.FromDays(HnmClaimsWindowDays);
        var claims = tods
            .Where(tod => tod.Claim)
            .Where(tod => !string.IsNullOrWhiteSpace(tod.MonsterName))
            .Where(tod => (tod.Time ?? tod.TimeStamp ?? DateTime.MinValue) >= cutoff)
            .GroupBy(tod => tod.MonsterName!.Trim(), StringComparer.OrdinalIgnoreCase)
            .Select(group => new { Monster = group.Key, Count = group.Count() })
            .OrderByDescending(item => item.Count)
            .ThenBy(item => item.Monster)
            .ToList();

        total = claims.Sum(item => item.Count);
        if (total == 0)
        {
            return new List<HnmClaimEntry>();
        }

        var computedTotal = total;
        return claims
            .Select((item, index) => new HnmClaimEntry
            {
                MonsterName = item.Monster,
                Count = item.Count,
                Percent = Math.Round(item.Count * 100.0 / computedTotal, 1),
                ColorClass = HnmPaletteClasses[index % HnmPaletteClasses.Length]
            })
            .Take(6)
            .ToList();
    }

    private static List<RecentActivityEntry> BuildRecentActivity(IReadOnlyCollection<EventHistory> eventHistories, IReadOnlyCollection<Tod> tods)
    {
        var items = new List<RecentActivityEntry>();

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
            var title = tod.Claim
                ? $"{tod.MonsterName} claimed"
                : $"{tod.MonsterName} defeated — No Claim";
            items.Add(new RecentActivityEntry
            {
                When = when,
                RelativeTime = FormatRelative(when),
                DotClass = tod.Claim ? "claim" : "kill",
                Title = title,
                Subtitle = tod.TodLootDetails.Count > 0 ? $"{tod.TodLootDetails.Count} loot" : null
            });
        }

        return items
            .OrderByDescending(item => item.When)
            .Take(12)
            .ToList();
    }

    private static List<NewsUpdateEntry> BuildNewsUpdates(IReadOnlyCollection<Tod> tods, string currentUserId)
    {
        var items = new List<NewsUpdateEntry>();
        foreach (var tod in tods.Take(40))
        {
            var when = tod.Time ?? tod.TimeStamp ?? DateTime.UtcNow;
            foreach (var loot in tod.TodLootDetails.OrderByDescending(l => l.Id))
            {
                if (string.IsNullOrWhiteSpace(loot.ItemName)) continue;
                items.Add(new NewsUpdateEntry
                {
                    Title = loot.ItemWinner is { Length: > 0 } winner
                        ? $"{loot.ItemName}"
                        : loot.ItemName!,
                    Subtitle = string.IsNullOrWhiteSpace(loot.ItemWinner)
                        ? $"{tod.MonsterName} defeated"
                        : $"{tod.MonsterName} defeated · {loot.ItemWinner}",
                    Dkp = loot.WinningDkpSpent,
                    RelativeTime = FormatRelative(when),
                    IsMine = false,
                    ColorClass = PickColorFromName(loot.ItemName!)
                });
            }
        }

        return items.Take(8).ToList();
    }

    private static string PickColorFromName(string name)
    {
        if (string.IsNullOrEmpty(name)) return "a";
        var hash = 0;
        foreach (var ch in name) { hash = (hash * 31 + ch) & 0x7fffffff; }
        return HnmPaletteClasses[hash % HnmPaletteClasses.Length];
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
