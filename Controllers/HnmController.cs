using LinkshellManagerDiscordApp.Data;
using LinkshellManagerDiscordApp.Models;
using LinkshellManagerDiscordApp.Services;
using LinkshellManagerDiscordApp.ViewModels;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace LinkshellManagerDiscordApp.Controllers;

// Streamlined HNM dashboard: one card per curated HNM with its latest ToD,
// the auto-created next-repop event, the last few ToDs as a mini-history,
// and any AttendanceSnapshots linked to the queued event. This page is the
// dedicated home for HNM activity -- the generic ToDs / Events pages filter
// these monsters out so they're not duplicated.
[Authorize]
public sealed class HnmController : Controller
{
    private const int RecentTodHistoryCount = 3;

    private readonly ApplicationDbContext _db;
    private readonly UserManager<AppUser> _userManager;
    private readonly TimeZoneConversionService _timeZones;

    public HnmController(
        ApplicationDbContext db,
        UserManager<AppUser> userManager,
        TimeZoneConversionService timeZones)
    {
        _db = db;
        _userManager = userManager;
        _timeZones = timeZones;
    }

    [HttpGet("/hnm")]
    public async Task<IActionResult> Index(CancellationToken cancellationToken)
    {
        var user = await _userManager.GetUserAsync(User);
        if (user is null) return Challenge();

        var memberships = await _db.AppUserLinkshells
            .AsNoTracking()
            .Include(link => link.Linkshell)
            .Where(link => link.AppUserId == user.Id)
            .ToListAsync(cancellationToken);

        var linkshells = memberships
            .Where(link => link.Linkshell is not null)
            .Select(link => link.Linkshell!)
            .OrderBy(ls => ls.LinkshellName)
            .ToList();

        if (linkshells.Count == 0)
        {
            return View(new HnmDashboardViewModel
            {
                LinkshellId = 0,
                Linkshells = linkshells,
            });
        }

        var activeLinkshellId = user.PrimaryLinkshellId.HasValue
            && linkshells.Any(ls => ls.Id == user.PrimaryLinkshellId.Value)
                ? user.PrimaryLinkshellId.Value
                : linkshells[0].Id;

        var activeLinkshell = linkshells.First(ls => ls.Id == activeLinkshellId);

        var role = await GetEffectiveRoleAsync(user.Id, activeLinkshellId, cancellationToken);
        var canManageEvents = role?.CanManageEvents == true;

        var rows = await BuildRowsAsync(activeLinkshellId, user.TimeZone, cancellationToken);

        return View(new HnmDashboardViewModel
        {
            LinkshellId = activeLinkshellId,
            LinkshellName = activeLinkshell.LinkshellName,
            HnmSectionEnabled = activeLinkshell.EnableHnmSection,
            CanManageEvents = canManageEvents,
            Rows = rows,
            Linkshells = linkshells,
        });
    }

    private async Task<IReadOnlyList<HnmDashboardRowViewModel>> BuildRowsAsync(
        int linkshellId,
        string? userTimeZoneId,
        CancellationToken cancellationToken)
    {
        // Single-pass query for every HNM in this linkshell -- pull all ToDs
        // for the curated set then bucket by monster client-side. Cheaper than
        // round-tripping per-monster, since each LS only has a handful of HNM
        // rows on the active table at any time.
        var hnmNames = HnmConfig.LongWindowHnms
            .Concat(HnmConfig.ShortWindowHnms)
            .Concat(HnmConfig.TestingHnms)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var tods = await _db.Tods
            .AsNoTracking()
            .Where(t => t.LinkshellId == linkshellId && t.MonsterName != null && hnmNames.Contains(t.MonsterName))
            .OrderByDescending(t => t.Time)
            .ThenByDescending(t => t.Id)
            .ToListAsync(cancellationToken);

        // Latest ToD per monster -> auto-event lookup (if any).
        var latestTodIds = tods
            .GroupBy(t => t.MonsterName!, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First().Id)
            .ToList();

        var linkedEvents = await _db.Events
            .AsNoTracking()
            .Where(e => e.LinkshellId == linkshellId && e.SourceTodId != null && latestTodIds.Contains(e.SourceTodId!.Value))
            .ToListAsync(cancellationToken);

        var linkedEventBySourceTod = linkedEvents.ToDictionary(e => e.SourceTodId!.Value, e => e);

        var linkedEventIds = linkedEvents.Select(e => e.Id).ToList();
        var linkedSnapshots = linkedEventIds.Count > 0
            ? await _db.AttendanceSnapshots
                .AsNoTracking()
                .Where(s => s.LinkshellId == linkshellId && s.LinkedEventId != null && linkedEventIds.Contains(s.LinkedEventId!.Value))
                .OrderByDescending(s => s.CapturedAtUtc)
                .ToListAsync(cancellationToken)
            : new List<AttendanceSnapshot>();

        var snapshotsByEventId = linkedSnapshots
            .Where(s => s.LinkedEventId.HasValue)
            .GroupBy(s => s.LinkedEventId!.Value)
            .ToDictionary(g => g.Key, g => g.ToList());

        var userZone = _timeZones.Resolve(userTimeZoneId);

        // Build a row per curated HNM in a stable order: long-window first
        // (the rarer cohort, more interesting to officers), then short-window,
        // then testing presets at the bottom.
        var orderedNames = HnmConfig.LongWindowHnms
            .Concat(HnmConfig.ShortWindowHnms)
            .Concat(HnmConfig.TestingHnms)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var rows = new List<HnmDashboardRowViewModel>(orderedNames.Count);
        foreach (var name in orderedNames)
        {
            var monsterTods = tods
                .Where(t => string.Equals(t.MonsterName, name, StringComparison.OrdinalIgnoreCase))
                .ToList();
            var latest = monsterTods.FirstOrDefault();
            var linkedEvent = latest is not null && linkedEventBySourceTod.TryGetValue(latest.Id, out var ev) ? ev : null;
            var snapshots = linkedEvent is not null && snapshotsByEventId.TryGetValue(linkedEvent.Id, out var snaps)
                ? snaps
                : new List<AttendanceSnapshot>();

            rows.Add(new HnmDashboardRowViewModel
            {
                MonsterName = name,
                Zone = HnmConfig.HnmZones.TryGetValue(name, out var zone) ? zone : null,
                WindowCount = HnmConfig.GetWindowCount(name),
                IsDayTracked = HnmConfig.HnmDayCycles.ContainsKey(name),
                DayCycle = HnmConfig.HnmDayCycles.TryGetValue(name, out var cycle) ? cycle : null,
                LatestTod = latest is null ? null : ToSummary(latest, userZone),
                LinkedEvent = linkedEvent is null ? null : ToSummary(linkedEvent),
                RecentTods = monsterTods.Skip(1).Take(RecentTodHistoryCount).Select(t => ToSummary(t, userZone)).ToList(),
                LinkedSnapshots = snapshots.Select(s => ToSummary(s, userZone)).ToList(),
            });
        }

        return rows;
    }

    private HnmTodSummary ToSummary(Tod tod, NodaTime.DateTimeZone userZone)
    {
        return new HnmTodSummary
        {
            Id = tod.Id,
            TimeUtc = tod.Time,
            TimeDisplayUtc = FormatUtc(tod.Time),
            TimeDisplayLocal = _timeZones.ToUserTime(tod.Time, userZone.Id)?.ToString("M/d/yyyy h:mm tt") ?? "-",
            RepopTimeUtc = tod.RepopTime,
            RepopDisplayLocal = _timeZones.ToUserTime(tod.RepopTime, userZone.Id)?.ToString("M/d/yyyy h:mm tt") ?? "-",
            Cooldown = tod.Cooldown,
            Claim = tod.Claim,
            DayNumber = tod.DayNumber,
        };
    }

    private HnmLinkedEventSummary ToSummary(Event ev)
    {
        return new HnmLinkedEventSummary
        {
            Id = ev.Id,
            EventName = ev.EventName ?? $"Event #{ev.Id}",
            EventLocation = ev.EventLocation,
            StartTimeUtc = ev.StartTime,
            StartTimeDisplayLocal = ev.StartTime.HasValue ? ev.StartTime.Value.ToString("u") : "-",
            IsLive = ev.CommencementStartTime.HasValue && !ev.EndTime.HasValue,
            IsEnded = ev.EndTime.HasValue,
            DkpPerHour = ev.DkpPerHour,
            WindowCount = ev.WindowCountOverride ?? HnmConfig.GetWindowCount(ev.EventName),
            AttInputEntryType = ev.AttInputEntryType,
        };
    }

    private HnmLinkedSnapshotSummary ToSummary(AttendanceSnapshot snapshot, NodaTime.DateTimeZone userZone)
    {
        return new HnmLinkedSnapshotSummary
        {
            Id = snapshot.Id,
            Name = snapshot.Name,
            CapturedAtUtc = snapshot.CapturedAtUtc,
            CapturedAtLocalDisplay = _timeZones.ToUserTime(snapshot.CapturedAtUtc, userZone.Id)?.ToString("M/d/yyyy h:mm tt") ?? "-",
            CapturedByCharacterName = snapshot.CapturedByCharacterName,
            EntryCount = snapshot.EntryCount,
        };
    }

    private static string FormatUtc(DateTime? utc)
    {
        return utc.HasValue ? utc.Value.ToString("u") : "-";
    }

    private async Task<LinkshellRole?> GetEffectiveRoleAsync(string appUserId, int linkshellId, CancellationToken cancellationToken)
    {
        var rank = await _db.AppUserLinkshells
            .Where(m => m.AppUserId == appUserId && m.LinkshellId == linkshellId)
            .Select(m => m.Rank)
            .FirstOrDefaultAsync(cancellationToken);
        if (rank is null) return null;
        var rankName = string.IsNullOrWhiteSpace(rank) ? "Member" : rank.Trim();
        return await _db.LinkshellRoles
            .AsNoTracking()
            .FirstOrDefaultAsync(r => r.LinkshellId == linkshellId && r.Name == rankName, cancellationToken);
    }
}
