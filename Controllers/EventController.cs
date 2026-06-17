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
public partial class EventController : Controller
{
    private readonly ApplicationDbContext _context;
    private readonly UserManager<AppUser> _userManager;
    private readonly TimeZoneConversionService _timeZones;

    public EventController(
        ApplicationDbContext context,
        UserManager<AppUser> userManager,
        TimeZoneConversionService timeZones)
    {
        _context = context;
        _userManager = userManager;
        _timeZones = timeZones;
    }
    private async Task<EventViewModel> BuildEventViewModelAsync(AppUser user, EventViewModel? source = null)
    {
        var linkshells = await _context.AppUserLinkshells
            .Where(link =>
                link.AppUserId == user.Id &&
                (link.Rank == LinkshellRanks.Leader || link.Rank == LinkshellRanks.Officer))
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

        var availablePartySetups = selectedLinkshellId > 0
            ? await _context.PartySetups
                .Where(setup => setup.LinkshellId == selectedLinkshellId)
                .OrderBy(setup => setup.Name)
                .Select(setup => new PartySetupOption
                {
                    Id = setup.Id,
                    Name = setup.Name,
                    AssignedMonsterName = setup.AssignedMonsterName,
                    EventType = setup.EventType
                })
                .ToListAsync()
            : new List<PartySetupOption>();

        return new EventViewModel
        {
            Event = eventDraft,
            PartySetupId = source?.PartySetupId ?? eventDraft.PartySetupId,
            AvailablePartySetups = availablePartySetups,
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
                (link.Rank == LinkshellRanks.Leader || link.Rank == LinkshellRanks.Officer))
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
        return LinkshellRanks.IsLeaderOrOfficer(membership?.Rank);
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

        // Match the winner by the membership's MAIN character OR either ALT (alts
        // share the main's account, so loot won on an alt deducts from the one
        // account balance). Requires the memberships to be loaded WITH AppUser.
        return linkshellMemberships.FirstOrDefault(link =>
            string.Equals(NormalizeLookupKey(link.CharacterName), normalizedWinner, StringComparison.OrdinalIgnoreCase)
            || string.Equals(NormalizeLookupKey(link.AppUser?.AltCharacterName1), normalizedWinner, StringComparison.OrdinalIgnoreCase)
            || string.Equals(NormalizeLookupKey(link.AppUser?.AltCharacterName2), normalizedWinner, StringComparison.OrdinalIgnoreCase));
    }

    internal static string? NormalizeLookupKey(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private DateTime? ConvertUtcToUserTimeZone(DateTime? utcDateTime, string? timeZoneId)
        => _timeZones.ToUserTime(utcDateTime, timeZoneId);

    private DateTime? ConvertUserTimeZoneToUtc(DateTime? localDateTime, string? timeZoneId)
        => _timeZones.ToUtc(localDateTime, timeZoneId);
}
