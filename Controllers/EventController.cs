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
    private readonly AdminOverrideService _adminOverride;
    private readonly TimeZoneConversionService _timeZones;
    private readonly DkpLedgerWriter _dkpLedger;
    private readonly DkpPoolResolver _dkpPools;
    private readonly DkpPoolBalanceService _dkpPoolBalances;
    private readonly HnmCampReviewHandoffService _campReviewHandoff;
    private readonly AttendanceSectionsBuilder _attendanceSections;
    private readonly MonsterTimingResolver _monsterTimings;

    public EventController(
        ApplicationDbContext context,
        UserManager<AppUser> userManager,
        AdminOverrideService adminOverride,
        TimeZoneConversionService timeZones,
        DkpLedgerWriter dkpLedger,
        DkpPoolResolver dkpPools,
        DkpPoolBalanceService dkpPoolBalances,
        HnmCampReviewHandoffService campReviewHandoff,
        AttendanceSectionsBuilder attendanceSections,
        MonsterTimingResolver monsterTimings)
    {
        _context = context;
        _userManager = userManager;
        _adminOverride = adminOverride;
        _timeZones = timeZones;
        _dkpLedger = dkpLedger;
        _dkpPools = dkpPools;
        _dkpPoolBalances = dkpPoolBalances;
        _campReviewHandoff = campReviewHandoff;
        _attendanceSections = attendanceSections;
        _monsterTimings = monsterTimings;
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

        // The linkshell's own monster catalog, already merged and including anything it added
        // itself. Falls back to the built-in merged list when no linkshell is resolved yet.
        var monsterTimings = await _monsterTimings.GetMapAsync(selectedLinkshellId, HttpContext.RequestAborted);

        var eventDraft = source?.Event ?? new Event();
        if (!isExistingEvent)
        {
            eventDraft.LinkshellId = selectedLinkshellId;
        }

        // The linkshell's standing Repeat-on-ToD boards, flattened to every spelling of each spawn
        // so the form's monster picker can pre-fill the recurrence toggle + lead for whatever is
        // chosen. Only ENABLED boards are carried: a disabled row's lead is stale bookkeeping, and
        // offering it would re-apply it the moment the toggle was flipped back on.
        var monsterRepeatLeads = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        if (selectedLinkshellId > 0)
        {
            var enabledBoards = await _context.HnmRecurringBoards
                .AsNoTracking()
                .Where(board => board.LinkshellId == selectedLinkshellId && board.Enabled)
                .Select(board => new { board.MonsterName, board.LeadHours })
                .ToListAsync(HttpContext.RequestAborted);
            foreach (var board in enabledBoards)
            {
                foreach (var name in HnmConfig.MonsterMatchNames(board.MonsterName))
                {
                    monsterRepeatLeads.TryAdd(name, board.LeadHours);
                }
            }
        }

        var availablePartySetups = selectedLinkshellId > 0
            ? await _context.PartySetups
                // OwnerEventId == null → reusable templates only (exclude per-event snapshots).
                .Where(setup => setup.LinkshellId == selectedLinkshellId && setup.OwnerEventId == null)
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

        var selectedLinkshell = linkshells.FirstOrDefault(linkshell => linkshell.Id == selectedLinkshellId);
        // Fail-closed to Standard, matching the server-side payout (Linkshell.HnmAttendanceMode).
        var isWdMode = string.Equals(
            selectedLinkshell?.HnmAttendanceMode, HnmAttendanceModes.Wd, StringComparison.OrdinalIgnoreCase);

        // Predicted repops the linkshell is still waiting on, flattened into a name → Start-input
        // value map for the form's monster picker. Each entry's match names are disjoint from
        // every other entry's (UpcomingRepopLookup keeps one row per spawn), so no key collides.
        var upcomingRepops = await UpcomingRepopLookup.ForLinkshellAsync(
            _context, selectedLinkshellId, DateTime.UtcNow, HttpContext.RequestAborted);
        var upcomingRepopStarts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var repop in upcomingRepops)
        {
            var localStart = ConvertUtcToUserTimeZone(repop.RepopTimeUtc, user.TimeZone);
            if (!localStart.HasValue)
            {
                continue;
            }

            var value = localStart.Value.ToString("yyyy-MM-ddTHH:mm:ss");
            foreach (var name in repop.MatchNames)
            {
                upcomingRepopStarts.TryAdd(name, value);
            }
        }

        return new EventViewModel
        {
            Event = eventDraft,
            PartySetupId = source?.PartySetupId ?? eventDraft.PartySetupId,
            AvailablePartySetups = availablePartySetups,
            // Seed for the inline "Create New Party Setup" modal on the event form.
            // Only the linkshell scope is event-specific; the editor's option lists
            // (roles/jobs/monsters/event types) default on the view model.
            PartySetupEditor = selectedLinkshellId > 0
                ? new PartySetupEditorViewModel
                {
                    LinkshellId = selectedLinkshellId,
                    LinkshellName = selectedLinkshell?.LinkshellName,
                    Slots = new()
                }
                : null,
            Linkshells = linkshells,
            LinkshellId = selectedLinkshellId,
            // HNM camp monster picker, from the linkshell's own monster setups. Each merge pair is
            // stored as ONE combined "Base/Stronger" row, so the list arrives merged; the DAY input
            // only changes what the sign-up board prints, not this list.
            MonsterOptions = monsterTimings.EventMonsterOptions.ToList(),
            UpcomingRepopStarts = upcomingRepopStarts,
            RepeatOnTod = source?.RepeatOnTod ?? false,
            RepostLeadHours = source?.RepostLeadHours,
            MonsterRepeatLeads = monsterRepeatLeads,
            // What a camp pays by default, for the form's "03 — Camp DKP" section. Every amount
            // comes from the pair belonging to the ACTIVE attendance mode: the linkshell stores
            // its Standard and Manual Check In amounts separately even though the per-camp
            // override columns are shared, so which default an override falls back to is decided
            // by the camp's mode and nothing else — the same rule HnmCampPricing applies at
            // payout, and the same one the Activity's hnmLinkshellBonus() reads.
            HnmAttendanceMode = selectedLinkshell?.HnmAttendanceMode ?? HnmAttendanceModes.Standard,
            HnmDefaultWindowBonus = isWdMode
                ? selectedLinkshell?.WdDkpPerWindow ?? 0.25d
                : selectedLinkshell?.HnmStandardWindowBonus ?? 0d,
            HnmDefaultOpenBonus = (isWdMode ? selectedLinkshell?.WdOpenBonus : selectedLinkshell?.HnmStandardOpenBonus) ?? 0d,
            HnmDefaultCloseBonus = (isWdMode ? selectedLinkshell?.WdCloseBonus : selectedLinkshell?.HnmStandardCloseBonus) ?? 0d,
            HnmDefaultClaimBonus = (isWdMode ? selectedLinkshell?.WdClaimBonus : selectedLinkshell?.HnmStandardClaimBonus) ?? 0d,
            HnmDefaultKillBonus = (isWdMode ? selectedLinkshell?.WdKillBonus : selectedLinkshell?.HnmStandardKillBonus) ?? 0d,
            IsDkpLootStructure = !string.Equals(selectedLinkshell?.LootStructure, "LootCouncil", StringComparison.OrdinalIgnoreCase)
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

    // Leader/Officer by rank, OR the app-wide admin override. A null membership is
    // rejected first, so the override can only elevate inside a linkshell the user
    // has actually joined. See AdminOverrideService.
    private async Task<bool> CanManageLinkshellAsync(AppUserLinkshell? membership)
    {
        if (membership is null) return false;
        return LinkshellRanks.IsLeaderOrOfficer(membership.Rank)
               || await _adminOverride.IsActiveForAsync(membership.AppUserId, HttpContext.RequestAborted);
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
