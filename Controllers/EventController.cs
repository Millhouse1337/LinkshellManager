using LinkshellManagerDiscordApp.Data;
using LinkshellManagerDiscordApp.Models;
using LinkshellManagerDiscordApp.ViewModels;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NodaTime;

namespace LinkshellManagerDiscordApp.Controllers;

[Authorize]
public partial class EventController : Controller
{
    private readonly ApplicationDbContext _context;
    private readonly UserManager<AppUser> _userManager;
    private readonly ILogger<EventController> _logger;
    private readonly IDateTimeZoneProvider _dateTimeZoneProvider;

    public EventController(
        ApplicationDbContext context,
        UserManager<AppUser> userManager,
        ILogger<EventController> logger,
        IDateTimeZoneProvider dateTimeZoneProvider)
    {
        _context = context;
        _userManager = userManager;
        _logger = logger;
        _dateTimeZoneProvider = dateTimeZoneProvider;
    }
    private async Task<EventViewModel> BuildEventViewModelAsync(AppUser user, EventViewModel? source = null)
    {
        var linkshells = await _context.AppUserLinkshells
            .Where(link =>
                link.AppUserId == user.Id &&
                (link.Rank == "Leader" || link.Rank == "Officer"))
            .Select(link => link.Linkshell!)
            .OrderBy(linkshell => linkshell.LinkshellName)
            .ToListAsync();

        var isExistingEvent = source?.Event?.Id > 0;
        var selectedLinkshellId = isExistingEvent && source?.Event?.LinkshellId > 0
            ? source.Event.LinkshellId
            : user.PrimaryLinkshellId ?? linkshells.FirstOrDefault()?.Id ?? 0;
        if (selectedLinkshellId > 0 && linkshells.All(linkshell => linkshell.Id != selectedLinkshellId))
        {
            selectedLinkshellId = linkshells.FirstOrDefault()?.Id ?? 0;
        }

        var eventDraft = source?.Event ?? new Event();
        if (!isExistingEvent)
        {
            eventDraft.LinkshellId = selectedLinkshellId;
        }

        return new EventViewModel
        {
            Event = eventDraft,
            Jobs = source?.Jobs ?? new List<Job>(),
            Linkshells = linkshells,
            LinkshellId = selectedLinkshellId
        };
    }

    private async Task<AppUser?> RequireCurrentUserAsync() => await _userManager.GetUserAsync(User);

    private async Task<AppUserLinkshell?> GetMembershipAsync(string appUserId, int linkshellId)
    {
        return await _context.AppUserLinkshells
            .Include(link => link.Linkshell)
            .FirstOrDefaultAsync(link => link.AppUserId == appUserId && link.LinkshellId == linkshellId);
    }

    private async Task<int> ResolveActiveManageableLinkshellIdAsync(AppUser user)
    {
        var linkshellIds = await _context.AppUserLinkshells
            .Where(link =>
                link.AppUserId == user.Id &&
                (link.Rank == "Leader" || link.Rank == "Officer"))
            .OrderBy(link => link.Linkshell!.LinkshellName)
            .Select(link => link.LinkshellId)
            .ToListAsync();

        if (user.PrimaryLinkshellId.HasValue && linkshellIds.Contains(user.PrimaryLinkshellId.Value))
        {
            return user.PrimaryLinkshellId.Value;
        }

        return linkshellIds.FirstOrDefault();
    }

    private static bool CanManageLinkshell(AppUserLinkshell? membership)
    {
        if (membership is null || string.IsNullOrWhiteSpace(membership.Rank))
        {
            return false;
        }

        return membership.Rank.Equals("Leader", StringComparison.OrdinalIgnoreCase) ||
               membership.Rank.Equals("Officer", StringComparison.OrdinalIgnoreCase);
    }

    internal static double CalculateAccumulatedDurationHours(AppUserEvent participation, DateTime referenceUtc, DateTime? eventStartUtc)
    {
        var accumulatedHours = Math.Max(0, participation.Duration ?? 0);
        if (participation.IsOnBreak == true)
        {
            return accumulatedHours;
        }

        var segmentStart = participation.ResumeTime ?? participation.StartTime ?? eventStartUtc;
        if (!segmentStart.HasValue)
        {
            return accumulatedHours;
        }

        var segmentHours = Math.Max(0, (referenceUtc - segmentStart.Value).TotalHours);
        return accumulatedHours + segmentHours;
    }

    internal static AppUserLinkshell? ResolveLootWinnerMembership(
        string? itemWinner,
        IReadOnlyDictionary<string, AppUserLinkshell> membershipsByAppUserId,
        IReadOnlyDictionary<string, AppUserEvent> participantsByCharacterName,
        IEnumerable<AppUserLinkshell> linkshellMemberships)
    {
        var normalizedWinner = NormalizeLookupKey(itemWinner);
        if (normalizedWinner is null)
        {
            return null;
        }

        if (participantsByCharacterName.TryGetValue(normalizedWinner, out var participation) &&
            !string.IsNullOrWhiteSpace(participation.AppUserId) &&
            membershipsByAppUserId.TryGetValue(participation.AppUserId, out var participantMembership))
        {
            return participantMembership;
        }

        return linkshellMemberships.FirstOrDefault(link =>
            string.Equals(NormalizeLookupKey(link.CharacterName), normalizedWinner, StringComparison.OrdinalIgnoreCase));
    }

    internal static string? NormalizeLookupKey(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private DateTime? ConvertUtcToUserTimeZone(DateTime? utcDateTime, string? timeZoneId)
    {
        if (!utcDateTime.HasValue)
        {
            return null;
        }

        var zone = ResolveTimeZone(timeZoneId);
        var instant = Instant.FromDateTimeUtc(DateTime.SpecifyKind(utcDateTime.Value, DateTimeKind.Utc));
        return instant.InZone(zone).ToDateTimeUnspecified();
    }

    private DateTime? ConvertUserTimeZoneToUtc(DateTime? localDateTime, string? timeZoneId)
    {
        if (!localDateTime.HasValue)
        {
            return null;
        }

        var zone = ResolveTimeZone(timeZoneId);
        var zonedDateTime = zone.AtLeniently(LocalDateTime.FromDateTime(localDateTime.Value));
        return zonedDateTime.ToDateTimeUtc();
    }

    private DateTimeZone ResolveTimeZone(string? timeZoneId)
    {
        if (!string.IsNullOrWhiteSpace(timeZoneId) && _dateTimeZoneProvider.Ids.Contains(timeZoneId))
        {
            return _dateTimeZoneProvider[timeZoneId];
        }

        // Truncate user-supplied value before logging to keep arbitrary input
        // bounded — log sinks that render structured fields as HTML/markdown
        // can otherwise be tricked into rendering malicious payloads.
        var safeTimeZoneId = string.IsNullOrEmpty(timeZoneId)
            ? string.Empty
            : timeZoneId.Length > 64 ? timeZoneId[..64] : timeZoneId;
        _logger.LogWarning("Unknown time zone '{TimeZoneId}', falling back to UTC.", safeTimeZoneId);
        return DateTimeZone.Utc;
    }
}
