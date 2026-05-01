using System.Globalization;
using System.Net.Http.Headers;
using LinkshellManagerDiscordApp.Data;
using LinkshellManagerDiscordApp.Models;
using LinkshellManagerDiscordApp.Services;
using LinkshellManagerDiscordApp.ViewModels;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NodaTime;

namespace LinkshellManagerDiscordApp.Controllers;

[ApiController]
[Route("api/activity")]
public sealed class ActivityDataController : ControllerBase
{
    private const string PendingInviteStatus = "PendingInvite";
    private const string PendingJoinRequestStatus = "PendingJoinRequest";
    private static readonly HashSet<string> SupportedTodMonsters = new(TodManagerViewModel.SupportedMonsters, StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> SupportedTodCooldowns = new(TodManagerViewModel.SupportedCooldowns, StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> SupportedTodIntervals = new(TodManagerViewModel.SupportedIntervals, StringComparer.OrdinalIgnoreCase);

    private readonly ApplicationDbContext _dbContext;
    private readonly DiscordIdentityService _discordIdentityService;
    private readonly AppUserProfileService _appUserProfileService;
    private readonly UserManager<AppUser> _userManager;
    private readonly IHostEnvironment _environment;
    private readonly Microsoft.AspNetCore.Hosting.IWebHostEnvironment _webHostEnvironment;
    private readonly IDateTimeZoneProvider _dateTimeZoneProvider;

    public ActivityDataController(
        ApplicationDbContext dbContext,
        DiscordIdentityService discordIdentityService,
        AppUserProfileService appUserProfileService,
        UserManager<AppUser> userManager,
        IHostEnvironment environment,
        Microsoft.AspNetCore.Hosting.IWebHostEnvironment webHostEnvironment,
        IDateTimeZoneProvider dateTimeZoneProvider)
    {
        _dbContext = dbContext;
        _discordIdentityService = discordIdentityService;
        _appUserProfileService = appUserProfileService;
        _userManager = userManager;
        _environment = environment;
        _webHostEnvironment = webHostEnvironment;
        _dateTimeZoneProvider = dateTimeZoneProvider;
    }

    [HttpGet("overview")]
    public async Task<IActionResult> GetOverviewAsync(CancellationToken cancellationToken)
    {
        var appUser = await ResolveAppUserAsync(cancellationToken);
        if (appUser is null)
        {
            return Unauthorized(new
            {
                error = "Sign in with ASP.NET Identity or provide a Discord bearer token to load Activity data."
            });
        }

        var linkshellMemberships = await _dbContext.AppUserLinkshells
            .Include(link => link.Linkshell)
            .Where(link => link.AppUserId == appUser.Id)
            .OrderBy(link => link.Linkshell!.LinkshellName)
            .ToListAsync(cancellationToken);

        var linkshellIds = linkshellMemberships
            .Select(link => link.LinkshellId)
            .Distinct()
            .ToList();
        var primaryLinkshellId = appUser.PrimaryLinkshellId ?? linkshellMemberships.FirstOrDefault()?.LinkshellId;

        var memberCounts = await _dbContext.AppUserLinkshells
            .Where(link => linkshellIds.Contains(link.LinkshellId))
            .GroupBy(link => link.LinkshellId)
            .Select(group => new { LinkshellId = group.Key, Count = group.Count() })
            .ToDictionaryAsync(item => item.LinkshellId, item => item.Count, cancellationToken);

        var itemCounts = await _dbContext.Items
            .Where(item => linkshellIds.Contains(item.LinkshellId))
            .GroupBy(item => item.LinkshellId)
            .Select(group => new { LinkshellId = group.Key, Count = group.Count() })
            .ToDictionaryAsync(item => item.LinkshellId, item => item.Count, cancellationToken);

        var revenueTotals = await _dbContext.RevenueEntries
            .Where(entry => linkshellIds.Contains(entry.LinkshellId))
            .GroupBy(entry => entry.LinkshellId)
            .Select(group => new { LinkshellId = group.Key, Total = group.Sum(entry => entry.Value) })
            .ToDictionaryAsync(item => item.LinkshellId, item => item.Total, cancellationToken);

        foreach (var linkId in linkshellIds)
        {
            await EnsureDefaultRolesAsync(linkId, cancellationToken);
        }

        var allRoles = await _dbContext.LinkshellRoles
            .Where(r => linkshellIds.Contains(r.LinkshellId))
            .ToListAsync(cancellationToken);
        var rolesByLinkshellAndName = allRoles
            .GroupBy(r => r.LinkshellId)
            .ToDictionary(
                g => g.Key,
                g => g.ToDictionary(r => r.Name, StringComparer.OrdinalIgnoreCase));

        // Cap is generous because this list feeds both the Live ops panel and the
        // Pending Events queue in one fetch — splitting at 8 was clipping queued
        // rows whenever the linkshell had more than a couple of live events going.
        var activeEvents = await _dbContext.Events
            .Include(evt => evt.Jobs)
            .Include(evt => evt.AppUserEvents)
                .ThenInclude(participation => participation.StatusLedgerEntries)
            .Include(evt => evt.EventLootDetails)
            .Include(evt => evt.AttendanceWindows)
                .ThenInclude(window => window.Attendees)
                    .ThenInclude(attendee => attendee.AppUserEvent)
            .Where(evt => linkshellIds.Contains(evt.LinkshellId))
            .OrderBy(evt => evt.StartTime)
            .Take(50)
            .ToListAsync(cancellationToken);

        var recentHistory = await _dbContext.EventHistories
            .Include(history => history.AppUserEventHistories)
            .Where(history => linkshellIds.Contains(history.LinkshellId))
            .OrderByDescending(history => history.EndTime ?? history.TimeStamp)
            .Take(8)
            .ToListAsync(cancellationToken);

        var recentTods = primaryLinkshellId.HasValue
            ? await _dbContext.Tods
                .AsNoTracking()
                .Include(tod => tod.TodLootDetails)
                .Where(tod => tod.LinkshellId == primaryLinkshellId.Value)
                .OrderByDescending(tod => tod.Time)
                .ThenByDescending(tod => tod.Id)
                .Take(25)
                .ToListAsync(cancellationToken)
            : new List<Tod>();

        var pendingInvites = await _dbContext.Invites
            .Include(invite => invite.Linkshell)
            .Include(invite => invite.AppUser)
            .Where(invite => invite.AppUserId == appUser.Id && invite.Status == PendingInviteStatus)
            .OrderBy(invite => invite.Linkshell!.LinkshellName)
            .ToListAsync(cancellationToken);

        var sentInvites = await _dbContext.Invites
            .Include(invite => invite.Linkshell)
            .Include(invite => invite.AppUser)
            .Where(invite => linkshellIds.Contains(invite.LinkshellId) && invite.Status == PendingInviteStatus)
            .OrderBy(invite => invite.Linkshell!.LinkshellName)
            .ThenBy(invite => invite.AppUser!.CharacterName)
            .ToListAsync(cancellationToken);

        var incomingJoinRequests = await _dbContext.Invites
            .Include(invite => invite.Linkshell)
            .Include(invite => invite.AppUser)
            .Where(invite => linkshellIds.Contains(invite.LinkshellId) && invite.Status == PendingJoinRequestStatus)
            .OrderBy(invite => invite.Linkshell!.LinkshellName)
            .ThenBy(invite => invite.AppUser!.CharacterName)
            .ToListAsync(cancellationToken);

        var outgoingJoinRequests = await _dbContext.Invites
            .Include(invite => invite.Linkshell)
            .Include(invite => invite.AppUser)
            .Where(invite => invite.AppUserId == appUser.Id && invite.Status == PendingJoinRequestStatus)
            .OrderBy(invite => invite.Linkshell!.LinkshellName)
            .ToListAsync(cancellationToken);

        var primaryLinkshell = linkshellMemberships.FirstOrDefault(link => link.LinkshellId == primaryLinkshellId)?.Linkshell;
        var primaryLinkshellMembers = primaryLinkshellId.HasValue
            ? await _dbContext.AppUserLinkshells
                .Include(link => link.AppUser)
                .Where(link => link.LinkshellId == primaryLinkshellId.Value)
                .OrderBy(link => link.CharacterName)
                .ToListAsync(cancellationToken)
            : new List<AppUserLinkshell>();

        var primaryRules = primaryLinkshellId.HasValue
            ? await _dbContext.Rules
                .AsNoTracking()
                .Where(rule => rule.LinkshellId == primaryLinkshellId.Value)
                .OrderByDescending(rule => rule.CreatedAt)
                .ThenByDescending(rule => rule.Id)
                .ToListAsync(cancellationToken)
            : new List<Rule>();

        var primaryAnnouncements = primaryLinkshellId.HasValue
            ? await _dbContext.Announcements
                .AsNoTracking()
                .Where(announcement => announcement.LinkshellId == primaryLinkshellId.Value)
                .OrderByDescending(announcement => announcement.CreatedAt)
                .ThenByDescending(announcement => announcement.Id)
                .ToListAsync(cancellationToken)
            : new List<Announcement>();

        var primaryItems = primaryLinkshellId.HasValue
            ? await _dbContext.Items
                .AsNoTracking()
                .Where(item => item.LinkshellId == primaryLinkshellId.Value)
                .OrderBy(item => item.ItemName)
                .ToListAsync(cancellationToken)
            : new List<Item>();

        var primaryRevenue = primaryLinkshellId.HasValue
            ? await _dbContext.RevenueEntries
                .AsNoTracking()
                .Where(entry => entry.LinkshellId == primaryLinkshellId.Value)
                .OrderByDescending(entry => entry.OccurredAt)
                .ThenByDescending(entry => entry.Id)
                .ToListAsync(cancellationToken)
            : new List<RevenueEntry>();

        Response.Headers.CacheControl = "no-store";
        Response.Headers.Pragma = "no-cache";

        return Ok(new ActivityOverviewDto(
            new ActivityAppUserDto(
                appUser.Id,
                appUser.UserName ?? string.Empty,
                appUser.CharacterName,
                appUser.TimeZone,
                appUser.PrimaryLinkshellId,
                appUser.PrimaryLinkshellName),
            linkshellMemberships.Select(link => new ActivityLinkshellDto(
                link.LinkshellId,
                link.Linkshell?.LinkshellName ?? "Unknown linkshell",
                link.Rank,
                link.Status,
                link.LinkshellDkp,
                memberCounts.GetValueOrDefault(link.LinkshellId, 0),
                itemCounts.GetValueOrDefault(link.LinkshellId, 0),
                revenueTotals.GetValueOrDefault(link.LinkshellId, 0L),
                link.Linkshell?.Details,
                ResolvePermissionsFor(link.LinkshellId, link.Rank, rolesByLinkshellAndName),
                MapLinkshellSettingsDto(link.Linkshell))).ToList(),
            primaryLinkshell is null
                ? null
                : new ActivityPrimaryLinkshellDto(
                    primaryLinkshell.Id,
                    primaryLinkshell.LinkshellName ?? "Unknown linkshell",
                    memberCounts.GetValueOrDefault(primaryLinkshell.Id, 0),
                    primaryLinkshell.Details,
                    primaryLinkshellMembers.Select(member => new ActivityMemberDto(
                        member.Id,
                        member.AppUserId,
                        member.CharacterName ?? member.AppUser?.UserName ?? "Unknown member",
                        member.Rank,
                        member.Status,
                        member.LinkshellDkp)).ToList(),
                    primaryRules.Select(rule => new ActivityRuleDto(
                        rule.Id,
                        rule.LinkshellId,
                        rule.RuleTitle,
                        rule.RuleDetails,
                        rule.CreatedByAppUserId,
                        rule.CreatedByCharacterName,
                        rule.CreatedAt)).ToList(),
                    primaryAnnouncements.Select(announcement => new ActivityAnnouncementDto(
                        announcement.Id,
                        announcement.LinkshellId,
                        announcement.AnnouncementTitle,
                        announcement.AnnouncementDetails,
                        announcement.CreatedByAppUserId,
                        announcement.CreatedByCharacterName,
                        announcement.CreatedAt)).ToList(),
                    primaryItems.Select(item => new ActivityItemDto(
                        item.Id,
                        item.LinkshellId,
                        item.ItemName,
                        item.ItemType,
                        item.Quantity,
                        item.Notes,
                        item.CreatedByAppUserId,
                        item.CreatedByCharacterName,
                        item.CreatedAt,
                        item.UpdatedAt)).ToList(),
                    primaryRevenue.Select(entry => new ActivityRevenueEntryDto(
                        entry.Id,
                        entry.LinkshellId,
                        entry.EntryType,
                        entry.Category,
                        entry.Value,
                        entry.Details,
                        entry.OccurredAt,
                        entry.CreatedByAppUserId,
                        entry.CreatedByCharacterName,
                        entry.CreatedAt)).ToList()),
            activeEvents.Select(evt => new ActivityEventDto(
                evt.Id,
                evt.LinkshellId,
                evt.EventName,
                evt.EventType,
                evt.EventLocation,
                evt.StartTime,
                evt.EndTime,
                evt.CommencementStartTime,
                evt.Duration,
                evt.DkpPerHour,
                evt.Details,
                evt.AppUserEvents.Count,
                evt.Jobs.Sum(job => job.Quantity ?? 0),
                evt.AppUserEvents
                    .Where(participation => participation.AppUserId == appUser.Id)
                    .Select(participation => new ActivityParticipationDto(
                        participation.Id,
                        participation.CharacterName,
                        participation.JobName,
                        participation.SubJobName,
                        participation.JobType,
                        participation.IsQuickJoin,
                        participation.IsVerified,
                        participation.IsOnBreak,
                        participation.StatusLedgerEntries
                            .OrderBy(item => item.OccurredAt)
                            .Select(item => new ActivityStatusLedgerDto(
                                item.Id,
                                item.ActionType,
                                item.OccurredAt,
                                item.RequiresVerification,
                                item.VerifiedAt,
                                item.VerifiedBy,
                                item.DeniedAt,
                                item.DeniedBy,
                                item.Source))
                            .ToList()))
                    .FirstOrDefault(),
                evt.AppUserEvents
                    .OrderBy(participation => participation.IsQuickJoin)
                    .ThenBy(participation => participation.CharacterName)
                    .Select(participation => new ActivityEventParticipantDto(
                        participation.Id,
                        participation.AppUserId,
                        participation.CharacterName,
                        participation.JobName,
                        participation.SubJobName,
                        participation.JobType,
                        participation.IsQuickJoin,
                        participation.IsVerified,
                        participation.Proctor,
                        participation.StartTime,
                        participation.ResumeTime,
                        participation.PauseTime,
                        participation.IsOnBreak,
                        participation.Duration,
                        participation.EventDkp,
                        participation.StatusLedgerEntries
                            .OrderBy(item => item.OccurredAt)
                            .Select(item => new ActivityStatusLedgerDto(
                                item.Id,
                                item.ActionType,
                                item.OccurredAt,
                                item.RequiresVerification,
                                item.VerifiedAt,
                                item.VerifiedBy,
                                item.DeniedAt,
                                item.DeniedBy,
                                item.Source))
                            .ToList()))
                    .ToList(),
                evt.EventLootDetails
                    .OrderByDescending(loot => loot.Id)
                    .Select(loot => new ActivityLootDto(
                        loot.Id,
                        loot.ItemName,
                        loot.ItemWinner,
                        loot.WinningDkpSpent))
                    .ToList(),
                evt.Jobs.Select(job => new ActivityJobDto(
                    job.Id,
                    job.JobName,
                    job.SubJobName,
                    job.JobType,
                    job.Quantity,
                    job.SignedUp,
                    job.Enlisted)).ToList(),
                HnmConfig.GetWindowCount(evt.EventName),
                evt.AttendanceWindows
                    .OrderBy(window => window.SequenceNumber)
                    .Select(window => new ActivityAttendanceWindowDto(
                        window.Id,
                        window.SequenceNumber,
                        window.Label,
                        window.PostedAt,
                        window.Attendees
                            .OrderBy(att => att.AppUserEvent != null ? att.AppUserEvent.CharacterName : string.Empty)
                            .Select(att => new ActivityAttendanceWindowAttendeeDto(
                                att.Id,
                                att.AppUserEvent != null ? att.AppUserEvent.CharacterName : null,
                                att.AppUserEvent != null ? att.AppUserEvent.JobName : null,
                                att.AppUserEvent != null ? att.AppUserEvent.SubJobName : null,
                                att.Zone,
                                att.VerifiedAt,
                                att.VerifiedBy))
                            .ToList()))
                    .ToList())).ToList(),
            pendingInvites.Select(invite => new ActivityInviteDto(
                invite.Id,
                invite.AppUserId,
                invite.LinkshellId,
                invite.AppUser?.CharacterName ?? invite.AppUser?.UserName ?? "Unknown member",
                invite.Linkshell?.LinkshellName ?? "Unknown linkshell",
                invite.Status)).ToList(),
            sentInvites.Select(invite => new ActivityInviteDto(
                invite.Id,
                invite.AppUserId,
                invite.LinkshellId,
                invite.AppUser?.CharacterName ?? invite.AppUser?.UserName ?? "Unknown member",
                invite.Linkshell?.LinkshellName ?? "Unknown linkshell",
                invite.Status)).ToList(),
            incomingJoinRequests.Select(invite => new ActivityInviteDto(
                invite.Id,
                invite.AppUserId,
                invite.LinkshellId,
                invite.AppUser?.CharacterName ?? invite.AppUser?.UserName ?? "Unknown member",
                invite.Linkshell?.LinkshellName ?? "Unknown linkshell",
                invite.Status)).ToList(),
            outgoingJoinRequests.Select(invite => new ActivityInviteDto(
                invite.Id,
                invite.AppUserId,
                invite.LinkshellId,
                invite.AppUser?.CharacterName ?? invite.AppUser?.UserName ?? "Unknown member",
                invite.Linkshell?.LinkshellName ?? "Unknown linkshell",
                invite.Status)).ToList(),
            recentHistory.Select(history => new ActivityHistoryDto(
                history.Id,
                history.LinkshellId,
                history.EventName,
                history.EventType,
                history.EventLocation,
                history.EndTime,
                history.Duration,
                history.AppUserEventHistories.Count)).ToList(),
            recentTods.Select(MapTodDto).ToList(),
            new ActivityOverviewStatsDto(
                linkshellMemberships.Count,
                activeEvents.Count,
                recentHistory.Count,
                activeEvents.Count(evt => evt.CommencementStartTime.HasValue))));
    }

    [HttpGet("history")]
    public async Task<IActionResult> GetHistoryAsync(CancellationToken cancellationToken)
    {
        var appUser = await ResolveAppUserAsync(cancellationToken);
        if (appUser is null)
        {
            return Unauthorized(new
            {
                error = "Sign in with ASP.NET Identity or provide a Discord bearer token to load event history."
            });
        }

        var linkshellIds = await _dbContext.AppUserLinkshells
            .Where(link => link.AppUserId == appUser.Id)
            .Select(link => link.LinkshellId)
            .Distinct()
            .ToListAsync(cancellationToken);

        if (linkshellIds.Count == 0)
        {
            return Ok(Array.Empty<ActivityHistoryDto>());
        }

        var historyQuery = _dbContext.EventHistories
            .Include(history => history.AppUserEventHistories)
            .Where(history => linkshellIds.Contains(history.LinkshellId));

        if (appUser.PrimaryLinkshellId.HasValue)
        {
            historyQuery = historyQuery.Where(history => history.LinkshellId == appUser.PrimaryLinkshellId.Value);
        }

        var histories = await historyQuery
            .OrderByDescending(history => history.EndTime ?? history.TimeStamp)
            .Take(50)
            .ToListAsync(cancellationToken);

        return Ok(histories.Select(history => new ActivityHistoryDto(
            history.Id,
            history.LinkshellId,
            history.EventName,
            history.EventType,
            history.EventLocation,
            history.EndTime,
            history.Duration,
            history.AppUserEventHistories.Count)).ToList());
    }

    [HttpGet("history/{historyId:int}")]
    public async Task<IActionResult> GetHistoryDetailAsync(int historyId, CancellationToken cancellationToken)
    {
        var appUser = await ResolveAppUserAsync(cancellationToken);
        if (appUser is null)
        {
            return Unauthorized(new
            {
                error = "Sign in with ASP.NET Identity or provide a Discord bearer token to load event history details."
            });
        }

        var history = await _dbContext.EventHistories
            .Include(item => item.AppUserEventHistories)
            .FirstOrDefaultAsync(item => item.Id == historyId, cancellationToken);

        if (history is null)
        {
            return NotFound(new { error = "The requested history entry was not found." });
        }

        var hasAccess = await _dbContext.AppUserLinkshells
            .AnyAsync(link => link.AppUserId == appUser.Id && link.LinkshellId == history.LinkshellId, cancellationToken);

        if (!hasAccess)
        {
            return Forbid();
        }

        return Ok(new ActivityHistoryDetailDto(
            history.Id,
            history.LinkshellId,
            history.EventName,
            history.EventType,
            history.EventLocation,
            history.StartTime,
            history.EndTime,
            history.Duration,
            history.DkpPerHour,
            history.Details,
            history.AppUserEventHistories
                .OrderBy(item => item.CharacterName)
                .Select(item => new ActivityHistoryParticipantDto(
                    item.Id,
                    item.AppUserId,
                    item.CharacterName,
                    item.JobName,
                    item.SubJobName,
                    item.JobType,
                    item.Duration,
                    item.EventDkp,
                    item.IsVerified))
                .ToList()));
    }

    [HttpGet("dkp-history")]
    public async Task<IActionResult> GetDkpHistoryAsync(int? linkshellId, string? appUserId, CancellationToken cancellationToken)
    {
        var appUser = await ResolveAppUserAsync(cancellationToken);
        if (appUser is null)
        {
            return Unauthorized(new
            {
                error = "Sign in with ASP.NET Identity or provide a Discord bearer token to load DKP history."
            });
        }

        var accessibleMemberships = await _dbContext.AppUserLinkshells
            .Include(link => link.Linkshell)
            .Where(link => link.AppUserId == appUser.Id)
            .OrderBy(link => link.Linkshell!.LinkshellName)
            .ToListAsync(cancellationToken);

        if (accessibleMemberships.Count == 0)
        {
            return Ok(new ActivityDkpHistoryDto(
                null,
                null,
                null,
                null,
                0,
                Array.Empty<ActivityDkpHistoryMemberDto>(),
                Array.Empty<ActivityDkpLedgerEntryDto>()));
        }

        var selectedLinkshellId = linkshellId
            ?? appUser.PrimaryLinkshellId
            ?? accessibleMemberships.First().LinkshellId;

        if (accessibleMemberships.All(link => link.LinkshellId != selectedLinkshellId))
        {
            return Forbid();
        }

        var selectedLinkshell = accessibleMemberships.First(link => link.LinkshellId == selectedLinkshellId);
        var linkshellMembers = await _dbContext.AppUserLinkshells
            .Where(link => link.LinkshellId == selectedLinkshellId && link.AppUserId != null)
            .OrderBy(link => link.CharacterName)
            .ToListAsync(cancellationToken);

        var memberDtos = linkshellMembers
            .Where(link => !string.IsNullOrWhiteSpace(link.AppUserId))
            .Select(link => new ActivityDkpHistoryMemberDto(
                link.AppUserId!,
                link.CharacterName ?? "Unknown member",
                link.LinkshellDkp ?? 0))
            .ToList();

        if (memberDtos.Count == 0)
        {
            return Ok(new ActivityDkpHistoryDto(
                selectedLinkshellId,
                selectedLinkshell.Linkshell?.LinkshellName ?? "Unknown linkshell",
                null,
                null,
                0,
                Array.Empty<ActivityDkpHistoryMemberDto>(),
                Array.Empty<ActivityDkpLedgerEntryDto>()));
        }

        var selectedAppUserId = string.IsNullOrWhiteSpace(appUserId) || memberDtos.All(member => member.AppUserId != appUserId)
            ? memberDtos.FirstOrDefault(member => member.AppUserId == appUser.Id)?.AppUserId ?? memberDtos.First().AppUserId
            : appUserId;

        var ledgerEntries = await _dbContext.DkpLedgerEntries
            .Where(entry => entry.LinkshellId == selectedLinkshellId && entry.AppUserId == selectedAppUserId)
            .OrderBy(entry => entry.OccurredAt)
            .ThenBy(entry => entry.Sequence)
            .ToListAsync(cancellationToken);

        var runningBalance = 0d;
        return Ok(new ActivityDkpHistoryDto(
            selectedLinkshellId,
            selectedLinkshell.Linkshell?.LinkshellName ?? "Unknown linkshell",
            selectedAppUserId,
            memberDtos.First(member => member.AppUserId == selectedAppUserId).CharacterName,
            memberDtos.First(member => member.AppUserId == selectedAppUserId).CurrentBalance,
            memberDtos,
            ledgerEntries.Select(entry =>
            {
                runningBalance += entry.Amount;
                return new ActivityDkpLedgerEntryDto(
                    entry.Id,
                    entry.EntryType,
                    entry.Amount,
                    runningBalance,
                    entry.OccurredAt,
                    entry.EventName,
                    entry.EventType,
                    entry.EventLocation,
                    entry.EventStartTime,
                    entry.EventEndTime,
                    entry.ItemName,
                    entry.Details);
            }).ToList()));
    }

    [HttpPost("dkp-audit")]
    public async Task<IActionResult> CreateDkpAuditAsync(
        [FromBody] ActivityDkpAuditRequest request,
        CancellationToken cancellationToken)
    {
        var appUser = await ResolveAppUserAsync(cancellationToken);
        if (appUser is null)
        {
            return Unauthorized(new
            {
                error = "Sign in with ASP.NET Identity or provide a Discord bearer token to adjust DKP."
            });
        }

        if (request.LinkshellId <= 0 || string.IsNullOrWhiteSpace(request.TargetAppUserId))
        {
            return BadRequest(new { error = "Linkshell and target member are required." });
        }

        var mode = request.Mode?.Trim();
        if (!string.Equals(mode, "Adjust", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(mode, "Misc", StringComparison.OrdinalIgnoreCase))
        {
            return BadRequest(new { error = "Audit mode must be 'Adjust' or 'Misc'." });
        }

        if (string.IsNullOrWhiteSpace(request.Reason))
        {
            return BadRequest(new { error = "A reason for the audit is required." });
        }

        var membership = await GetMembershipAsync(appUser.Id, request.LinkshellId, cancellationToken);
        if (!await CanAsync(membership, r => r.CanAuditDkp, cancellationToken))
        {
            return Forbid();
        }

        var targetMembership = await _dbContext.AppUserLinkshells
            .FirstOrDefaultAsync(link =>
                link.LinkshellId == request.LinkshellId &&
                link.AppUserId == request.TargetAppUserId,
                cancellationToken);
        if (targetMembership is null)
        {
            return NotFound(new { error = "The selected member is not in this linkshell." });
        }

        var nowUtc = DateTime.UtcNow;
        var officerName = appUser.CharacterName ?? appUser.UserName ?? "Officer";
        var reason = request.Reason.Trim();

        var nextSequence = await _dbContext.DkpLedgerEntries
            .Where(entry => entry.LinkshellId == request.LinkshellId && entry.AppUserId == request.TargetAppUserId)
            .Select(entry => (int?)entry.Sequence)
            .MaxAsync(cancellationToken);
        var sequence = (nextSequence ?? 0) + 1;

        DkpLedgerEntry newEntry;
        double deltaAmount;

        if (string.Equals(mode, "Adjust", StringComparison.OrdinalIgnoreCase))
        {
            if (!request.RelatedLedgerEntryId.HasValue)
            {
                return BadRequest(new { error = "A related ledger entry is required when adjusting a previous entry." });
            }

            var original = await _dbContext.DkpLedgerEntries.FirstOrDefaultAsync(
                entry => entry.Id == request.RelatedLedgerEntryId.Value &&
                         entry.LinkshellId == request.LinkshellId &&
                         entry.AppUserId == request.TargetAppUserId,
                cancellationToken);
            if (original is null)
            {
                return NotFound(new { error = "The selected original ledger entry was not found for this member." });
            }

            deltaAmount = request.Amount - original.Amount;
            newEntry = new DkpLedgerEntry
            {
                AppUserId = request.TargetAppUserId,
                LinkshellId = request.LinkshellId,
                EntryType = "AuditAdjustment",
                Amount = deltaAmount,
                Sequence = sequence,
                OccurredAt = nowUtc,
                CharacterName = targetMembership.CharacterName,
                EventName = original.EventName,
                EventType = original.EventType,
                EventLocation = original.EventLocation,
                EventStartTime = original.EventStartTime,
                EventEndTime = original.EventEndTime,
                ItemName = original.ItemName,
                Details = $"Audit adjustment by {officerName}: {reason} (entry #{original.Id} was {original.Amount:+0.##;-0.##;0}, corrected to {request.Amount:+0.##;-0.##;0})."
            };
        }
        else
        {
            deltaAmount = request.Amount;
            newEntry = new DkpLedgerEntry
            {
                AppUserId = request.TargetAppUserId,
                LinkshellId = request.LinkshellId,
                EntryType = "AuditMisc",
                Amount = deltaAmount,
                Sequence = sequence,
                OccurredAt = nowUtc,
                CharacterName = targetMembership.CharacterName,
                Details = $"Audit by {officerName}: {reason}"
            };
        }

        targetMembership.LinkshellDkp = (targetMembership.LinkshellDkp ?? 0) + deltaAmount;
        _dbContext.DkpLedgerEntries.Add(newEntry);

        await _dbContext.SaveChangesAsync(cancellationToken);
        return Ok(new { success = true });
    }

    [HttpPost("profile")]
    public async Task<IActionResult> UpdateProfileAsync(
        [FromBody] ActivityUpdateProfileRequest request,
        CancellationToken cancellationToken)
    {
        var appUser = await ResolveAppUserAsync(cancellationToken);
        if (appUser is null)
        {
            return Unauthorized(new
            {
                error = "Sign in with ASP.NET Identity or provide a Discord bearer token to update the activity profile."
            });
        }

        if (string.IsNullOrWhiteSpace(request.CharacterName))
        {
            return BadRequest(new { error = "Character name is required." });
        }

        var normalizedTimeZone = string.IsNullOrWhiteSpace(request.TimeZone) ? null : request.TimeZone.Trim();
        if (normalizedTimeZone is not null && !_dateTimeZoneProvider.Ids.Contains(normalizedTimeZone))
        {
            return BadRequest(new { error = "Use a valid IANA time zone such as America/New_York." });
        }

        var result = await _appUserProfileService.UpdateProfileAsync(
            appUser,
            request.CharacterName,
            normalizedTimeZone,
            profileImage: null,
            cancellationToken);

        if (!result.Succeeded)
        {
            var errorMessage = result.Errors.FirstOrDefault()?.Description ?? "Updating the activity profile failed.";
            return BadRequest(new { error = errorMessage });
        }

        return Ok(new { success = true });
    }

    [HttpGet("linkshells/{linkshellId:int}/roles")]
    public async Task<IActionResult> GetLinkshellRolesAsync(int linkshellId, CancellationToken cancellationToken)
    {
        var appUser = await ResolveAppUserAsync(cancellationToken);
        if (appUser is null)
        {
            return Unauthorized(new { error = "Sign in to load linkshell roles." });
        }

        var membership = await GetMembershipAsync(appUser.Id, linkshellId, cancellationToken);
        if (membership is null)
        {
            return Forbid();
        }

        var roles = await EnsureDefaultRolesAsync(linkshellId, cancellationToken);
        var dtoRoles = roles.Select(MapLinkshellRoleDto).ToList();
        return Ok(new ActivityLinkshellRolesResponse(linkshellId, dtoRoles));
    }

    [HttpPost("linkshells/{linkshellId:int}/roles")]
    public async Task<IActionResult> CreateLinkshellRoleAsync(
        int linkshellId,
        [FromBody] ActivityLinkshellRolePermissions request,
        CancellationToken cancellationToken)
    {
        var appUser = await ResolveAppUserAsync(cancellationToken);
        if (appUser is null)
        {
            return Unauthorized(new { error = "Sign in to create a linkshell role." });
        }

        var membership = await GetMembershipAsync(appUser.Id, linkshellId, cancellationToken);
        if (!await CanAsync(membership, r => r.CanManageRoles, cancellationToken))
        {
            return Forbid();
        }

        var name = request.Name?.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            return BadRequest(new { error = "A role name is required." });
        }
        if (name.Length > 64)
        {
            return BadRequest(new { error = "Role name must be 64 characters or fewer." });
        }

        await EnsureDefaultRolesAsync(linkshellId, cancellationToken);

        var existing = await _dbContext.LinkshellRoles
            .FirstOrDefaultAsync(r => r.LinkshellId == linkshellId && r.Name == name, cancellationToken);
        if (existing is not null)
        {
            return BadRequest(new { error = "A role with that name already exists." });
        }

        var maxSort = await _dbContext.LinkshellRoles
            .Where(r => r.LinkshellId == linkshellId)
            .MaxAsync(r => (int?)r.SortOrder, cancellationToken) ?? 0;

        var role = new LinkshellRole
        {
            LinkshellId = linkshellId,
            Name = name,
            IsSystem = false,
            SortOrder = maxSort + 1
        };
        ApplyPermissions(role, request);
        _dbContext.LinkshellRoles.Add(role);
        await _dbContext.SaveChangesAsync(cancellationToken);

        return Ok(MapLinkshellRoleDto(role));
    }

    [HttpPost("linkshells/{linkshellId:int}/roles/{roleId:int}/update")]
    public async Task<IActionResult> UpdateLinkshellRoleAsync(
        int linkshellId,
        int roleId,
        [FromBody] ActivityLinkshellRolePermissions request,
        CancellationToken cancellationToken)
    {
        var appUser = await ResolveAppUserAsync(cancellationToken);
        if (appUser is null)
        {
            return Unauthorized(new { error = "Sign in to update a linkshell role." });
        }

        var membership = await GetMembershipAsync(appUser.Id, linkshellId, cancellationToken);
        if (!await CanAsync(membership, r => r.CanManageRoles, cancellationToken))
        {
            return Forbid();
        }

        var role = await _dbContext.LinkshellRoles
            .FirstOrDefaultAsync(r => r.Id == roleId && r.LinkshellId == linkshellId, cancellationToken);
        if (role is null)
        {
            return NotFound(new { error = "The role was not found." });
        }

        if (!role.IsSystem)
        {
            var name = request.Name?.Trim();
            if (!string.IsNullOrWhiteSpace(name))
            {
                if (name.Length > 64)
                {
                    return BadRequest(new { error = "Role name must be 64 characters or fewer." });
                }

                if (!string.Equals(role.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    var clash = await _dbContext.LinkshellRoles.AnyAsync(r =>
                        r.LinkshellId == linkshellId && r.Id != roleId && r.Name == name, cancellationToken);
                    if (clash)
                    {
                        return BadRequest(new { error = "Another role with that name already exists." });
                    }

                    var previousName = role.Name;
                    role.Name = name;
                    await _dbContext.AppUserLinkshells
                        .Where(link => link.LinkshellId == linkshellId && link.Rank == previousName)
                        .ExecuteUpdateAsync(setters => setters.SetProperty(link => link.Rank, name), cancellationToken);
                }
            }
        }

        if (role.Name.Equals("Leader", StringComparison.OrdinalIgnoreCase))
        {
            // Safety: a Leader must always retain CanManageRoles so there is a way back.
            request = request with { CanManageRoles = true };
        }

        ApplyPermissions(role, request);
        await _dbContext.SaveChangesAsync(cancellationToken);

        return Ok(MapLinkshellRoleDto(role));
    }

    [HttpPost("linkshells/{linkshellId:int}/roles/{roleId:int}/delete")]
    public async Task<IActionResult> DeleteLinkshellRoleAsync(
        int linkshellId,
        int roleId,
        CancellationToken cancellationToken)
    {
        var appUser = await ResolveAppUserAsync(cancellationToken);
        if (appUser is null)
        {
            return Unauthorized(new { error = "Sign in to delete a linkshell role." });
        }

        var membership = await GetMembershipAsync(appUser.Id, linkshellId, cancellationToken);
        if (!await CanAsync(membership, r => r.CanManageRoles, cancellationToken))
        {
            return Forbid();
        }

        var role = await _dbContext.LinkshellRoles
            .FirstOrDefaultAsync(r => r.Id == roleId && r.LinkshellId == linkshellId, cancellationToken);
        if (role is null)
        {
            return NotFound(new { error = "The role was not found." });
        }

        if (role.IsSystem)
        {
            return BadRequest(new { error = "System roles cannot be deleted." });
        }

        var inUse = await _dbContext.AppUserLinkshells
            .AnyAsync(link => link.LinkshellId == linkshellId && link.Rank == role.Name, cancellationToken);
        if (inUse)
        {
            return BadRequest(new { error = "Members still have this role. Reassign them first." });
        }

        _dbContext.LinkshellRoles.Remove(role);
        await _dbContext.SaveChangesAsync(cancellationToken);

        return Ok(new { success = true });
    }

    private static void ApplyPermissions(LinkshellRole role, ActivityLinkshellRolePermissions permissions)
    {
        role.CanManageRoles = permissions.CanManageRoles;
        role.CanManageMembers = permissions.CanManageMembers;
        role.CanManageEvents = permissions.CanManageEvents;
        role.CanModerateLiveEvent = permissions.CanModerateLiveEvent;
        role.CanAddLoot = permissions.CanAddLoot;
        role.CanManageInventory = permissions.CanManageInventory;
        role.CanManageTreasury = permissions.CanManageTreasury;
        role.CanManageRules = permissions.CanManageRules;
        role.CanManageAnnouncements = permissions.CanManageAnnouncements;
        role.CanManageTods = permissions.CanManageTods;
        role.CanAuditDkp = permissions.CanAuditDkp;
        role.CanManageAuctions = permissions.CanManageAuctions;
        role.CanCustomizeLinkshell = permissions.CanCustomizeLinkshell;
    }

    private static bool IsValidLootStructure(string? structure)
    {
        return string.Equals(structure, "Dkp", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(structure, "LootCouncil", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(structure, "Hybrid", StringComparison.OrdinalIgnoreCase);
    }

    internal static string NormalizeLootStructure(string structure)
    {
        if (string.Equals(structure, "LootCouncil", StringComparison.OrdinalIgnoreCase)) return "LootCouncil";
        if (string.Equals(structure, "Hybrid", StringComparison.OrdinalIgnoreCase)) return "Hybrid";
        return "Dkp";
    }

    private static ActivityLinkshellSettingsDto MapLinkshellSettingsDto(Linkshell? linkshell)
    {
        if (linkshell is null)
        {
            return new ActivityLinkshellSettingsDto("Dkp", true, true, true, true, true, true, true, true, true, "Quarter");
        }

        return new ActivityLinkshellSettingsDto(
            string.IsNullOrWhiteSpace(linkshell.LootStructure) ? "Dkp" : linkshell.LootStructure,
            linkshell.EnableHnmSection,
            linkshell.EnableMissions,
            linkshell.EnableAuctions,
            linkshell.EnableToDs,
            linkshell.EnableEndgame,
            linkshell.EnableEvents,
            linkshell.EnableDkp,
            linkshell.EnableItems,
            linkshell.EnableRevenue,
            NormalizeDkpRounding(linkshell.DkpRoundingIncrement));
    }

    private static string NormalizeDkpRounding(string? raw)
    {
        if (string.Equals(raw, "Half", StringComparison.OrdinalIgnoreCase)) return "Half";
        return "Quarter";
    }

    private static ActivityPermissionsDto? ResolvePermissionsFor(
        int linkshellId,
        string? rank,
        Dictionary<int, Dictionary<string, LinkshellRole>> rolesByLinkshellAndName)
    {
        if (!rolesByLinkshellAndName.TryGetValue(linkshellId, out var rolesByName))
        {
            return null;
        }

        var rankName = string.IsNullOrWhiteSpace(rank) ? "Member" : rank.Trim();
        if (!rolesByName.TryGetValue(rankName, out var role))
        {
            if (!rolesByName.TryGetValue("Member", out role))
            {
                return null;
            }
        }

        return new ActivityPermissionsDto(
            role.CanManageRoles,
            role.CanManageMembers,
            role.CanManageEvents,
            role.CanModerateLiveEvent,
            role.CanAddLoot,
            role.CanManageInventory,
            role.CanManageTreasury,
            role.CanManageRules,
            role.CanManageAnnouncements,
            role.CanManageTods,
            role.CanAuditDkp,
            role.CanManageAuctions,
            role.CanCustomizeLinkshell);
    }

    private static ActivityLinkshellRoleDto MapLinkshellRoleDto(LinkshellRole role)
    {
        return new ActivityLinkshellRoleDto(
            role.Id,
            role.Name,
            role.IsSystem,
            role.SortOrder,
            role.CanManageRoles,
            role.CanManageMembers,
            role.CanManageEvents,
            role.CanModerateLiveEvent,
            role.CanAddLoot,
            role.CanManageInventory,
            role.CanManageTreasury,
            role.CanManageRules,
            role.CanManageAnnouncements,
            role.CanManageTods,
            role.CanAuditDkp,
            role.CanManageAuctions,
            role.CanCustomizeLinkshell);
    }

    [HttpPost("linkshells")]
    public async Task<IActionResult> CreateLinkshellAsync(
        [FromBody] ActivityCreateLinkshellRequest request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
        {
            return BadRequest(new { error = "Linkshell name is required." });
        }

        var appUser = await ResolveAppUserAsync(cancellationToken);
        if (appUser is null)
        {
            return Unauthorized(new
            {
                error = "Sign in with ASP.NET Identity or provide a Discord bearer token to create a linkshell."
            });
        }

        var trimmedName = request.Name.Trim();
        var duplicateLinkshell = await _dbContext.Linkshells
            .AnyAsync(
                linkshell => linkshell.AppUserId == appUser.Id && linkshell.LinkshellName == trimmedName,
                cancellationToken);

        if (duplicateLinkshell)
        {
            return BadRequest(new { error = "A linkshell with that name already exists for the current app user." });
        }

        var linkshell = new Linkshell
        {
            AppUserId = appUser.Id,
            LinkshellName = trimmedName,
            Details = request.Details?.Trim(),
            Status = "Active"
        };

        _dbContext.Linkshells.Add(linkshell);
        await _dbContext.SaveChangesAsync(cancellationToken);

        _dbContext.AppUserLinkshells.Add(new AppUserLinkshell
        {
            AppUserId = appUser.Id,
            LinkshellId = linkshell.Id,
            CharacterName = appUser.CharacterName ?? appUser.UserName,
            Rank = "Leader",
            Status = "Active",
            LinkshellDkp = 0,
            DateJoined = DateTime.UtcNow
        });

        appUser.PrimaryLinkshellId ??= linkshell.Id;
        appUser.PrimaryLinkshellName ??= linkshell.LinkshellName;

        await _dbContext.SaveChangesAsync(cancellationToken);
        await _userManager.UpdateAsync(appUser);

        return Ok(new { success = true, linkshellId = linkshell.Id });
    }

    [HttpGet("linkshells/{linkshellId:int}")]
    public async Task<IActionResult> GetLinkshellDetailAsync(int linkshellId, CancellationToken cancellationToken)
    {
        var appUser = await ResolveAppUserAsync(cancellationToken);
        if (appUser is null)
        {
            return Unauthorized(new
            {
                error = "Sign in with ASP.NET Identity or provide a Discord bearer token to load linkshell details."
            });
        }

        var hasAccess = await _dbContext.AppUserLinkshells
            .AnyAsync(link => link.AppUserId == appUser.Id && link.LinkshellId == linkshellId, cancellationToken);

        if (!hasAccess)
        {
            return Forbid();
        }

        var linkshell = await _dbContext.Linkshells
            .Include(item => item.AppUserLinkshells)
            .ThenInclude(link => link.AppUser)
            .FirstOrDefaultAsync(item => item.Id == linkshellId, cancellationToken);

        if (linkshell is null)
        {
            return NotFound(new { error = "The selected linkshell was not found." });
        }

        return Ok(new ActivityLinkshellDetailDto(
            linkshell.Id,
            linkshell.LinkshellName ?? "Unknown linkshell",
            linkshell.AppUserLinkshells.Count,
            linkshell.Details,
            linkshell.Status,
            linkshell.AppUserLinkshells
                .OrderBy(link => link.CharacterName)
                .Select(link => new ActivityMemberDto(
                    link.Id,
                    link.AppUserId,
                    link.CharacterName ?? link.AppUser?.UserName ?? "Unknown member",
                    link.Rank,
                    link.Status,
                    link.LinkshellDkp))
                .ToList()));
    }

    [HttpPost("linkshells/{linkshellId:int}/primary")]
    public async Task<IActionResult> SetPrimaryLinkshellAsync(int linkshellId, CancellationToken cancellationToken)
    {
        var appUser = await ResolveAppUserAsync(cancellationToken);
        if (appUser is null)
        {
            return Unauthorized(new
            {
                error = "Sign in with ASP.NET Identity or provide a Discord bearer token to update the primary linkshell."
            });
        }

        var membership = await _dbContext.AppUserLinkshells
            .Include(link => link.Linkshell)
            .FirstOrDefaultAsync(link => link.AppUserId == appUser.Id && link.LinkshellId == linkshellId, cancellationToken);

        if (membership?.Linkshell is null)
        {
            return NotFound(new { error = "The selected linkshell membership was not found." });
        }

        appUser.PrimaryLinkshellId = membership.LinkshellId;
        appUser.PrimaryLinkshellName = membership.Linkshell.LinkshellName;

        await _userManager.UpdateAsync(appUser);
        await _dbContext.SaveChangesAsync(cancellationToken);

        return Ok(new { success = true });
    }

    [HttpGet("players/search")]
    public async Task<IActionResult> SearchPlayersAsync(
        [FromQuery] string? query,
        [FromQuery] int linkshellId,
        CancellationToken cancellationToken)
    {
        if (linkshellId <= 0)
        {
            return BadRequest(new { error = "A linkshell selection is required." });
        }

        if (string.IsNullOrWhiteSpace(query) || query.Trim().Length < 2)
        {
            return Ok(Array.Empty<ActivityUserSearchResultDto>());
        }

        var appUser = await ResolveAppUserAsync(cancellationToken);
        if (appUser is null)
        {
            return Unauthorized(new
            {
                error = "Sign in with ASP.NET Identity or provide a Discord bearer token to search players."
            });
        }

        var membership = await GetMembershipAsync(appUser.Id, linkshellId, cancellationToken);
        if (!await CanAsync(membership, r => r.CanManageMembers, cancellationToken))
        {
            return Forbid();
        }

        var normalizedQuery = query?.Trim();
        var existingMemberIds = await _dbContext.AppUserLinkshells
            .Where(link => link.LinkshellId == linkshellId && link.AppUserId != null)
            .Select(link => link.AppUserId!)
            .ToListAsync(cancellationToken);

        var pendingInviteIds = await _dbContext.Invites
            .Where(invite =>
                invite.LinkshellId == linkshellId &&
                (invite.Status == PendingInviteStatus || invite.Status == PendingJoinRequestStatus))
            .Select(invite => invite.AppUserId)
            .ToListAsync(cancellationToken);

        var results = await _dbContext.Users
            .Where(user =>
                user.Id != appUser.Id &&
                !existingMemberIds.Contains(user.Id) &&
                !pendingInviteIds.Contains(user.Id) &&
                (
                    (user.CharacterName != null && EF.Functions.ILike(user.CharacterName, $"%{normalizedQuery}%")) ||
                    EF.Functions.ILike(user.UserName!, $"%{normalizedQuery}%")
                ))
            .OrderBy(user => user.CharacterName ?? user.UserName)
            .Take(10)
            .Select(user => new ActivityUserSearchResultDto(
                user.Id,
                user.CharacterName ?? user.UserName ?? "Unknown member",
                user.UserName,
                user.PrimaryLinkshellName))
            .ToListAsync(cancellationToken);

        return Ok(results);
    }

    [HttpPost("invites/participants")]
    public async Task<IActionResult> GetParticipantInviteCandidatesAsync(
        [FromBody] ActivityParticipantInviteCandidatesRequest request,
        CancellationToken cancellationToken)
    {
        if (request.LinkshellId <= 0)
        {
            return BadRequest(new { error = "A linkshell selection is required." });
        }

        var appUser = await ResolveAppUserAsync(cancellationToken);
        if (appUser is null)
        {
            return Unauthorized(new
            {
                error = "Sign in with ASP.NET Identity or provide a Discord bearer token to load connected participant invites."
            });
        }

        var membership = await GetMembershipAsync(appUser.Id, request.LinkshellId, cancellationToken);
        if (!await CanAsync(membership, r => r.CanManageMembers, cancellationToken))
        {
            return Forbid();
        }

        var normalizedDiscordUserIds = (request.DiscordUserIds ?? Array.Empty<string>())
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id.Trim())
            .Distinct(StringComparer.Ordinal)
            .Take(25)
            .ToList();

        if (normalizedDiscordUserIds.Count == 0)
        {
            return Ok(Array.Empty<ActivityParticipantInviteCandidateDto>());
        }

        var existingMemberIds = await _dbContext.AppUserLinkshells
            .Where(link => link.LinkshellId == request.LinkshellId && link.AppUserId != null)
            .Select(link => link.AppUserId!)
            .ToListAsync(cancellationToken);

        var pendingInviteIds = await _dbContext.Invites
            .Where(invite =>
                invite.LinkshellId == request.LinkshellId &&
                (invite.Status == PendingInviteStatus || invite.Status == PendingJoinRequestStatus))
            .Select(invite => invite.AppUserId)
            .ToListAsync(cancellationToken);

        var candidates = await _dbContext.DiscordActivityUsers
            .Include(discordUser => discordUser.IdentityUser)
            .Where(discordUser =>
                normalizedDiscordUserIds.Contains(discordUser.DiscordUserId) &&
                discordUser.IdentityUserId != null &&
                discordUser.IdentityUserId != appUser.Id &&
                !existingMemberIds.Contains(discordUser.IdentityUserId) &&
                !pendingInviteIds.Contains(discordUser.IdentityUserId))
            .OrderBy(discordUser => discordUser.IdentityUser!.CharacterName ?? discordUser.IdentityUser!.UserName)
            .ToListAsync(cancellationToken);

        var results = candidates
            .GroupBy(discordUser => discordUser.IdentityUserId, StringComparer.Ordinal)
            .Select(group => group.First())
            .Select(discordUser => new ActivityParticipantInviteCandidateDto(
                discordUser.IdentityUserId!,
                discordUser.DiscordUserId,
                discordUser.IdentityUser?.CharacterName ??
                discordUser.IdentityUser?.UserName ??
                discordUser.GlobalName ??
                discordUser.Username,
                discordUser.IdentityUser?.UserName,
                discordUser.IdentityUser?.PrimaryLinkshellName))
            .ToList();

        return Ok(results);
    }

    [HttpPost("linkshells/{linkshellId:int}/invites")]
    public async Task<IActionResult> SendInviteAsync(
        int linkshellId,
        [FromBody] ActivitySendInviteRequest request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.AppUserId))
        {
            return BadRequest(new { error = "A target app user is required." });
        }

        var appUser = await ResolveAppUserAsync(cancellationToken);
        if (appUser is null)
        {
            return Unauthorized(new
            {
                error = "Sign in with ASP.NET Identity or provide a Discord bearer token to send invites."
            });
        }

        var membership = await GetMembershipAsync(appUser.Id, linkshellId, cancellationToken);
        if (!await CanAsync(membership, r => r.CanManageMembers, cancellationToken))
        {
            return Forbid();
        }

        var targetUser = await _dbContext.Users.FindAsync(new object?[] { request.AppUserId }, cancellationToken);
        if (targetUser is null)
        {
            return NotFound(new { error = "The selected player was not found." });
        }

        var existingMembership = await _dbContext.AppUserLinkshells
            .AnyAsync(link => link.LinkshellId == linkshellId && link.AppUserId == request.AppUserId, cancellationToken);

        if (existingMembership)
        {
            return BadRequest(new { error = "That player is already a member of the selected linkshell." });
        }

        var existingInvite = await _dbContext.Invites
            .AnyAsync(
                invite => invite.LinkshellId == linkshellId &&
                          invite.AppUserId == request.AppUserId &&
                          (invite.Status == PendingInviteStatus || invite.Status == PendingJoinRequestStatus),
                cancellationToken);

        if (existingInvite)
        {
            return BadRequest(new { error = "A pending invite or join request already exists for that player." });
        }

        _dbContext.Invites.Add(new Invite
        {
            AppUserId = request.AppUserId,
            LinkshellId = linkshellId,
            Status = PendingInviteStatus
        });

        await _dbContext.SaveChangesAsync(cancellationToken);
        return Ok(new { success = true });
    }

    [HttpGet("linkshells/search")]
    public async Task<IActionResult> SearchLinkshellsAsync(
        [FromQuery] string? query,
        CancellationToken cancellationToken)
    {
        var appUser = await ResolveAppUserAsync(cancellationToken);
        if (appUser is null)
        {
            return Unauthorized(new
            {
                error = "Sign in with ASP.NET Identity or provide a Discord bearer token to search linkshells."
            });
        }

        var normalizedQuery = query?.Trim();
        var existingMembershipIds = await _dbContext.AppUserLinkshells
            .Where(link => link.AppUserId == appUser.Id)
            .Select(link => link.LinkshellId)
            .ToListAsync(cancellationToken);

        var pendingRequestIds = await _dbContext.Invites
            .Where(invite => invite.AppUserId == appUser.Id && invite.Status == PendingJoinRequestStatus)
            .Select(invite => invite.LinkshellId)
            .ToListAsync(cancellationToken);

        var memberCounts = await _dbContext.AppUserLinkshells
            .GroupBy(link => link.LinkshellId)
            .Select(group => new { LinkshellId = group.Key, Count = group.Count() })
            .ToDictionaryAsync(item => item.LinkshellId, item => item.Count, cancellationToken);

        var results = await _dbContext.Linkshells
            .Where(linkshell =>
                linkshell.Status == "Active" &&
                !existingMembershipIds.Contains(linkshell.Id) &&
                !pendingRequestIds.Contains(linkshell.Id) &&
                linkshell.LinkshellName != null &&
                (string.IsNullOrWhiteSpace(normalizedQuery) ||
                 EF.Functions.ILike(linkshell.LinkshellName, $"%{normalizedQuery}%")))
            .OrderBy(linkshell => linkshell.LinkshellName)
            .Take(10)
            .ToListAsync(cancellationToken);

        return Ok(results.Select(linkshell => new ActivityLinkshellSearchResultDto(
            linkshell.Id,
            linkshell.LinkshellName ?? "Unknown linkshell",
            linkshell.Details,
            memberCounts.GetValueOrDefault(linkshell.Id, 0),
            linkshell.Status)).ToList());
    }

    [HttpPost("linkshells/{linkshellId:int}/join-request")]
    public async Task<IActionResult> RequestJoinLinkshellAsync(int linkshellId, CancellationToken cancellationToken)
    {
        var appUser = await ResolveAppUserAsync(cancellationToken);
        if (appUser is null)
        {
            return Unauthorized(new
            {
                error = "Sign in with ASP.NET Identity or provide a Discord bearer token to request linkshell access."
            });
        }

        var linkshell = await _dbContext.Linkshells
            .FirstOrDefaultAsync(item => item.Id == linkshellId && item.Status == "Active", cancellationToken);

        if (linkshell is null)
        {
            return NotFound(new { error = "The selected linkshell was not found." });
        }

        var existingMembership = await _dbContext.AppUserLinkshells
            .AnyAsync(link => link.LinkshellId == linkshellId && link.AppUserId == appUser.Id, cancellationToken);

        if (existingMembership)
        {
            return BadRequest(new { error = "You already belong to that linkshell." });
        }

        var existingRequest = await _dbContext.Invites
            .AnyAsync(invite =>
                invite.LinkshellId == linkshellId &&
                invite.AppUserId == appUser.Id &&
                invite.Status == PendingJoinRequestStatus,
                cancellationToken);

        if (existingRequest)
        {
            return BadRequest(new { error = "A join request is already pending for that linkshell." });
        }

        _dbContext.Invites.Add(new Invite
        {
            AppUserId = appUser.Id,
            LinkshellId = linkshellId,
            Status = PendingJoinRequestStatus
        });

        await _dbContext.SaveChangesAsync(cancellationToken);
        return Ok(new { success = true });
    }

    [HttpPost("invites/{inviteId:int}/revoke")]
    public async Task<IActionResult> RevokeInviteAsync(int inviteId, CancellationToken cancellationToken)
    {
        var appUser = await ResolveAppUserAsync(cancellationToken);
        if (appUser is null)
        {
            return Unauthorized(new
            {
                error = "Sign in with ASP.NET Identity or provide a Discord bearer token to revoke invites."
            });
        }

        var invite = await _dbContext.Invites
            .Include(item => item.Linkshell)
            .FirstOrDefaultAsync(item => item.Id == inviteId && item.Status == PendingInviteStatus, cancellationToken);

        if (invite is null)
        {
            return NotFound(new { error = "The selected invite was not found." });
        }

        var membership = await GetMembershipAsync(appUser.Id, invite.LinkshellId, cancellationToken);
        if (!await CanAsync(membership, r => r.CanManageMembers, cancellationToken))
        {
            return Forbid();
        }

        _dbContext.Invites.Remove(invite);
        await _dbContext.SaveChangesAsync(cancellationToken);

        return Ok(new { success = true });
    }

    [HttpPost("invites/{inviteId:int}/accept")]
    public async Task<IActionResult> AcceptInviteAsync(int inviteId, CancellationToken cancellationToken)
    {
        var appUser = await ResolveAppUserAsync(cancellationToken);
        if (appUser is null)
        {
            return Unauthorized(new
            {
                error = "Sign in with ASP.NET Identity or provide a Discord bearer token to accept invites."
            });
        }

        var invite = await _dbContext.Invites
            .Include(item => item.Linkshell)
            .FirstOrDefaultAsync(item => item.Id == inviteId && item.AppUserId == appUser.Id && item.Status == PendingInviteStatus, cancellationToken);

        if (invite is null)
        {
            return NotFound(new { error = "The selected invite was not found." });
        }

        var existingMembership = await _dbContext.AppUserLinkshells
            .AnyAsync(link => link.LinkshellId == invite.LinkshellId && link.AppUserId == appUser.Id, cancellationToken);

        if (!existingMembership)
        {
            _dbContext.AppUserLinkshells.Add(new AppUserLinkshell
            {
                AppUserId = appUser.Id,
                LinkshellId = invite.LinkshellId,
                LinkshellDkp = 0,
                DateJoined = DateTime.UtcNow,
                CharacterName = appUser.CharacterName ?? appUser.UserName,
                Rank = "Member",
                Status = "Active"
            });
        }

        appUser.PrimaryLinkshellId ??= invite.LinkshellId;
        appUser.PrimaryLinkshellName ??= invite.Linkshell?.LinkshellName;

        _dbContext.Invites.Remove(invite);
        await _dbContext.SaveChangesAsync(cancellationToken);
        await _userManager.UpdateAsync(appUser);

        return Ok(new { success = true });
    }

    [HttpPost("invites/{inviteId:int}/decline")]
    public async Task<IActionResult> DeclineInviteAsync(int inviteId, CancellationToken cancellationToken)
    {
        var appUser = await ResolveAppUserAsync(cancellationToken);
        if (appUser is null)
        {
            return Unauthorized(new
            {
                error = "Sign in with ASP.NET Identity or provide a Discord bearer token to decline invites."
            });
        }

        var invite = await _dbContext.Invites
            .FirstOrDefaultAsync(item => item.Id == inviteId && item.AppUserId == appUser.Id && item.Status == PendingInviteStatus, cancellationToken);

        if (invite is null)
        {
            return NotFound(new { error = "The selected invite was not found." });
        }

        _dbContext.Invites.Remove(invite);
        await _dbContext.SaveChangesAsync(cancellationToken);
        return Ok(new { success = true });
    }

    [HttpPost("join-requests/{inviteId:int}/approve")]
    public async Task<IActionResult> ApproveJoinRequestAsync(int inviteId, CancellationToken cancellationToken)
    {
        var appUser = await ResolveAppUserAsync(cancellationToken);
        if (appUser is null)
        {
            return Unauthorized(new
            {
                error = "Sign in with ASP.NET Identity or provide a Discord bearer token to approve join requests."
            });
        }

        var invite = await _dbContext.Invites
            .Include(item => item.Linkshell)
            .Include(item => item.AppUser)
            .FirstOrDefaultAsync(item => item.Id == inviteId && item.Status == PendingJoinRequestStatus, cancellationToken);

        if (invite is null)
        {
            return NotFound(new { error = "The selected join request was not found." });
        }

        var membership = await GetMembershipAsync(appUser.Id, invite.LinkshellId, cancellationToken);
        if (!await CanAsync(membership, r => r.CanManageMembers, cancellationToken))
        {
            return Forbid();
        }

        var existingMembership = await _dbContext.AppUserLinkshells
            .AnyAsync(link => link.LinkshellId == invite.LinkshellId && link.AppUserId == invite.AppUserId, cancellationToken);

        if (!existingMembership)
        {
            _dbContext.AppUserLinkshells.Add(new AppUserLinkshell
            {
                AppUserId = invite.AppUserId,
                LinkshellId = invite.LinkshellId,
                LinkshellDkp = 0,
                DateJoined = DateTime.UtcNow,
                CharacterName = invite.AppUser?.CharacterName ?? invite.AppUser?.UserName,
                Rank = "Member",
                Status = "Active"
            });
        }

        if (invite.AppUser is not null)
        {
            invite.AppUser.PrimaryLinkshellId ??= invite.LinkshellId;
            invite.AppUser.PrimaryLinkshellName ??= invite.Linkshell?.LinkshellName;
            await _userManager.UpdateAsync(invite.AppUser);
        }

        _dbContext.Invites.Remove(invite);
        await _dbContext.SaveChangesAsync(cancellationToken);
        return Ok(new { success = true });
    }

    [HttpPost("join-requests/{inviteId:int}/decline")]
    public async Task<IActionResult> DeclineJoinRequestAsync(int inviteId, CancellationToken cancellationToken)
    {
        var appUser = await ResolveAppUserAsync(cancellationToken);
        if (appUser is null)
        {
            return Unauthorized(new
            {
                error = "Sign in with ASP.NET Identity or provide a Discord bearer token to decline join requests."
            });
        }

        var invite = await _dbContext.Invites
            .FirstOrDefaultAsync(item => item.Id == inviteId && item.Status == PendingJoinRequestStatus, cancellationToken);

        if (invite is null)
        {
            return NotFound(new { error = "The selected join request was not found." });
        }

        var membership = await GetMembershipAsync(appUser.Id, invite.LinkshellId, cancellationToken);
        if (!await CanAsync(membership, r => r.CanManageMembers, cancellationToken))
        {
            return Forbid();
        }

        _dbContext.Invites.Remove(invite);
        await _dbContext.SaveChangesAsync(cancellationToken);
        return Ok(new { success = true });
    }

    [HttpPost("linkshells/{linkshellId:int}/members/{memberId:int}/remove")]
    public async Task<IActionResult> RemoveMemberAsync(int linkshellId, int memberId, CancellationToken cancellationToken)
    {
        var appUser = await ResolveAppUserAsync(cancellationToken);
        if (appUser is null)
        {
            return Unauthorized(new
            {
                error = "Sign in with ASP.NET Identity or provide a Discord bearer token to remove members."
            });
        }

        var currentMembership = await GetMembershipAsync(appUser.Id, linkshellId, cancellationToken);
        if (!await CanAsync(currentMembership, r => r.CanManageMembers, cancellationToken))
        {
            return Forbid();
        }

        var targetMembership = await _dbContext.AppUserLinkshells
            .FirstOrDefaultAsync(link => link.Id == memberId && link.LinkshellId == linkshellId, cancellationToken);

        if (targetMembership is null)
        {
            return NotFound(new { error = "The selected member was not found." });
        }

        if (string.Equals(targetMembership.AppUserId, appUser.Id, StringComparison.Ordinal))
        {
            return BadRequest(new { error = "Use the website membership tools to leave your own primary linkshell." });
        }

        _dbContext.AppUserLinkshells.Remove(targetMembership);

        if (!string.IsNullOrWhiteSpace(targetMembership.AppUserId))
        {
            var targetUser = await _dbContext.Users.FindAsync(new object?[] { targetMembership.AppUserId }, cancellationToken);
            if (targetUser is not null && targetUser.PrimaryLinkshellId == linkshellId)
            {
                var fallbackMembership = await _dbContext.AppUserLinkshells
                    .Include(link => link.Linkshell)
                    .Where(link => link.AppUserId == targetUser.Id && link.LinkshellId != linkshellId)
                    .OrderBy(link => link.Linkshell!.LinkshellName)
                    .FirstOrDefaultAsync(cancellationToken);

                targetUser.PrimaryLinkshellId = fallbackMembership?.LinkshellId;
                targetUser.PrimaryLinkshellName = fallbackMembership?.Linkshell?.LinkshellName;
            }

            var pendingInvites = await _dbContext.Invites
                .Where(invite => invite.LinkshellId == linkshellId && invite.AppUserId == targetMembership.AppUserId)
                .ToListAsync(cancellationToken);

            if (pendingInvites.Count > 0)
            {
                _dbContext.Invites.RemoveRange(pendingInvites);
            }
        }

        await _dbContext.SaveChangesAsync(cancellationToken);
        return Ok(new { success = true });
    }

    [HttpPost("linkshells/{linkshellId:int}/members/{memberId:int}/role")]
    public async Task<IActionResult> UpdateMemberRoleAsync(
        int linkshellId,
        int memberId,
        [FromBody] ActivityUpdateMemberRoleRequest request,
        CancellationToken cancellationToken)
    {
        var appUser = await ResolveAppUserAsync(cancellationToken);
        if (appUser is null)
        {
            return Unauthorized(new
            {
                error = "Sign in with ASP.NET Identity or provide a Discord bearer token to update member roles."
            });
        }

        var currentMembership = await GetMembershipAsync(appUser.Id, linkshellId, cancellationToken);
        if (!await CanAsync(currentMembership, r => r.CanManageRoles, cancellationToken))
        {
            return Forbid();
        }

        var targetMembership = await _dbContext.AppUserLinkshells
            .FirstOrDefaultAsync(link => link.Id == memberId && link.LinkshellId == linkshellId, cancellationToken);

        if (targetMembership is null)
        {
            return NotFound(new { error = "The selected member was not found." });
        }

        var normalizedRole = request.Role?.Trim();
        if (string.IsNullOrWhiteSpace(normalizedRole))
        {
            return BadRequest(new { error = "A role name is required." });
        }

        await EnsureDefaultRolesAsync(linkshellId, cancellationToken);
        var roleRow = await _dbContext.LinkshellRoles
            .FirstOrDefaultAsync(r => r.LinkshellId == linkshellId && r.Name == normalizedRole, cancellationToken);
        if (roleRow is null)
        {
            return BadRequest(new { error = "That role does not exist for this linkshell." });
        }
        normalizedRole = roleRow.Name;

        if (string.Equals(normalizedRole, "Leader", StringComparison.OrdinalIgnoreCase))
        {
            if (string.Equals(targetMembership.AppUserId, appUser.Id, StringComparison.Ordinal))
            {
                return BadRequest(new { error = "You are already the leader of this linkshell." });
            }

            if (string.Equals(currentMembership!.Rank, "Leader", StringComparison.OrdinalIgnoreCase))
            {
                currentMembership.Rank = "Officer";
            }
            targetMembership.Rank = "Leader";
        }
        else
        {
            if (string.Equals(targetMembership.AppUserId, appUser.Id, StringComparison.Ordinal))
            {
                return BadRequest(new { error = "Leaders cannot change their own role without transferring leadership." });
            }

            targetMembership.Rank = normalizedRole;
        }

        await _dbContext.SaveChangesAsync(cancellationToken);
        return Ok(new { success = true });
    }

    [HttpPost("linkshells/{linkshellId:int}/update")]
    public async Task<IActionResult> UpdateLinkshellAsync(
        int linkshellId,
        [FromBody] ActivityUpdateLinkshellRequest request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
        {
            return BadRequest(new { error = "Linkshell name is required." });
        }

        var appUser = await ResolveAppUserAsync(cancellationToken);
        if (appUser is null)
        {
            return Unauthorized(new
            {
                error = "Sign in with ASP.NET Identity or provide a Discord bearer token to update the linkshell."
            });
        }

        var membership = await GetMembershipAsync(appUser.Id, linkshellId, cancellationToken);
        if (!await CanAsync(membership, r => r.CanCustomizeLinkshell, cancellationToken))
        {
            return Forbid();
        }

        var linkshell = await _dbContext.Linkshells.FirstOrDefaultAsync(item => item.Id == linkshellId, cancellationToken);
        if (linkshell is null)
        {
            return NotFound(new { error = "The selected linkshell was not found." });
        }

        var trimmedName = request.Name.Trim();
        var duplicate = await _dbContext.Linkshells
            .AnyAsync(
                item => item.Id != linkshellId &&
                        item.AppUserId == linkshell.AppUserId &&
                        item.LinkshellName == trimmedName,
                cancellationToken);

        if (duplicate)
        {
            return BadRequest(new { error = "Another linkshell with that name already exists." });
        }

        linkshell.LinkshellName = trimmedName;
        linkshell.Details = request.Details?.Trim();

        if (!string.IsNullOrWhiteSpace(request.LootStructure))
        {
            var requestedStructure = request.LootStructure.Trim();
            if (!IsValidLootStructure(requestedStructure))
            {
                return BadRequest(new { error = "Loot structure must be Dkp, LootCouncil, or Hybrid." });
            }
            linkshell.LootStructure = NormalizeLootStructure(requestedStructure);
        }

        if (request.EnableHnmSection.HasValue) linkshell.EnableHnmSection = request.EnableHnmSection.Value;
        if (request.EnableMissions.HasValue) linkshell.EnableMissions = request.EnableMissions.Value;
        if (request.EnableAuctions.HasValue) linkshell.EnableAuctions = request.EnableAuctions.Value;
        if (request.EnableToDs.HasValue) linkshell.EnableToDs = request.EnableToDs.Value;
        if (request.EnableEndgame.HasValue) linkshell.EnableEndgame = request.EnableEndgame.Value;
        if (request.EnableEvents.HasValue) linkshell.EnableEvents = request.EnableEvents.Value;
        if (request.EnableDkp.HasValue) linkshell.EnableDkp = request.EnableDkp.Value;
        if (request.EnableItems.HasValue) linkshell.EnableItems = request.EnableItems.Value;
        if (request.EnableRevenue.HasValue) linkshell.EnableRevenue = request.EnableRevenue.Value;
        if (!string.IsNullOrWhiteSpace(request.DkpRoundingIncrement))
        {
            linkshell.DkpRoundingIncrement = NormalizeDkpRounding(request.DkpRoundingIncrement);
        }

        var memberships = await _dbContext.AppUserLinkshells
            .Where(link => link.LinkshellId == linkshellId)
            .ToListAsync(cancellationToken);

        var memberIds = memberships
            .Where(link => !string.IsNullOrWhiteSpace(link.AppUserId))
            .Select(link => link.AppUserId!)
            .Distinct()
            .ToList();

        if (memberIds.Count > 0)
        {
            var users = await _dbContext.Users.Where(user => memberIds.Contains(user.Id)).ToListAsync(cancellationToken);
            foreach (var user in users.Where(user => user.PrimaryLinkshellId == linkshellId))
            {
                user.PrimaryLinkshellName = trimmedName;
            }
        }

        await _dbContext.SaveChangesAsync(cancellationToken);
        return Ok(new { success = true });
    }

    [HttpPost("linkshells/{linkshellId:int}/delete")]
    public async Task<IActionResult> DeleteLinkshellAsync(int linkshellId, CancellationToken cancellationToken)
    {
        var appUser = await ResolveAppUserAsync(cancellationToken);
        if (appUser is null)
        {
            return Unauthorized(new
            {
                error = "Sign in with ASP.NET Identity or provide a Discord bearer token to delete the linkshell."
            });
        }

        var membership = await GetMembershipAsync(appUser.Id, linkshellId, cancellationToken);
        if (!IsLeader(membership))
        {
            return Forbid();
        }

        var linkshell = await _dbContext.Linkshells
            .Include(ls => ls.AppUserLinkshells)
            .Include(ls => ls.Events)
                .ThenInclude(evt => evt.Jobs)
            .Include(ls => ls.Events)
                .ThenInclude(evt => evt.AppUserEvents)
            .Include(ls => ls.Events)
                .ThenInclude(evt => evt.EventLootDetails)
            .Include(ls => ls.EventHistories)
                .ThenInclude(history => history.AppUserEventHistories)
            .FirstOrDefaultAsync(ls => ls.Id == linkshellId, cancellationToken);

        if (linkshell is null)
        {
            return NotFound(new { error = "The selected linkshell was not found." });
        }

        if (linkshell.AppUserLinkshells.Count > 1)
        {
            return BadRequest(new
            {
                error = "Remove the remaining members or transfer ownership before deleting this linkshell."
            });
        }

        if (linkshell.Events.Count > 0)
        {
            return BadRequest(new
            {
                error = "Cancel or end all active and queued events before deleting this linkshell."
            });
        }

        var impactedUserIds = linkshell.AppUserLinkshells
            .Where(link => !string.IsNullOrWhiteSpace(link.AppUserId))
            .Select(link => link.AppUserId!)
            .Distinct()
            .ToList();

        if (impactedUserIds.Count > 0)
        {
            var impactedUsers = await _dbContext.Users
                .Where(user => impactedUserIds.Contains(user.Id))
                .ToListAsync(cancellationToken);

            foreach (var user in impactedUsers.Where(user => user.PrimaryLinkshellId == linkshellId))
            {
                var fallback = await _dbContext.AppUserLinkshells
                    .Include(link => link.Linkshell)
                    .Where(link => link.AppUserId == user.Id && link.LinkshellId != linkshellId)
                    .OrderBy(link => link.Linkshell!.LinkshellName)
                    .FirstOrDefaultAsync(cancellationToken);

                user.PrimaryLinkshellId = fallback?.LinkshellId;
                user.PrimaryLinkshellName = fallback?.Linkshell?.LinkshellName;
            }
        }

        var pendingInvites = await _dbContext.Invites
            .Where(invite => invite.LinkshellId == linkshellId)
            .ToListAsync(cancellationToken);

        if (pendingInvites.Count > 0)
        {
            _dbContext.Invites.RemoveRange(pendingInvites);
        }

        _dbContext.AppUserLinkshells.RemoveRange(linkshell.AppUserLinkshells);
        _dbContext.Jobs.RemoveRange(linkshell.Events.SelectMany(evt => evt.Jobs));
        _dbContext.AppUserEvents.RemoveRange(linkshell.Events.SelectMany(evt => evt.AppUserEvents));
        _dbContext.EventLootDetails.RemoveRange(linkshell.Events.SelectMany(evt => evt.EventLootDetails));
        _dbContext.Events.RemoveRange(linkshell.Events);
        _dbContext.AppUserEventHistories.RemoveRange(linkshell.EventHistories.SelectMany(history => history.AppUserEventHistories));
        _dbContext.EventHistories.RemoveRange(linkshell.EventHistories);
        _dbContext.Linkshells.Remove(linkshell);

        await _dbContext.SaveChangesAsync(cancellationToken);
        return Ok(new { success = true });
    }

    [HttpPost("linkshells/{linkshellId:int}/leave")]
    public async Task<IActionResult> LeaveLinkshellAsync(int linkshellId, CancellationToken cancellationToken)
    {
        var appUser = await ResolveAppUserAsync(cancellationToken);
        if (appUser is null)
        {
            return Unauthorized(new
            {
                error = "Sign in with ASP.NET Identity or provide a Discord bearer token to leave the linkshell."
            });
        }

        var membership = await GetMembershipAsync(appUser.Id, linkshellId, cancellationToken);
        if (membership is null)
        {
            return NotFound(new { error = "The selected linkshell membership was not found." });
        }

        var memberCount = await _dbContext.AppUserLinkshells
            .CountAsync(link => link.LinkshellId == linkshellId, cancellationToken);

        if (IsLeader(membership) && memberCount > 1)
        {
            return BadRequest(new { error = "Leaders must transfer ownership or remove remaining members before leaving." });
        }

        if (IsLeader(membership) && memberCount == 1)
        {
            return await DeleteLinkshellAsync(linkshellId, cancellationToken);
        }

        _dbContext.AppUserLinkshells.Remove(membership);

        if (appUser.PrimaryLinkshellId == linkshellId)
        {
            var fallback = await _dbContext.AppUserLinkshells
                .Include(link => link.Linkshell)
                .Where(link => link.AppUserId == appUser.Id && link.LinkshellId != linkshellId)
                .OrderBy(link => link.Linkshell!.LinkshellName)
                .FirstOrDefaultAsync(cancellationToken);

            appUser.PrimaryLinkshellId = fallback?.LinkshellId;
            appUser.PrimaryLinkshellName = fallback?.Linkshell?.LinkshellName;
        }

        var eventParticipations = await _dbContext.AppUserEvents
            .Include(participation => participation.Event)
            .Where(participation => participation.AppUserId == appUser.Id && participation.Event!.LinkshellId == linkshellId)
            .ToListAsync(cancellationToken);

        if (eventParticipations.Count > 0)
        {
            var affectedEventIds = eventParticipations.Select(participation => participation.EventId).Distinct().ToList();
            var jobs = await _dbContext.Jobs.Where(job => affectedEventIds.Contains(job.EventId)).ToListAsync(cancellationToken);
            var displayNames = new[]
            {
                appUser.CharacterName,
                appUser.UserName
            }.Where(name => !string.IsNullOrWhiteSpace(name)).ToArray();

            foreach (var participation in eventParticipations)
            {
                var job = jobs.FirstOrDefault(item =>
                    item.EventId == participation.EventId &&
                    item.JobName == participation.JobName &&
                    item.SubJobName == participation.SubJobName);

                if (job is not null)
                {
                    foreach (var name in displayNames)
                    {
                        job.Enlisted.RemoveAll(item => item == name);
                    }

                    if (!string.IsNullOrWhiteSpace(participation.CharacterName))
                    {
                        job.Enlisted.RemoveAll(item => item == participation.CharacterName);
                    }

                    job.SignedUp = job.Enlisted.Count;
                }
            }

            _dbContext.AppUserEvents.RemoveRange(eventParticipations);
        }

        var pendingInvites = await _dbContext.Invites
            .Where(invite => invite.LinkshellId == linkshellId && invite.AppUserId == appUser.Id)
            .ToListAsync(cancellationToken);

        if (pendingInvites.Count > 0)
        {
            _dbContext.Invites.RemoveRange(pendingInvites);
        }

        await _dbContext.SaveChangesAsync(cancellationToken);
        return Ok(new { success = true });
    }

    [HttpPost("events/{eventId:int}/signup")]
    public async Task<IActionResult> SignUpAsync(int eventId, [FromBody] ActivityEventSignupRequest request, CancellationToken cancellationToken)
    {
        var appUser = await ResolveAppUserAsync(cancellationToken);
        if (appUser is null)
        {
            return Unauthorized(new
            {
                error = "Sign in with ASP.NET Identity or provide a Discord bearer token to sign up for events."
            });
        }

        var displayName = appUser.CharacterName ?? appUser.UserName ?? "Unknown";

        if (request.JobId <= 0)
        {
            var eventEntity = await _dbContext.Events
                .Include(item => item.Jobs)
                .FirstOrDefaultAsync(item => item.Id == eventId, cancellationToken);

            if (eventEntity is null)
            {
                return NotFound(new { error = "The selected event was not found." });
            }

            if (eventEntity.Jobs.Count > 0)
            {
                return BadRequest(new { error = "A job selection is required." });
            }

            var existingNoJobSignup = await _dbContext.AppUserEvents
                .FirstOrDefaultAsync(item => item.EventId == eventId && item.AppUserId == appUser.Id, cancellationToken);

            if (existingNoJobSignup is not null)
            {
                _dbContext.AppUserEvents.Remove(existingNoJobSignup);
            }

            // For events with no pre-defined party setup, accept the user's
            // ad-hoc Main/Sub/Role from the body. Strings are trimmed and
            // null-coalesced so blank picks land as null instead of "".
            static string? Clean(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

            _dbContext.AppUserEvents.Add(new AppUserEvent
            {
                AppUserId = appUser.Id,
                EventId = eventId,
                CharacterName = displayName,
                JobName = Clean(request.JobName),
                SubJobName = Clean(request.SubJobName),
                JobType = Clean(request.JobType),
                EventDkp = 0,
                StartTime = eventEntity.CommencementStartTime
            });

            await _dbContext.SaveChangesAsync(cancellationToken);
            return Ok(new { success = true });
        }

        var job = await _dbContext.Jobs
            .Include(item => item.Event)
            .FirstOrDefaultAsync(item => item.Id == request.JobId && item.EventId == eventId, cancellationToken);

        if (job?.Event is null)
        {
            return NotFound(new { error = "The selected event job was not found." });
        }

        var existingSignup = await _dbContext.AppUserEvents
            .FirstOrDefaultAsync(item => item.EventId == eventId && item.AppUserId == appUser.Id, cancellationToken);

        if (existingSignup is not null)
        {
            var previousJob = await _dbContext.Jobs
                .FirstOrDefaultAsync(item =>
                    item.EventId == eventId &&
                    item.JobName == existingSignup.JobName &&
                    item.SubJobName == existingSignup.SubJobName,
                    cancellationToken);

            if (previousJob is not null)
            {
                previousJob.Enlisted.RemoveAll(name => name == existingSignup.CharacterName || name == displayName);
                previousJob.SignedUp = previousJob.Enlisted.Count;
            }

            _dbContext.AppUserEvents.Remove(existingSignup);
        }

        job.Enlisted ??= new List<string>();
        if (!job.Enlisted.Contains(displayName))
        {
            job.Enlisted.Add(displayName);
        }

        job.SignedUp = job.Enlisted.Count;

        _dbContext.AppUserEvents.Add(new AppUserEvent
        {
            AppUserId = appUser.Id,
            EventId = eventId,
            CharacterName = displayName,
            JobName = job.JobName,
            SubJobName = job.SubJobName,
            JobType = job.JobType,
            EventDkp = 0,
            StartTime = job.Event.CommencementStartTime
        });

        await _dbContext.SaveChangesAsync(cancellationToken);
        return Ok(new { success = true });
    }

    [HttpPost("events/{eventId:int}/quick-join")]
    public async Task<IActionResult> QuickJoinAsync(
        int eventId,
        [FromBody] ActivityQuickJoinRequest request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.JobName) ||
            string.IsNullOrWhiteSpace(request.SubJobName) ||
            string.IsNullOrWhiteSpace(request.JobType))
        {
            return BadRequest(new { error = "Job, sub job, and type are required for quick join." });
        }

        var appUser = await ResolveAppUserAsync(cancellationToken);
        if (appUser is null)
        {
            return Unauthorized(new
            {
                error = "Sign in with ASP.NET Identity or provide a Discord bearer token to quick join a live event."
            });
        }

        var eventEntity = await _dbContext.Events.FirstOrDefaultAsync(item => item.Id == eventId, cancellationToken);
        if (eventEntity is null)
        {
            return NotFound(new { error = "The selected event was not found." });
        }

        if (!eventEntity.CommencementStartTime.HasValue)
        {
            return BadRequest(new { error = "Quick join is only available after the event has started." });
        }

        var hasLinkshellMembership = await _dbContext.AppUserLinkshells
            .AnyAsync(link => link.AppUserId == appUser.Id && link.LinkshellId == eventEntity.LinkshellId, cancellationToken);

        if (!hasLinkshellMembership)
        {
            return Forbid();
        }

        var existingSignup = await _dbContext.AppUserEvents
            .FirstOrDefaultAsync(item => item.EventId == eventId && item.AppUserId == appUser.Id, cancellationToken);

        if (existingSignup is not null)
        {
            return BadRequest(new { error = "You are already attached to this live event." });
        }

        _dbContext.AppUserEvents.Add(new AppUserEvent
        {
            AppUserId = appUser.Id,
            EventId = eventId,
            CharacterName = appUser.CharacterName,
            JobName = request.JobName.Trim(),
            SubJobName = request.SubJobName.Trim(),
            JobType = request.JobType.Trim(),
            StartTime = DateTime.UtcNow,
            EventDkp = 0,
            IsQuickJoin = true
        });

        await _dbContext.SaveChangesAsync(cancellationToken);
        return Ok(new { success = true });
    }

    [HttpPost("events/{eventId:int}/break")]
    public async Task<IActionResult> TakeBreakAsync(int eventId, CancellationToken cancellationToken)
    {
        var appUser = await ResolveAppUserAsync(cancellationToken);
        if (appUser is null)
        {
            return Unauthorized(new
            {
                error = "Sign in with ASP.NET Identity or provide a Discord bearer token to update break status."
            });
        }

        var eventEntity = await _dbContext.Events.FirstOrDefaultAsync(evt => evt.Id == eventId, cancellationToken);
        if (eventEntity is null)
        {
            return NotFound(new { error = "The selected event was not found." });
        }

        if (!eventEntity.CommencementStartTime.HasValue)
        {
            return BadRequest(new { error = "Break status is only available after the event has started." });
        }

        var participation = await _dbContext.AppUserEvents
            .FirstOrDefaultAsync(item => item.EventId == eventId && item.AppUserId == appUser.Id, cancellationToken);

        if (participation is null)
        {
            return BadRequest(new { error = "Join the live event before taking a break." });
        }

        if (participation.IsOnBreak == true)
        {
            return BadRequest(new { error = "You are already marked as on break." });
        }

        var nowUtc = DateTime.UtcNow;
        participation.Duration = CalculateAccumulatedDurationHours(participation, nowUtc, eventEntity.CommencementStartTime);
        participation.IsOnBreak = true;
        participation.PauseTime = nowUtc;
        participation.ResumeTime = null;
        _dbContext.AppUserEventStatusLedgers.Add(new AppUserEventStatusLedger
        {
            AppUserEventId = participation.Id,
            EventId = eventId,
            AppUserId = appUser.Id,
            ActionType = "BreakStart",
            OccurredAt = nowUtc,
            RequiresVerification = false
        });

        await _dbContext.SaveChangesAsync(cancellationToken);
        return Ok(new { success = true });
    }

    [HttpPost("events/{eventId:int}/break/return")]
    public async Task<IActionResult> ReturnFromBreakAsync(int eventId, CancellationToken cancellationToken)
    {
        var appUser = await ResolveAppUserAsync(cancellationToken);
        if (appUser is null)
        {
            return Unauthorized(new
            {
                error = "Sign in with ASP.NET Identity or provide a Discord bearer token to update break status."
            });
        }

        var eventEntity = await _dbContext.Events.FirstOrDefaultAsync(evt => evt.Id == eventId, cancellationToken);
        if (eventEntity is null)
        {
            return NotFound(new { error = "The selected event was not found." });
        }

        if (!eventEntity.CommencementStartTime.HasValue)
        {
            return BadRequest(new { error = "Break status is only available after the event has started." });
        }

        var participation = await _dbContext.AppUserEvents
            .FirstOrDefaultAsync(item => item.EventId == eventId && item.AppUserId == appUser.Id, cancellationToken);

        if (participation is null)
        {
            return BadRequest(new { error = "Join the live event before returning from break." });
        }

        if (participation.IsOnBreak != true)
        {
            return BadRequest(new { error = "You are not currently marked as on break." });
        }

        participation.IsOnBreak = false;
        participation.PauseTime = null;
        participation.ResumeTime = DateTime.UtcNow;
        _dbContext.AppUserEventStatusLedgers.Add(new AppUserEventStatusLedger
        {
            AppUserEventId = participation.Id,
            EventId = eventId,
            AppUserId = appUser.Id,
            ActionType = "BreakReturn",
            OccurredAt = participation.ResumeTime.Value,
            RequiresVerification = true
        });

        await _dbContext.SaveChangesAsync(cancellationToken);
        return Ok(new { success = true });
    }

    [HttpPost("events/{eventId:int}/break/force")]
    public async Task<IActionResult> ForceBreakAsync(
        int eventId,
        [FromBody] ActivityForceBreakRequest request,
        CancellationToken cancellationToken)
    {
        var appUser = await ResolveAppUserAsync(cancellationToken);
        if (appUser is null)
        {
            return Unauthorized(new
            {
                error = "Sign in with ASP.NET Identity or provide a Discord bearer token to send a member to the break room."
            });
        }

        var eventEntity = await _dbContext.Events.FirstOrDefaultAsync(evt => evt.Id == eventId, cancellationToken);
        if (eventEntity is null)
        {
            return NotFound(new { error = "The selected event was not found." });
        }

        var membership = await GetMembershipAsync(appUser.Id, eventEntity.LinkshellId, cancellationToken);
        if (!await CanAsync(membership, r => r.CanModerateLiveEvent, cancellationToken))
        {
            return Forbid();
        }

        if (!eventEntity.CommencementStartTime.HasValue)
        {
            return BadRequest(new { error = "Break status is only available after the event has started." });
        }

        var participation = await _dbContext.AppUserEvents
            .FirstOrDefaultAsync(item => item.Id == request.ParticipantId && item.EventId == eventId, cancellationToken);

        if (participation is null)
        {
            return NotFound(new { error = "The selected participant was not found." });
        }

        if (participation.IsOnBreak == true)
        {
            return BadRequest(new { error = "That member is already marked as on break." });
        }

        var nowUtc = DateTime.UtcNow;
        participation.Duration = CalculateAccumulatedDurationHours(participation, nowUtc, eventEntity.CommencementStartTime);
        participation.IsOnBreak = true;
        participation.PauseTime = nowUtc;
        participation.ResumeTime = null;
        _dbContext.AppUserEventStatusLedgers.Add(new AppUserEventStatusLedger
        {
            AppUserEventId = participation.Id,
            EventId = eventId,
            AppUserId = participation.AppUserId,
            ActionType = "BreakStart",
            OccurredAt = nowUtc,
            RequiresVerification = false
        });

        await _dbContext.SaveChangesAsync(cancellationToken);
        return Ok(new { success = true });
    }

    [HttpPost("events/{eventId:int}/break/resume/force")]
    public async Task<IActionResult> ForceResumeAsync(
        int eventId,
        [FromBody] ActivityForceResumeRequest request,
        CancellationToken cancellationToken)
    {
        var appUser = await ResolveAppUserAsync(cancellationToken);
        if (appUser is null)
        {
            return Unauthorized(new
            {
                error = "Sign in with ASP.NET Identity or provide a Discord bearer token to resume a member."
            });
        }

        var eventEntity = await _dbContext.Events.FirstOrDefaultAsync(evt => evt.Id == eventId, cancellationToken);
        if (eventEntity is null)
        {
            return NotFound(new { error = "The selected event was not found." });
        }

        var membership = await GetMembershipAsync(appUser.Id, eventEntity.LinkshellId, cancellationToken);
        if (!await CanAsync(membership, r => r.CanModerateLiveEvent, cancellationToken))
        {
            return Forbid();
        }

        if (!eventEntity.CommencementStartTime.HasValue)
        {
            return BadRequest(new { error = "Break status is only available after the event has started." });
        }

        var participation = await _dbContext.AppUserEvents
            .FirstOrDefaultAsync(item => item.Id == request.ParticipantId && item.EventId == eventId, cancellationToken);

        if (participation is null)
        {
            return NotFound(new { error = "The selected participant was not found." });
        }

        if (participation.IsOnBreak != true)
        {
            return BadRequest(new { error = "That member is not currently on break." });
        }

        var nowUtc = DateTime.UtcNow;
        participation.IsOnBreak = false;
        participation.PauseTime = null;
        participation.ResumeTime = nowUtc;

        var pendingReturns = await _dbContext.AppUserEventStatusLedgers
            .Where(entry =>
                entry.AppUserEventId == participation.Id &&
                entry.ActionType == "BreakReturn" &&
                entry.RequiresVerification &&
                entry.VerifiedAt == null &&
                entry.DeniedAt == null)
            .ToListAsync(cancellationToken);
        foreach (var pending in pendingReturns)
        {
            pending.VerifiedAt = nowUtc;
            pending.VerifiedBy = appUser.CharacterName ?? appUser.UserName;
        }

        _dbContext.AppUserEventStatusLedgers.Add(new AppUserEventStatusLedger
        {
            AppUserEventId = participation.Id,
            EventId = eventId,
            AppUserId = participation.AppUserId,
            ActionType = "BreakReturn",
            OccurredAt = nowUtc,
            RequiresVerification = false,
            VerifiedAt = nowUtc,
            VerifiedBy = appUser.CharacterName ?? appUser.UserName
        });

        await _dbContext.SaveChangesAsync(cancellationToken);
        return Ok(new { success = true });
    }

    [HttpPost("events")]
    public async Task<IActionResult> CreateEventAsync([FromBody] ActivityCreateEventRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.EventName))
        {
            return BadRequest(new { error = "Event name is required." });
        }

        if (request.LinkshellId <= 0)
        {
            return BadRequest(new { error = "A linkshell selection is required." });
        }

        var appUser = await ResolveAppUserAsync(cancellationToken);
        if (appUser is null)
        {
            return Unauthorized(new
            {
                error = "Sign in with ASP.NET Identity or provide a Discord bearer token to create events."
            });
        }

        var membership = await GetMembershipAsync(appUser.Id, request.LinkshellId, cancellationToken);
        if (!await CanAsync(membership, r => r.CanManageEvents, cancellationToken))
        {
            return Forbid();
        }

        if (!TryConvertUserTimeZoneToUtc(request.StartTimeLocal, appUser.TimeZone, out var startTimeUtc) ||
            !TryConvertUserTimeZoneToUtc(request.EndTimeLocal, appUser.TimeZone, out var endTimeUtc))
        {
            return BadRequest(new { error = "Use valid local start and end times in the event form." });
        }

        var eventEntity = new Event
        {
            LinkshellId = request.LinkshellId,
            EventName = request.EventName.Trim(),
            EventType = request.EventType?.Trim(),
            EventLocation = request.EventLocation?.Trim(),
            CreatorUserId = appUser.Id,
            StartTime = startTimeUtc,
            EndTime = endTimeUtc,
            Duration = request.Duration,
            DkpPerHour = request.DkpPerHour,
            Details = request.Details?.Trim(),
            TimeStamp = DateTime.UtcNow
        };

        _dbContext.Events.Add(eventEntity);
        await _dbContext.SaveChangesAsync(cancellationToken);

        foreach (var job in request.Jobs.Where(job => !string.IsNullOrWhiteSpace(job.JobName)))
        {
            _dbContext.Jobs.Add(new Job
            {
                EventId = eventEntity.Id,
                JobName = job.JobName?.Trim(),
                SubJobName = job.SubJobName?.Trim(),
                JobType = job.JobType?.Trim(),
                Quantity = job.Quantity,
                SignedUp = 0,
                Enlisted = new List<string>(),
                Details = job.Details?.Trim()
            });
        }

        await _dbContext.SaveChangesAsync(cancellationToken);
        return Ok(new { success = true, eventId = eventEntity.Id });
    }

    [HttpPost("events/{eventId:int}/update")]
    public async Task<IActionResult> UpdateEventAsync(
        int eventId,
        [FromBody] ActivityCreateEventRequest request,
        CancellationToken cancellationToken)
    {
        if (request.LinkshellId <= 0)
        {
            return BadRequest(new { error = "A linkshell selection is required." });
        }

        if (string.IsNullOrWhiteSpace(request.EventName))
        {
            return BadRequest(new { error = "Event name is required." });
        }

        var appUser = await ResolveAppUserAsync(cancellationToken);
        if (appUser is null)
        {
            return Unauthorized(new
            {
                error = "Sign in with ASP.NET Identity or provide a Discord bearer token to update events."
            });
        }

        var membership = await GetMembershipAsync(appUser.Id, request.LinkshellId, cancellationToken);
        if (!await CanAsync(membership, r => r.CanManageEvents, cancellationToken))
        {
            return Forbid();
        }

        var eventEntity = await _dbContext.Events
            .Include(evt => evt.Jobs)
            .Include(evt => evt.AppUserEvents)
            .FirstOrDefaultAsync(evt => evt.Id == eventId, cancellationToken);

        if (eventEntity is null)
        {
            return NotFound(new { error = "The selected event was not found." });
        }

        var currentMembership = await GetMembershipAsync(appUser.Id, eventEntity.LinkshellId, cancellationToken);
        if (!await CanAsync(currentMembership, r => r.CanManageEvents, cancellationToken))
        {
            return Forbid();
        }

        if (!TryConvertUserTimeZoneToUtc(request.StartTimeLocal, appUser.TimeZone, out var startTimeUtc) ||
            !TryConvertUserTimeZoneToUtc(request.EndTimeLocal, appUser.TimeZone, out var endTimeUtc))
        {
            return BadRequest(new { error = "Use valid local start and end times in the event form." });
        }

        if (eventEntity.CommencementStartTime.HasValue)
        {
            return BadRequest(new { error = "Live events cannot be edited. End the event or create a new one instead." });
        }

        var hasJobChanges = eventEntity.Jobs.Count != request.Jobs.Count ||
                            eventEntity.Jobs
                                .Select(CreateJobSignature)
                                .OrderBy(signature => signature)
                                .SequenceEqual(request.Jobs.Select(CreateJobSignature).OrderBy(signature => signature)) == false;

        if (eventEntity.AppUserEvents.Count > 0 && hasJobChanges)
        {
            return BadRequest(new { error = "Jobs cannot be changed after players have signed up. Remove signups or keep the existing job list." });
        }

        eventEntity.LinkshellId = request.LinkshellId;
        eventEntity.EventName = request.EventName.Trim();
        eventEntity.EventType = request.EventType?.Trim();
        eventEntity.EventLocation = request.EventLocation?.Trim();
        eventEntity.StartTime = startTimeUtc;
        eventEntity.EndTime = endTimeUtc;
        eventEntity.Duration = request.Duration;
        eventEntity.DkpPerHour = request.DkpPerHour;
        eventEntity.Details = request.Details?.Trim();

        _dbContext.Jobs.RemoveRange(eventEntity.Jobs);
        await _dbContext.SaveChangesAsync(cancellationToken);

        foreach (var job in request.Jobs.Where(job => !string.IsNullOrWhiteSpace(job.JobName)))
        {
            _dbContext.Jobs.Add(new Job
            {
                EventId = eventEntity.Id,
                JobName = job.JobName?.Trim(),
                SubJobName = job.SubJobName?.Trim(),
                JobType = job.JobType?.Trim(),
                Quantity = job.Quantity,
                SignedUp = 0,
                Enlisted = new List<string>(),
                Details = job.Details?.Trim()
            });
        }

        await _dbContext.SaveChangesAsync(cancellationToken);
        return Ok(new { success = true });
    }

    [HttpPost("events/{eventId:int}/start")]
    public async Task<IActionResult> StartEventAsync(
        int eventId,
        [FromBody] ActivityStartEventRequest? request,
        CancellationToken cancellationToken)
    {
        var appUser = await ResolveAppUserAsync(cancellationToken);
        if (appUser is null)
        {
            return Unauthorized(new
            {
                error = "Sign in with ASP.NET Identity or provide a Discord bearer token to start events."
            });
        }

        var eventEntity = await _dbContext.Events
            .Include(evt => evt.AppUserEvents)
            .Include(evt => evt.Linkshell)
            .FirstOrDefaultAsync(evt => evt.Id == eventId, cancellationToken);

        if (eventEntity is null)
        {
            return NotFound(new { error = "The selected event was not found." });
        }

        var membership = await GetMembershipAsync(appUser.Id, eventEntity.LinkshellId, cancellationToken);
        if (!await CanAsync(membership, r => r.CanManageEvents, cancellationToken))
        {
            return Forbid();
        }

        var absentIds = request?.AbsentParticipantIds;
        if (absentIds is { Count: > 0 })
        {
            var absentSet = new HashSet<int>(absentIds);
            var absentParticipations = eventEntity.AppUserEvents
                .Where(p => absentSet.Contains(p.Id))
                .ToList();

            if (absentParticipations.Count > 0)
            {
                var jobs = await _dbContext.Jobs
                    .Where(job => job.EventId == eventId)
                    .ToListAsync(cancellationToken);

                foreach (var participation in absentParticipations)
                {
                    var job = jobs.FirstOrDefault(j =>
                        j.JobName == participation.JobName &&
                        j.SubJobName == participation.SubJobName);

                    if (job is not null)
                    {
                        job.Enlisted.RemoveAll(name => name == participation.CharacterName);
                        job.SignedUp = job.Enlisted.Count;
                    }

                    _dbContext.AppUserEvents.Remove(participation);
                }
            }
        }

        eventEntity.CommencementStartTime ??= DateTime.UtcNow;
        foreach (var participation in eventEntity.AppUserEvents)
        {
            if (absentIds is { Count: > 0 } && absentIds.Contains(participation.Id))
            {
                continue;
            }
            participation.StartTime ??= eventEntity.CommencementStartTime;
        }

        await _dbContext.SaveChangesAsync(cancellationToken);
        return Ok(new { success = true });
    }

    [HttpPost("events/{eventId:int}/verify")]
    public async Task<IActionResult> VerifyParticipantAsync(
        int eventId,
        [FromBody] ActivityVerifyParticipantRequest request,
        CancellationToken cancellationToken)
    {
        var appUser = await ResolveAppUserAsync(cancellationToken);
        if (appUser is null)
        {
            return Unauthorized(new
            {
                error = "Sign in with ASP.NET Identity or provide a Discord bearer token to verify attendance."
            });
        }

        var eventEntity = await _dbContext.Events
            .FirstOrDefaultAsync(evt => evt.Id == eventId, cancellationToken);

        if (eventEntity is null)
        {
            return NotFound(new { error = "The selected event was not found." });
        }

        var membership = await GetMembershipAsync(appUser.Id, eventEntity.LinkshellId, cancellationToken);
        if (!await CanAsync(membership, r => r.CanModerateLiveEvent, cancellationToken))
        {
            return Forbid();
        }

        var participation = await _dbContext.AppUserEvents
            .FirstOrDefaultAsync(item => item.Id == request.ParticipantId && item.EventId == eventId, cancellationToken);

        if (participation is null)
        {
            return NotFound(new { error = "The selected participant was not found." });
        }

        if (participation.IsVerified.HasValue)
        {
            return BadRequest(new { error = "Initial attendance has already been verified. Use undo if you need to change it." });
        }

        participation.IsVerified = request.IsVerified;
        participation.Proctor = appUser.CharacterName ?? appUser.UserName;

        await _dbContext.SaveChangesAsync(cancellationToken);
        return Ok(new { success = true });
    }

    [HttpPost("events/{eventId:int}/verify/reset")]
    public async Task<IActionResult> ResetVerificationAsync(
        int eventId,
        [FromBody] ActivityResetParticipantRequest request,
        CancellationToken cancellationToken)
    {
        var appUser = await ResolveAppUserAsync(cancellationToken);
        if (appUser is null)
        {
            return Unauthorized(new
            {
                error = "Sign in with ASP.NET Identity or provide a Discord bearer token to reset attendance verification."
            });
        }

        var eventEntity = await _dbContext.Events
            .FirstOrDefaultAsync(evt => evt.Id == eventId, cancellationToken);

        if (eventEntity is null)
        {
            return NotFound(new { error = "The selected event was not found." });
        }

        var membership = await GetMembershipAsync(appUser.Id, eventEntity.LinkshellId, cancellationToken);
        if (!await CanAsync(membership, r => r.CanModerateLiveEvent, cancellationToken))
        {
            return Forbid();
        }

        var participation = await _dbContext.AppUserEvents
            .FirstOrDefaultAsync(item => item.Id == request.ParticipantId && item.EventId == eventId, cancellationToken);

        if (participation is null)
        {
            return NotFound(new { error = "The selected participant was not found." });
        }

        participation.IsVerified = null;
        participation.Proctor = null;

        await _dbContext.SaveChangesAsync(cancellationToken);
        return Ok(new { success = true });
    }

    [HttpPost("events/{eventId:int}/verify-return")]
    public async Task<IActionResult> VerifyReturnAsync(
        int eventId,
        [FromBody] ActivityVerifyReturnRequest request,
        CancellationToken cancellationToken)
    {
        var appUser = await ResolveAppUserAsync(cancellationToken);
        if (appUser is null)
        {
            return Unauthorized(new
            {
                error = "Sign in with ASP.NET Identity or provide a Discord bearer token to verify a break return."
            });
        }

        var eventEntity = await _dbContext.Events.FirstOrDefaultAsync(evt => evt.Id == eventId, cancellationToken);
        if (eventEntity is null)
        {
            return NotFound(new { error = "The selected event was not found." });
        }

        var membership = await GetMembershipAsync(appUser.Id, eventEntity.LinkshellId, cancellationToken);
        if (!await CanAsync(membership, r => r.CanModerateLiveEvent, cancellationToken))
        {
            return Forbid();
        }

        var ledgerEntry = await _dbContext.AppUserEventStatusLedgers
            .FirstOrDefaultAsync(
                item => item.Id == request.LedgerEntryId &&
                        item.EventId == eventId &&
                        item.ActionType == "BreakReturn",
                cancellationToken);

        if (ledgerEntry is null)
        {
            return NotFound(new { error = "The selected ledger entry was not found." });
        }

        if (!ledgerEntry.RequiresVerification || ledgerEntry.VerifiedAt.HasValue)
        {
            return BadRequest(new { error = "That break return has already been verified." });
        }

        ledgerEntry.VerifiedAt = DateTime.UtcNow;
        ledgerEntry.VerifiedBy = appUser.CharacterName ?? appUser.UserName;
        ledgerEntry.RequiresVerification = false;

        await _dbContext.SaveChangesAsync(cancellationToken);
        return Ok(new { success = true });
    }

    [HttpPost("events/{eventId:int}/deny-return")]
    public async Task<IActionResult> DenyReturnAsync(
        int eventId,
        [FromBody] ActivityVerifyReturnRequest request,
        CancellationToken cancellationToken)
    {
        var appUser = await ResolveAppUserAsync(cancellationToken);
        if (appUser is null)
        {
            return Unauthorized(new
            {
                error = "Sign in with ASP.NET Identity or provide a Discord bearer token to deny a break return."
            });
        }

        var eventEntity = await _dbContext.Events.FirstOrDefaultAsync(evt => evt.Id == eventId, cancellationToken);
        if (eventEntity is null)
        {
            return NotFound(new { error = "The selected event was not found." });
        }

        var membership = await GetMembershipAsync(appUser.Id, eventEntity.LinkshellId, cancellationToken);
        if (!await CanAsync(membership, r => r.CanModerateLiveEvent, cancellationToken))
        {
            return Forbid();
        }

        var ledgerEntry = await _dbContext.AppUserEventStatusLedgers
            .FirstOrDefaultAsync(
                item => item.Id == request.LedgerEntryId &&
                        item.EventId == eventId &&
                        item.ActionType == "BreakReturn",
                cancellationToken);

        if (ledgerEntry is null)
        {
            return NotFound(new { error = "The selected ledger entry was not found." });
        }

        if (!ledgerEntry.RequiresVerification || ledgerEntry.VerifiedAt.HasValue || ledgerEntry.DeniedAt.HasValue)
        {
            return BadRequest(new { error = "That break return has already been resolved." });
        }

        ledgerEntry.DeniedAt = DateTime.UtcNow;
        ledgerEntry.DeniedBy = appUser.CharacterName ?? appUser.UserName;
        ledgerEntry.RequiresVerification = false;

        await _dbContext.SaveChangesAsync(cancellationToken);
        return Ok(new { success = true });
    }

    [HttpPost("events/{eventId:int}/loot")]
    public async Task<IActionResult> AddLootAsync(
        int eventId,
        [FromBody] ActivityAddLootRequest request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.ItemName))
        {
            return BadRequest(new { error = "Item name is required." });
        }

        var appUser = await ResolveAppUserAsync(cancellationToken);
        if (appUser is null)
        {
            return Unauthorized(new
            {
                error = "Sign in with ASP.NET Identity or provide a Discord bearer token to add loot."
            });
        }

        var eventEntity = await _dbContext.Events
            .FirstOrDefaultAsync(evt => evt.Id == eventId, cancellationToken);

        if (eventEntity is null)
        {
            return NotFound(new { error = "The selected event was not found." });
        }

        var membership = await GetMembershipAsync(appUser.Id, eventEntity.LinkshellId, cancellationToken);
        if (!await CanAsync(membership, r => r.CanAddLoot, cancellationToken))
        {
            return Forbid();
        }

        _dbContext.EventLootDetails.Add(new EventLootDetail
        {
            EventId = eventId,
            ItemName = request.ItemName.Trim(),
            ItemWinner = request.ItemWinner?.Trim(),
            WinningDkpSpent = request.WinningDkpSpent
        });

        await _dbContext.SaveChangesAsync(cancellationToken);
        return Ok(new { success = true });
    }

    [HttpPost("tods")]
    public async Task<IActionResult> CreateTodAsync(
        [FromBody] ActivityCreateTodRequest request,
        CancellationToken cancellationToken)
    {
        if (request.LinkshellId <= 0)
        {
            return BadRequest(new { error = "A linkshell selection is required." });
        }

        var appUser = await ResolveAppUserAsync(cancellationToken);
        if (appUser is null)
        {
            return Unauthorized(new
            {
                error = "Sign in with ASP.NET Identity or provide a Discord bearer token to log a ToD entry."
            });
        }

        var membership = await GetMembershipAsync(appUser.Id, request.LinkshellId, cancellationToken);
        if (membership is null)
        {
            return Forbid();
        }

        var monsterName = request.MonsterName?.Trim();
        if (string.IsNullOrWhiteSpace(monsterName))
        {
            return BadRequest(new { error = "A monster name is required." });
        }

        if (!TryConvertUserTimeZoneToUtc(request.TimeLocal, appUser.TimeZone, out var todTimeUtc) || !todTimeUtc.HasValue)
        {
            return BadRequest(new { error = "Enter a valid Time of Death using your local time." });
        }

        var cooldown = string.IsNullOrWhiteSpace(request.Cooldown)
            ? GetDefaultTodCooldown(monsterName)
            : request.Cooldown.Trim();
        if (!IsAcceptableTodCooldown(cooldown))
        {
            return BadRequest(new { error = "Enter a valid cooldown (e.g. 22 Hour, 72 Hour, or a positive number of hours)." });
        }

        var interval = request.Interval?.Trim();
        if (string.IsNullOrWhiteSpace(interval))
        {
            interval = null;
        }
        else if (!SupportedTodIntervals.Contains(interval))
        {
            return BadRequest(new { error = "Select a valid interval." });
        }

        var linkshellEntity = await _dbContext.Linkshells
            .FirstOrDefaultAsync(ls => ls.Id == request.LinkshellId, cancellationToken);
        var linkshellStructure = NormalizeLootStructure(linkshellEntity?.LootStructure ?? "Dkp");

        var normalizedLootDetails = request.Claim && !request.NoLoot && linkshellStructure != "LootCouncil"
            ? NormalizeTodLootDetails(request.LootDetails)
            : new List<TodLootDetail>();

        var validCharacterNames = await _dbContext.AppUserLinkshells
            .Where(link => link.LinkshellId == request.LinkshellId)
            .Select(link => link.CharacterName)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => name!)
            .ToListAsync(cancellationToken);

        foreach (var lootDetail in normalizedLootDetails)
        {
            if (string.IsNullOrWhiteSpace(lootDetail.ItemName))
            {
                return BadRequest(new { error = "Each ToD loot row needs an item name." });
            }

            if (string.IsNullOrWhiteSpace(lootDetail.ItemWinner))
            {
                return BadRequest(new { error = "Each ToD loot row needs an item winner." });
            }

            if (!validCharacterNames.Contains(lootDetail.ItemWinner.Trim(), StringComparer.OrdinalIgnoreCase))
            {
                return BadRequest(new { error = "Choose a loot winner from the current linkshell roster." });
            }

            if (!lootDetail.WinningDkpSpent.HasValue || lootDetail.WinningDkpSpent <= 0)
            {
                return BadRequest(new
                {
                    error = linkshellStructure == "Hybrid"
                        ? "Each ToD loot row needs a deduction % (1-100)."
                        : "Each ToD loot row needs a positive DKP spent value."
                });
            }

            if (linkshellStructure == "Hybrid" && lootDetail.WinningDkpSpent > 100)
            {
                return BadRequest(new { error = "Deduction % cannot exceed 100." });
            }
        }

        var nowUtc = DateTime.UtcNow;
        var tod = new Tod
        {
            LinkshellId = request.LinkshellId,
            MonsterName = monsterName,
            DayNumber = request.DayNumber,
            Claim = request.Claim,
            Time = todTimeUtc,
            Cooldown = cooldown,
            RepopTime = todTimeUtc.Value.AddHours(ResolveTodCooldownHours(cooldown)),
            Interval = interval,
            TimeStamp = nowUtc,
            TotalTods = 1,
            TotalClaims = request.Claim ? 1 : 0,
            ImagePath = SanitizeUploadedImagePath(request.ImagePath)
        };

        _dbContext.Tods.Add(tod);
        await _dbContext.SaveChangesAsync(cancellationToken);

        if (normalizedLootDetails.Count > 0)
        {
            foreach (var lootDetail in normalizedLootDetails)
            {
                lootDetail.TodId = tod.Id;
            }

            await _dbContext.TodLootDetails.AddRangeAsync(normalizedLootDetails, cancellationToken);
            await AdjustTodLootDkpAsync(_dbContext, tod, normalizedLootDetails, nowUtc, isRefund: false, cancellationToken);
            await _dbContext.SaveChangesAsync(cancellationToken);
            tod.TodLootDetails = normalizedLootDetails;
        }

        return Ok(MapTodDto(tod));
    }

    [HttpPost("tods/{todId:int}/delete")]
    public async Task<IActionResult> DeleteTodAsync(int todId, CancellationToken cancellationToken)
    {
        var appUser = await ResolveAppUserAsync(cancellationToken);
        if (appUser is null)
        {
            return Unauthorized(new
            {
                error = "Sign in with ASP.NET Identity or provide a Discord bearer token to delete a ToD entry."
            });
        }

        var tod = await _dbContext.Tods
            .Include(item => item.TodLootDetails)
            .FirstOrDefaultAsync(item => item.Id == todId, cancellationToken);

        if (tod is null)
        {
            return NotFound(new { error = "The selected ToD entry was not found." });
        }

        var membership = await GetMembershipAsync(appUser.Id, tod.LinkshellId, cancellationToken);
        if (membership is null)
        {
            return Forbid();
        }

        await AdjustTodLootDkpAsync(_dbContext, tod, tod.TodLootDetails.ToList(), DateTime.UtcNow, isRefund: true, cancellationToken);
        _dbContext.TodLootDetails.RemoveRange(tod.TodLootDetails);
        DeleteUploadedTodImage(tod.ImagePath);
        _dbContext.Tods.Remove(tod);
        await _dbContext.SaveChangesAsync(cancellationToken);

        return Ok(new { success = true });
    }

    [HttpPost("tods/upload-image")]
    [RequestSizeLimit(2_200_000)]
    public async Task<IActionResult> UploadTodImageAsync(
        [FromForm] IFormFile? file,
        CancellationToken cancellationToken)
    {
        var appUser = await ResolveAppUserAsync(cancellationToken);
        if (appUser is null)
        {
            return Unauthorized(new { error = "Sign in to upload ToD images." });
        }

        if (file is null || file.Length <= 0)
        {
            return BadRequest(new { error = "Choose an image to upload." });
        }

        if (file.Length > 2_000_000)
        {
            return BadRequest(new { error = "Images must be 2 MB or smaller." });
        }

        var allowedExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".png", ".jpg", ".jpeg", ".webp" };
        var extension = Path.GetExtension(file.FileName).ToLowerInvariant();
        if (!allowedExtensions.Contains(extension))
        {
            return BadRequest(new { error = "Only PNG, JPG, or WEBP images are supported." });
        }

        var webRoot = _webHostEnvironment.WebRootPath;
        if (string.IsNullOrWhiteSpace(webRoot))
        {
            webRoot = Path.Combine(Directory.GetCurrentDirectory(), "wwwroot");
        }

        var uploadsDir = Path.Combine(webRoot, "uploads", "tods");
        Directory.CreateDirectory(uploadsDir);

        var fileName = $"{Guid.NewGuid():N}{extension}";
        var absolutePath = Path.Combine(uploadsDir, fileName);
        await using (var stream = System.IO.File.Create(absolutePath))
        {
            await file.CopyToAsync(stream, cancellationToken);
        }

        var relativePath = $"/uploads/tods/{fileName}";
        return Ok(new { imagePath = relativePath });
    }

    [HttpPost("tods/{todId:int}/update")]
    public async Task<IActionResult> UpdateTodAsync(
        int todId,
        [FromBody] ActivityUpdateTodRequest request,
        CancellationToken cancellationToken)
    {
        var appUser = await ResolveAppUserAsync(cancellationToken);
        if (appUser is null)
        {
            return Unauthorized(new { error = "Sign in to update a ToD entry." });
        }

        var tod = await _dbContext.Tods
            .Include(item => item.TodLootDetails)
            .Include(item => item.Linkshell)
            .FirstOrDefaultAsync(item => item.Id == todId, cancellationToken);

        if (tod is null)
        {
            return NotFound(new { error = "The selected ToD entry was not found." });
        }

        var membership = await GetMembershipAsync(appUser.Id, tod.LinkshellId, cancellationToken);
        if (membership is null)
        {
            return Forbid();
        }

        var linkshellStructure = NormalizeLootStructure(tod.Linkshell?.LootStructure ?? "Dkp");

        var monsterName = request.MonsterName?.Trim();
        if (string.IsNullOrWhiteSpace(monsterName))
        {
            return BadRequest(new { error = "A monster name is required." });
        }

        if (!TryConvertUserTimeZoneToUtc(request.TimeLocal, appUser.TimeZone, out var todTimeUtc) || !todTimeUtc.HasValue)
        {
            return BadRequest(new { error = "Enter a valid Time of Death using your local time." });
        }

        var cooldown = string.IsNullOrWhiteSpace(request.Cooldown)
            ? GetDefaultTodCooldown(monsterName)
            : request.Cooldown.Trim();
        if (!IsAcceptableTodCooldown(cooldown))
        {
            return BadRequest(new { error = "Enter a valid cooldown." });
        }

        var interval = request.Interval?.Trim();
        if (string.IsNullOrWhiteSpace(interval))
        {
            interval = null;
        }
        else if (!SupportedTodIntervals.Contains(interval))
        {
            return BadRequest(new { error = "Select a valid interval." });
        }

        var normalizedLootDetails = request.Claim && !request.NoLoot && linkshellStructure != "LootCouncil"
            ? NormalizeTodLootDetails(request.LootDetails)
            : new List<TodLootDetail>();

        var validCharacterNames = await _dbContext.AppUserLinkshells
            .Where(link => link.LinkshellId == tod.LinkshellId)
            .Select(link => link.CharacterName)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => name!)
            .ToListAsync(cancellationToken);

        foreach (var lootDetail in normalizedLootDetails)
        {
            if (string.IsNullOrWhiteSpace(lootDetail.ItemName))
            {
                return BadRequest(new { error = "Each ToD loot row needs an item name." });
            }

            if (string.IsNullOrWhiteSpace(lootDetail.ItemWinner))
            {
                return BadRequest(new { error = "Each ToD loot row needs an item winner." });
            }

            if (!validCharacterNames.Contains(lootDetail.ItemWinner.Trim(), StringComparer.OrdinalIgnoreCase))
            {
                return BadRequest(new { error = "Choose a loot winner from the current linkshell roster." });
            }

            if (!lootDetail.WinningDkpSpent.HasValue || lootDetail.WinningDkpSpent <= 0)
            {
                return BadRequest(new
                {
                    error = linkshellStructure == "Hybrid"
                        ? "Each ToD loot row needs a deduction % (1-100)."
                        : "Each ToD loot row needs a positive DKP spent value."
                });
            }

            if (linkshellStructure == "Hybrid" && lootDetail.WinningDkpSpent > 100)
            {
                return BadRequest(new { error = "Deduction % cannot exceed 100." });
            }
        }

        var nowUtc = DateTime.UtcNow;

        // Reverse DKP impact from existing loot, remove it, then apply the new set.
        if (tod.TodLootDetails.Count > 0)
        {
            await AdjustTodLootDkpAsync(_dbContext, tod, tod.TodLootDetails.ToList(), nowUtc, isRefund: true, cancellationToken);
            _dbContext.TodLootDetails.RemoveRange(tod.TodLootDetails);
        }

        tod.MonsterName = monsterName;
        tod.DayNumber = request.DayNumber;
        tod.Claim = request.Claim;
        tod.Time = todTimeUtc;
        tod.Cooldown = cooldown;
        tod.RepopTime = todTimeUtc.Value.AddHours(ResolveTodCooldownHours(cooldown));
        tod.Interval = interval;
        tod.TimeStamp = nowUtc;
        tod.TotalClaims = request.Claim ? 1 : 0;

        var previousImage = tod.ImagePath;
        var newImage = SanitizeUploadedImagePath(request.ImagePath);
        tod.ImagePath = newImage;

        await _dbContext.SaveChangesAsync(cancellationToken);

        if (normalizedLootDetails.Count > 0)
        {
            foreach (var lootDetail in normalizedLootDetails)
            {
                lootDetail.TodId = tod.Id;
            }

            await _dbContext.TodLootDetails.AddRangeAsync(normalizedLootDetails, cancellationToken);
            await AdjustTodLootDkpAsync(_dbContext, tod, normalizedLootDetails, nowUtc, isRefund: false, cancellationToken);
            await _dbContext.SaveChangesAsync(cancellationToken);
            tod.TodLootDetails = normalizedLootDetails;
        }
        else
        {
            tod.TodLootDetails = new List<TodLootDetail>();
        }

        if (!string.IsNullOrWhiteSpace(previousImage) && !string.Equals(previousImage, newImage, StringComparison.Ordinal))
        {
            DeleteUploadedTodImage(previousImage);
        }

        return Ok(MapTodDto(tod));
    }

    private void DeleteUploadedTodImage(string? relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath) || SanitizeUploadedImagePath(relativePath) is null)
        {
            return;
        }

        var webRoot = _webHostEnvironment.WebRootPath;
        if (string.IsNullOrWhiteSpace(webRoot))
        {
            webRoot = Path.Combine(Directory.GetCurrentDirectory(), "wwwroot");
        }

        var fileName = Path.GetFileName(relativePath);
        var absolutePath = Path.Combine(webRoot, "uploads", "tods", fileName);
        try
        {
            if (System.IO.File.Exists(absolutePath))
            {
                System.IO.File.Delete(absolutePath);
            }
        }
        catch
        {
            // Best-effort cleanup.
        }
    }

    [HttpPost("events/{eventId:int}/end")]
    public async Task<IActionResult> EndEventAsync(int eventId, CancellationToken cancellationToken)
    {
        var appUser = await ResolveAppUserAsync(cancellationToken);
        if (appUser is null)
        {
            return Unauthorized(new
            {
                error = "Sign in with ASP.NET Identity or provide a Discord bearer token to end events."
            });
        }

        var eventEntity = await _dbContext.Events
            .Include(evt => evt.AppUserEvents)
            .Include(evt => evt.EventLootDetails)
            .Include(evt => evt.Linkshell)
            .FirstOrDefaultAsync(evt => evt.Id == eventId, cancellationToken);

        if (eventEntity is null)
        {
            return NotFound(new { error = "The selected event was not found." });
        }

        var membership = await GetMembershipAsync(appUser.Id, eventEntity.LinkshellId, cancellationToken);
        if (!await CanAsync(membership, r => r.CanManageEvents, cancellationToken))
        {
            return Forbid();
        }

        var lootStructure = NormalizeLootStructure(eventEntity.Linkshell?.LootStructure ?? "Dkp");
        var isLootCouncil = lootStructure == "LootCouncil";
        var isHybrid = lootStructure == "Hybrid";
        var roundingStep = NormalizeDkpRounding(eventEntity.Linkshell?.DkpRoundingIncrement) == "Half" ? 0.5 : 0.25;
        var roundingMultiplier = 1d / roundingStep;

        var endTimeUtc = DateTime.UtcNow;
        var history = new EventHistory
        {
            LinkshellId = eventEntity.LinkshellId,
            EventName = eventEntity.EventName,
            EventType = eventEntity.EventType,
            EventLocation = eventEntity.EventLocation,
            StartDate = eventEntity.StartTime?.Date,
            StartTime = eventEntity.StartTime,
            EndTime = endTimeUtc,
            CommencementStartTime = eventEntity.CommencementStartTime,
            Duration = eventEntity.CommencementStartTime.HasValue
                ? (endTimeUtc - eventEntity.CommencementStartTime.Value).TotalHours
                : eventEntity.Duration,
            DkpPerHour = eventEntity.DkpPerHour,
            EventDkp = eventEntity.EventDkp,
            Details = eventEntity.Details,
            TimeStamp = DateTime.UtcNow,
            AppUserEventHistories = new List<AppUserEventHistory>()
        };

        var linkshellMemberships = await _dbContext.AppUserLinkshells
            .Where(link => link.LinkshellId == eventEntity.LinkshellId && link.AppUserId != null)
            .ToListAsync(cancellationToken);
        var membershipsByAppUserId = linkshellMemberships
            .Where(link => !string.IsNullOrWhiteSpace(link.AppUserId))
            .GroupBy(link => link.AppUserId!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        var participantsByCharacterName = eventEntity.AppUserEvents
            .Where(participation => !string.IsNullOrWhiteSpace(participation.CharacterName))
            .GroupBy(participation => participation.CharacterName!.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        var ledgerEntries = new List<DkpLedgerEntry>();
        var nextSequenceByAppUserId = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (var participation in eventEntity.AppUserEvents)
        {
            var durationHours = CalculateAccumulatedDurationHours(participation, endTimeUtc, eventEntity.CommencementStartTime);
            var roundedDuration = Math.Round(durationHours * roundingMultiplier) / roundingMultiplier;
            var eventDkp = isLootCouncil ? 0 : roundedDuration * (eventEntity.DkpPerHour ?? 0);

            participation.Duration = roundedDuration;
            participation.EventDkp = eventDkp;

            history.AppUserEventHistories.Add(new AppUserEventHistory
            {
                AppUserId = participation.AppUserId,
                CharacterName = participation.CharacterName,
                JobName = participation.JobName,
                SubJobName = participation.SubJobName,
                JobType = participation.JobType,
                StartTime = participation.StartTime,
                Duration = roundedDuration,
                EventDkp = eventDkp,
                IsQuickJoin = participation.IsQuickJoin,
                IsVerified = participation.IsVerified,
                Proctor = participation.Proctor
            });

            if (!string.IsNullOrWhiteSpace(participation.AppUserId) &&
                membershipsByAppUserId.TryGetValue(participation.AppUserId, out var linkshellMembership))
            {
                if (!isLootCouncil)
                {
                    linkshellMembership.LinkshellDkp = (linkshellMembership.LinkshellDkp ?? 0) + eventDkp;
                }
                nextSequenceByAppUserId[participation.AppUserId] = 2;
            }

            if (!isLootCouncil && !string.IsNullOrWhiteSpace(participation.AppUserId))
            {
                ledgerEntries.Add(new DkpLedgerEntry
                {
                    AppUserId = participation.AppUserId,
                    EventHistory = history,
                    LinkshellId = eventEntity.LinkshellId,
                    EntryType = "EventEarned",
                    Amount = eventDkp,
                    Sequence = 1,
                    OccurredAt = endTimeUtc,
                    CharacterName = participation.CharacterName,
                    EventName = eventEntity.EventName,
                    EventType = eventEntity.EventType,
                    EventLocation = eventEntity.EventLocation,
                    EventStartTime = eventEntity.StartTime,
                    EventEndTime = endTimeUtc,
                    Details = "DKP earned from completed event."
                });
            }
        }

        _dbContext.EventHistories.Add(history);
        if (!isLootCouncil)
        {
            foreach (var lootDetail in eventEntity.EventLootDetails.OrderBy(detail => detail.Id))
            {
                var rawValue = lootDetail.WinningDkpSpent.GetValueOrDefault();
                if (rawValue <= 0)
                {
                    continue;
                }

                var winnerMembership = ResolveLootWinnerMembership(
                    lootDetail.ItemWinner,
                    membershipsByAppUserId,
                    participantsByCharacterName,
                    linkshellMemberships);
                if (winnerMembership is null || string.IsNullOrWhiteSpace(winnerMembership.AppUserId))
                {
                    continue;
                }

                double amount;
                string lootDetailsText;
                if (isHybrid)
                {
                    var pct = Math.Clamp(rawValue, 0, 100);
                    var currentBalance = Math.Max(0, winnerMembership.LinkshellDkp ?? 0);
                    amount = -Math.Round(currentBalance * pct / 100d, 2);
                    lootDetailsText = $"Hybrid DKP spent ({pct}%) on loot: {lootDetail.ItemName ?? "Unknown item"}.";
                }
                else
                {
                    amount = -rawValue;
                    lootDetailsText = $"DKP spent on loot: {lootDetail.ItemName ?? "Unknown item"}.";
                }

                winnerMembership.LinkshellDkp = (winnerMembership.LinkshellDkp ?? 0) + amount;

                var currentSequence = nextSequenceByAppUserId.GetValueOrDefault(winnerMembership.AppUserId, 2);
                ledgerEntries.Add(new DkpLedgerEntry
                {
                    AppUserId = winnerMembership.AppUserId,
                    EventHistory = history,
                    LinkshellId = eventEntity.LinkshellId,
                    EntryType = "LootSpent",
                    Amount = amount,
                    Sequence = currentSequence,
                    OccurredAt = endTimeUtc,
                    CharacterName = winnerMembership.CharacterName,
                    EventName = eventEntity.EventName,
                    EventType = eventEntity.EventType,
                    EventLocation = eventEntity.EventLocation,
                    EventStartTime = eventEntity.StartTime,
                    EventEndTime = endTimeUtc,
                    ItemName = lootDetail.ItemName,
                    Details = lootDetailsText
                });
                nextSequenceByAppUserId[winnerMembership.AppUserId] = currentSequence + 1;
            }
        }

        _dbContext.DkpLedgerEntries.AddRange(ledgerEntries);
        _dbContext.EventLootDetails.RemoveRange(eventEntity.EventLootDetails);
        _dbContext.AppUserEvents.RemoveRange(eventEntity.AppUserEvents);

        var eventJobs = await _dbContext.Jobs.Where(job => job.EventId == eventId).ToListAsync(cancellationToken);
        _dbContext.Jobs.RemoveRange(eventJobs);
        _dbContext.Events.Remove(eventEntity);

        await _dbContext.SaveChangesAsync(cancellationToken);
        return Ok(new { success = true });
    }

    [HttpPost("events/{eventId:int}/cancel")]
    public async Task<IActionResult> CancelEventAsync(int eventId, CancellationToken cancellationToken)
    {
        var appUser = await ResolveAppUserAsync(cancellationToken);
        if (appUser is null)
        {
            return Unauthorized(new
            {
                error = "Sign in with ASP.NET Identity or provide a Discord bearer token to cancel events."
            });
        }

        var eventEntity = await _dbContext.Events
            .Include(evt => evt.Jobs)
            .Include(evt => evt.AppUserEvents)
            .Include(evt => evt.EventLootDetails)
            .FirstOrDefaultAsync(evt => evt.Id == eventId, cancellationToken);

        if (eventEntity is null)
        {
            return NotFound(new { error = "The selected event was not found." });
        }

        var membership = await GetMembershipAsync(appUser.Id, eventEntity.LinkshellId, cancellationToken);
        if (!await CanAsync(membership, r => r.CanManageEvents, cancellationToken))
        {
            return Forbid();
        }

        if (eventEntity.CommencementStartTime.HasValue)
        {
            return BadRequest(new { error = "Live events cannot be canceled. End the event instead." });
        }

        _dbContext.Jobs.RemoveRange(eventEntity.Jobs);
        _dbContext.AppUserEvents.RemoveRange(eventEntity.AppUserEvents);
        _dbContext.EventLootDetails.RemoveRange(eventEntity.EventLootDetails);
        _dbContext.Events.Remove(eventEntity);

        await _dbContext.SaveChangesAsync(cancellationToken);
        return Ok(new { success = true });
    }

    [HttpPost("events/{eventId:int}/unsign")]
    public async Task<IActionResult> UnsignAsync(int eventId, CancellationToken cancellationToken)
    {
        var appUser = await ResolveAppUserAsync(cancellationToken);
        if (appUser is null)
        {
            return Unauthorized(new
            {
                error = "Sign in with ASP.NET Identity or provide a Discord bearer token to unsign from events."
            });
        }

        var participation = await _dbContext.AppUserEvents
            .FirstOrDefaultAsync(item => item.EventId == eventId && item.AppUserId == appUser.Id, cancellationToken);

        if (participation is null)
        {
            return NotFound(new { error = "No signup was found for the current app user." });
        }

        var job = await _dbContext.Jobs
            .FirstOrDefaultAsync(item =>
                item.EventId == eventId &&
                item.JobName == participation.JobName &&
                item.SubJobName == participation.SubJobName,
                cancellationToken);

        if (job is not null)
        {
            var displayName = appUser.CharacterName ?? appUser.UserName ?? "Unknown";
            job.Enlisted.RemoveAll(name => name == participation.CharacterName || name == displayName);
            job.SignedUp = job.Enlisted.Count;
        }

        _dbContext.AppUserEvents.Remove(participation);
        await _dbContext.SaveChangesAsync(cancellationToken);

        return Ok(new { success = true });
    }

    [HttpGet("auctions")]
    public async Task<IActionResult> GetAuctionsAsync(int? linkshellId, CancellationToken cancellationToken)
    {
        var appUser = await ResolveAppUserAsync(cancellationToken);
        if (appUser is null)
        {
            return Unauthorized(new { error = "Sign in to load auctions." });
        }

        var accessibleLinkshellIds = await _dbContext.AppUserLinkshells
            .Where(link => link.AppUserId == appUser.Id)
            .Select(link => link.LinkshellId)
            .Distinct()
            .ToListAsync(cancellationToken);

        if (accessibleLinkshellIds.Count == 0)
        {
            return Ok(Array.Empty<ActivityAuctionDto>());
        }

        var selectedLinkshellId = linkshellId
            ?? appUser.PrimaryLinkshellId
            ?? accessibleLinkshellIds.First();

        if (!accessibleLinkshellIds.Contains(selectedLinkshellId))
        {
            return Forbid();
        }

        var nowUtc = DateTime.UtcNow;
        var auctions = await _dbContext.Auctions
            .Include(auction => auction.AuctionItems.OrderBy(item => item.Id))
                .ThenInclude(item => item.Bids.OrderByDescending(bid => bid.BidAmount).ThenBy(bid => bid.CreatedAt))
            .Where(auction => auction.LinkshellId == selectedLinkshellId)
            .OrderBy(auction => auction.StartTime)
            .ToListAsync(cancellationToken);

        return Ok(auctions.Select(auction => MapAuctionDto(auction, appUser.Id, nowUtc)).ToList());
    }

    [HttpGet("auction-history")]
    public async Task<IActionResult> GetAuctionHistoryAsync(int? linkshellId, CancellationToken cancellationToken)
    {
        var appUser = await ResolveAppUserAsync(cancellationToken);
        if (appUser is null)
        {
            return Unauthorized(new { error = "Sign in to load auction history." });
        }

        var accessibleLinkshellIds = await _dbContext.AppUserLinkshells
            .Where(link => link.AppUserId == appUser.Id)
            .Select(link => link.LinkshellId)
            .Distinct()
            .ToListAsync(cancellationToken);

        if (accessibleLinkshellIds.Count == 0)
        {
            return Ok(Array.Empty<ActivityAuctionHistoryDto>());
        }

        var selectedLinkshellId = linkshellId
            ?? appUser.PrimaryLinkshellId
            ?? accessibleLinkshellIds.First();

        if (!accessibleLinkshellIds.Contains(selectedLinkshellId))
        {
            return Forbid();
        }

        var history = await _dbContext.AuctionHistories
            .Include(item => item.AuctionItems.OrderBy(auctionItem => auctionItem.Id))
            .Where(item => item.LinkshellId == selectedLinkshellId)
            .OrderByDescending(item => item.ClosedAt)
            .Take(25)
            .ToListAsync(cancellationToken);

        return Ok(history.Select(MapAuctionHistoryDto).ToList());
    }

    [HttpGet("auction-items/{itemId:int}/bids")]
    public async Task<IActionResult> GetAuctionItemBidsAsync(int itemId, CancellationToken cancellationToken)
    {
        var appUser = await ResolveAppUserAsync(cancellationToken);
        if (appUser is null)
        {
            return Unauthorized(new { error = "Sign in to load bid history." });
        }

        var auctionItem = await _dbContext.AuctionItems
            .Include(item => item.Auction)
            .Include(item => item.Bids.OrderByDescending(bid => bid.BidAmount).ThenBy(bid => bid.CreatedAt))
            .FirstOrDefaultAsync(item => item.Id == itemId, cancellationToken);

        if (auctionItem is null || auctionItem.Auction is null)
        {
            return NotFound(new { error = "Auction item not found." });
        }

        var membership = await GetMembershipAsync(appUser.Id, auctionItem.Auction.LinkshellId, cancellationToken);
        if (membership is null)
        {
            return Forbid();
        }

        return Ok(auctionItem.Bids.Select(bid => new ActivityAuctionBidDto(
            bid.Id,
            bid.CharacterName,
            bid.BidAmount,
            bid.CreatedAt)).ToList());
    }

    [HttpPost("auctions")]
    public async Task<IActionResult> CreateAuctionAsync(
        [FromBody] ActivityCreateAuctionRequest request,
        CancellationToken cancellationToken)
    {
        var appUser = await ResolveAppUserAsync(cancellationToken);
        if (appUser is null)
        {
            return Unauthorized(new { error = "Sign in to create auctions." });
        }

        var membership = await GetMembershipAsync(appUser.Id, request.LinkshellId, cancellationToken);
        if (membership is null)
        {
            return Forbid();
        }

        if (!TryConvertUserTimeZoneToUtc(request.StartTimeLocal, appUser.TimeZone, out var startTimeUtc) ||
            !TryConvertUserTimeZoneToUtc(request.EndTimeLocal, appUser.TimeZone, out var endTimeUtc))
        {
            return BadRequest(new { error = "Auction start and end times must be valid local date/time values." });
        }

        var normalizedItems = NormalizeAuctionItems(request.Items);
        var validationError = ValidateAuctionRequest(request.Title, startTimeUtc, endTimeUtc, normalizedItems);
        if (validationError is not null)
        {
            return BadRequest(new { error = validationError });
        }

        var auction = new Auction
        {
            LinkshellId = request.LinkshellId,
            AuctionTitle = request.Title.Trim(),
            CreatedBy = appUser.CharacterName ?? appUser.UserName ?? "User",
            CreatedByUserId = appUser.Id,
            StartTime = startTimeUtc,
            EndTime = endTimeUtc,
            StartedAt = null,
            AuctionItems = normalizedItems.Select(item => new AuctionItem
            {
                ItemName = item.ItemName?.Trim(),
                ItemType = item.ItemType?.Trim(),
                StartingBidDkp = item.StartingBidDkp,
                CurrentHighestBid = null,
                CurrentHighestBidder = null,
                CurrentHighestBidderAppUserId = null,
                EndingBidDkp = null,
                StartTime = startTimeUtc,
                EndTime = endTimeUtc,
                Status = "Pending",
                Notes = item.Notes?.Trim(),
                SourceItemId = item.SourceItemId
            }).ToList()
        };

        _dbContext.Auctions.Add(auction);
        await _dbContext.SaveChangesAsync(cancellationToken);

        return Ok(MapAuctionDto(auction, appUser.Id, DateTime.UtcNow));
    }

    [HttpPost("auctions/{auctionId:int}/update")]
    public async Task<IActionResult> UpdateAuctionAsync(
        int auctionId,
        [FromBody] ActivityCreateAuctionRequest request,
        CancellationToken cancellationToken)
    {
        var appUser = await ResolveAppUserAsync(cancellationToken);
        if (appUser is null)
        {
            return Unauthorized(new { error = "Sign in to update auctions." });
        }

        var auction = await _dbContext.Auctions
            .Include(item => item.AuctionItems.OrderBy(auctionItem => auctionItem.Id))
                .ThenInclude(item => item.Bids)
            .FirstOrDefaultAsync(item => item.Id == auctionId, cancellationToken);

        if (auction is null)
        {
            return NotFound(new { error = "Auction not found." });
        }

        if (!CanEditAuction(appUser.Id, auction, DateTime.UtcNow))
        {
            return Forbid();
        }

        var membership = await GetMembershipAsync(appUser.Id, request.LinkshellId, cancellationToken);
        if (membership is null)
        {
            return Forbid();
        }

        if (!TryConvertUserTimeZoneToUtc(request.StartTimeLocal, appUser.TimeZone, out var startTimeUtc) ||
            !TryConvertUserTimeZoneToUtc(request.EndTimeLocal, appUser.TimeZone, out var endTimeUtc))
        {
            return BadRequest(new { error = "Auction start and end times must be valid local date/time values." });
        }

        var normalizedItems = NormalizeAuctionItems(request.Items);
        var validationError = ValidateAuctionRequest(request.Title, startTimeUtc, endTimeUtc, normalizedItems);
        if (validationError is not null)
        {
            return BadRequest(new { error = validationError });
        }

        auction.LinkshellId = request.LinkshellId;
        auction.AuctionTitle = request.Title.Trim();
        auction.StartTime = startTimeUtc;
        auction.EndTime = endTimeUtc;

        var remainingItems = auction.AuctionItems.ToDictionary(item => item.Id);
        foreach (var itemRequest in normalizedItems)
        {
            if (itemRequest.Id > 0 && remainingItems.TryGetValue(itemRequest.Id, out var existingItem))
            {
                existingItem.ItemName = itemRequest.ItemName?.Trim();
                existingItem.ItemType = itemRequest.ItemType?.Trim();
                existingItem.StartingBidDkp = itemRequest.StartingBidDkp;
                existingItem.StartTime = startTimeUtc;
                existingItem.EndTime = endTimeUtc;
                existingItem.Notes = itemRequest.Notes?.Trim();
                existingItem.SourceItemId = itemRequest.SourceItemId;
                remainingItems.Remove(itemRequest.Id);
                continue;
            }

            auction.AuctionItems.Add(new AuctionItem
            {
                ItemName = itemRequest.ItemName?.Trim(),
                ItemType = itemRequest.ItemType?.Trim(),
                StartingBidDkp = itemRequest.StartingBidDkp,
                CurrentHighestBid = null,
                CurrentHighestBidder = null,
                CurrentHighestBidderAppUserId = null,
                EndingBidDkp = null,
                StartTime = startTimeUtc,
                EndTime = endTimeUtc,
                Status = "Pending",
                Notes = itemRequest.Notes?.Trim(),
                SourceItemId = itemRequest.SourceItemId
            });
        }

        if (remainingItems.Count > 0)
        {
            if (remainingItems.Values.Any(item => item.Bids.Count > 0))
            {
                return BadRequest(new { error = "Items that already have bids can't be removed from a live auction." });
            }
            _dbContext.AuctionItems.RemoveRange(remainingItems.Values);
        }

        await _dbContext.SaveChangesAsync(cancellationToken);
        return Ok(MapAuctionDto(auction, appUser.Id, DateTime.UtcNow));
    }

    [HttpPost("auctions/{auctionId:int}/start")]
    public async Task<IActionResult> StartAuctionAsync(int auctionId, CancellationToken cancellationToken)
    {
        var appUser = await ResolveAppUserAsync(cancellationToken);
        if (appUser is null)
        {
            return Unauthorized(new { error = "Sign in to start auctions." });
        }

        var auction = await _dbContext.Auctions
            .Include(item => item.AuctionItems)
            .FirstOrDefaultAsync(item => item.Id == auctionId, cancellationToken);

        if (auction is null)
        {
            return NotFound(new { error = "Auction not found." });
        }

        if (!CanStartAuction(appUser.Id, auction, DateTime.UtcNow))
        {
            return Forbid();
        }

        var startedAt = DateTime.UtcNow;
        var plannedDuration = ResolveAuctionDuration(auction, startedAt);
        auction.StartedAt = startedAt;
        auction.EndTime = startedAt.Add(plannedDuration);
        foreach (var item in auction.AuctionItems)
        {
            item.StartTime = startedAt;
            item.EndTime = auction.EndTime;
            item.Status = "Live";
        }

        await _dbContext.SaveChangesAsync(cancellationToken);
        return Ok(MapAuctionDto(auction, appUser.Id, DateTime.UtcNow));
    }

    [HttpPost("auction-items/{itemId:int}/bid")]
    public async Task<IActionResult> MakeAuctionBidAsync(
        int itemId,
        [FromBody] ActivityAuctionBidRequest request,
        CancellationToken cancellationToken)
    {
        var appUser = await ResolveAppUserAsync(cancellationToken);
        if (appUser is null)
        {
            return Unauthorized(new { error = "Sign in to bid on auctions." });
        }

        var auctionItem = await _dbContext.AuctionItems
            .Include(item => item.Auction)
            .Include(item => item.Bids)
            .FirstOrDefaultAsync(item => item.Id == itemId, cancellationToken);

        if (auctionItem is null || auctionItem.Auction is null)
        {
            return NotFound(new { error = "Auction item not found." });
        }

        var membership = await GetMembershipAsync(appUser.Id, auctionItem.Auction.LinkshellId, cancellationToken);
        if (membership is null)
        {
            return Forbid();
        }

        var nowUtc = DateTime.UtcNow;
        if (!IsAuctionLive(auctionItem.Auction, nowUtc))
        {
            return BadRequest(new { error = "This auction has not started yet." });
        }

        if (HasAuctionEnded(auctionItem.Auction, nowUtc))
        {
            return BadRequest(new { error = "This auction has already ended." });
        }

        var bidAmount = request.BidAmount;
        var minimumBid = Math.Max(auctionItem.StartingBidDkp ?? 0, auctionItem.CurrentHighestBid ?? 0);
        if (bidAmount <= minimumBid)
        {
            return BadRequest(new { error = $"Bid amount must be greater than {minimumBid}." });
        }

        if (bidAmount > (membership.LinkshellDkp ?? 0))
        {
            return BadRequest(new { error = "You cannot bid more DKP than you currently have." });
        }

        var bid = new Bid
        {
            AuctionItemId = itemId,
            AppUserId = appUser.Id,
            CharacterName = appUser.CharacterName ?? appUser.UserName ?? "User",
            BidAmount = bidAmount,
            CreatedAt = nowUtc
        };

        auctionItem.Bids.Add(bid);
        auctionItem.CurrentHighestBid = bidAmount;
        auctionItem.CurrentHighestBidder = bid.CharacterName;
        auctionItem.CurrentHighestBidderAppUserId = appUser.Id;
        auctionItem.Status = "BidPlaced";

        await _dbContext.SaveChangesAsync(cancellationToken);
        return Ok(new ActivityAuctionBidDto(bid.Id, bid.CharacterName, bid.BidAmount, bid.CreatedAt));
    }

    [HttpPost("auctions/{auctionId:int}/close")]
    public async Task<IActionResult> CloseAuctionAsync(
        int auctionId,
        [FromBody] ActivityCloseAuctionRequest? request,
        CancellationToken cancellationToken)
    {
        var appUser = await ResolveAppUserAsync(cancellationToken);
        if (appUser is null)
        {
            return Unauthorized(new { error = "Sign in to close auctions." });
        }

        var auction = await _dbContext.Auctions
            .Include(item => item.AuctionItems)
                .ThenInclude(item => item.Bids)
            .FirstOrDefaultAsync(item => item.Id == auctionId, cancellationToken);

        if (auction is null)
        {
            return NotFound(new { error = "Auction not found." });
        }

        if (!IsAuctionCreator(appUser.Id, auction))
        {
            return Forbid();
        }

        if (!HasAuctionStarted(auction, DateTime.UtcNow))
        {
            return BadRequest(new { error = "An auction must be started before it can be closed." });
        }

        if (!HasAuctionEnded(auction, DateTime.UtcNow))
        {
            return BadRequest(new { error = "An auction can only be closed after its timer has run out." });
        }

        var deliveredIds = (request?.DeliveredItemIds ?? Array.Empty<int>()).ToHashSet();

        var closedAt = DateTime.UtcNow;
        var history = new AuctionHistory
        {
            LinkshellId = auction.LinkshellId,
            AuctionTitle = auction.AuctionTitle,
            CreatedBy = auction.CreatedBy,
            CreatedByUserId = auction.CreatedByUserId,
            StartTime = auction.StartTime,
            EndTime = auction.EndTime,
            StartedAt = auction.StartedAt,
            ClosedAt = closedAt,
            AuctionItems = auction.AuctionItems
                .OrderBy(item => item.Id)
                .Select(item =>
                {
                    var hasWinner = !string.IsNullOrWhiteSpace(item.CurrentHighestBidderAppUserId);
                    var delivered = hasWinner && deliveredIds.Contains(item.Id);
                    return new AuctionItem
                    {
                        ItemName = item.ItemName,
                        ItemType = item.ItemType,
                        StartingBidDkp = item.StartingBidDkp,
                        CurrentHighestBid = item.CurrentHighestBid,
                        CurrentHighestBidder = item.CurrentHighestBidder,
                        CurrentHighestBidderAppUserId = item.CurrentHighestBidderAppUserId,
                        EndingBidDkp = item.CurrentHighestBid,
                        StartTime = item.StartTime,
                        EndTime = item.EndTime,
                        Status = !hasWinner ? "NoBids" : delivered ? "Received" : "Closed",
                        Notes = item.Notes,
                        SourceItemId = item.SourceItemId
                    };
                })
                .ToList()
        };

        _dbContext.AuctionHistories.Add(history);

        var sourceItemIds = auction.AuctionItems
            .Where(item => item.SourceItemId.HasValue && deliveredIds.Contains(item.Id) && !string.IsNullOrWhiteSpace(item.CurrentHighestBidderAppUserId))
            .Select(item => item.SourceItemId!.Value)
            .Distinct()
            .ToList();
        var inventoryItems = sourceItemIds.Count == 0
            ? new List<Item>()
            : await _dbContext.Items
                .Where(inv => sourceItemIds.Contains(inv.Id) && inv.LinkshellId == auction.LinkshellId)
                .ToListAsync(cancellationToken);
        foreach (var auctionItem in auction.AuctionItems.Where(item =>
                     item.SourceItemId.HasValue &&
                     deliveredIds.Contains(item.Id) &&
                     !string.IsNullOrWhiteSpace(item.CurrentHighestBidderAppUserId)))
        {
            var inv = inventoryItems.FirstOrDefault(candidate => candidate.Id == auctionItem.SourceItemId!.Value);
            if (inv is null) continue;
            inv.Quantity = Math.Max(0, inv.Quantity - 1);
            inv.UpdatedAt = closedAt;
            if (inv.Quantity == 0)
            {
                _dbContext.Items.Remove(inv);
            }
        }

        foreach (var item in auction.AuctionItems.Where(item =>
                     !string.IsNullOrWhiteSpace(item.CurrentHighestBidderAppUserId) &&
                     item.CurrentHighestBid.HasValue &&
                     item.CurrentHighestBid.Value > 0))
        {
            var winnerMembership = await _dbContext.AppUserLinkshells
                .FirstOrDefaultAsync(link =>
                    link.AppUserId == item.CurrentHighestBidderAppUserId &&
                    link.LinkshellId == auction.LinkshellId,
                    cancellationToken);

            if (winnerMembership is null)
            {
                continue;
            }

            var winningBid = item.CurrentHighestBid.GetValueOrDefault();
            winnerMembership.LinkshellDkp = (winnerMembership.LinkshellDkp ?? 0) - winningBid;
            _dbContext.DkpLedgerEntries.Add(new DkpLedgerEntry
            {
                AppUserId = winnerMembership.AppUserId,
                LinkshellId = auction.LinkshellId,
                EntryType = "AuctionSpent",
                Amount = -winningBid,
                Sequence = 1,
                OccurredAt = closedAt,
                CharacterName = winnerMembership.CharacterName,
                EventName = auction.AuctionTitle,
                EventStartTime = auction.StartedAt ?? auction.StartTime,
                EventEndTime = auction.EndTime ?? closedAt,
                ItemName = item.ItemName,
                Details = $"Auction spend from {auction.AuctionTitle ?? "auction"}."
            });
        }

        _dbContext.Bids.RemoveRange(auction.AuctionItems.SelectMany(item => item.Bids));
        _dbContext.AuctionItems.RemoveRange(auction.AuctionItems);
        _dbContext.Auctions.Remove(auction);
        await _dbContext.SaveChangesAsync(cancellationToken);

        return Ok(new { success = true });
    }

    [HttpPost("auction-history/items/{itemId:int}/received")]
    public async Task<IActionResult> MarkAuctionHistoryItemReceivedAsync(int itemId, CancellationToken cancellationToken)
    {
        var appUser = await ResolveAppUserAsync(cancellationToken);
        if (appUser is null)
        {
            return Unauthorized(new { error = "Sign in to update auction history." });
        }

        var item = await _dbContext.AuctionItems
            .Include(auctionItem => auctionItem.AuctionHistory)
            .FirstOrDefaultAsync(auctionItem => auctionItem.Id == itemId, cancellationToken);

        if (item is null || item.AuctionHistory is null)
        {
            return NotFound(new { error = "Auction history item not found." });
        }

        var membership = await GetMembershipAsync(appUser.Id, item.AuctionHistory.LinkshellId, cancellationToken);
        if (membership is null)
        {
            return Forbid();
        }

        item.Status = "Received";
        await _dbContext.SaveChangesAsync(cancellationToken);
        return Ok(new { success = true });
    }

    [HttpPost("auction-history/items/{itemId:int}/undo")]
    public async Task<IActionResult> UndoAuctionHistoryItemStatusAsync(int itemId, CancellationToken cancellationToken)
    {
        var appUser = await ResolveAppUserAsync(cancellationToken);
        if (appUser is null)
        {
            return Unauthorized(new { error = "Sign in to update auction history." });
        }

        var item = await _dbContext.AuctionItems
            .Include(auctionItem => auctionItem.AuctionHistory)
            .FirstOrDefaultAsync(auctionItem => auctionItem.Id == itemId, cancellationToken);

        if (item is null || item.AuctionHistory is null)
        {
            return NotFound(new { error = "Auction history item not found." });
        }

        var membership = await GetMembershipAsync(appUser.Id, item.AuctionHistory.LinkshellId, cancellationToken);
        if (membership is null)
        {
            return Forbid();
        }

        item.Status = string.Equals(item.Status, "Received", StringComparison.OrdinalIgnoreCase) ? "Closed" : "Pending";
        if (string.IsNullOrWhiteSpace(item.CurrentHighestBidderAppUserId))
        {
            item.Status = "NoBids";
        }

        await _dbContext.SaveChangesAsync(cancellationToken);
        return Ok(new { success = true });
    }

    private static List<ActivityAuctionItemInput> NormalizeAuctionItems(IReadOnlyList<ActivityAuctionItemInput>? items)
    {
        return (items ?? Array.Empty<ActivityAuctionItemInput>())
            .Where(item => !string.IsNullOrWhiteSpace(item.ItemName) || item.StartingBidDkp.HasValue)
            .ToList();
    }

    internal static List<TodLootDetail> NormalizeTodLootDetails(IReadOnlyList<ActivityCreateTodLootRequest>? lootDetails)
    {
        return (lootDetails ?? Array.Empty<ActivityCreateTodLootRequest>())
            .Where(detail =>
                !string.IsNullOrWhiteSpace(detail.ItemName) ||
                !string.IsNullOrWhiteSpace(detail.ItemWinner) ||
                detail.WinningDkpSpent.HasValue)
            .Select(detail => new TodLootDetail
            {
                ItemName = detail.ItemName?.Trim(),
                ItemWinner = detail.ItemWinner?.Trim(),
                WinningDkpSpent = detail.WinningDkpSpent
            })
            .ToList();
    }

    // Refactored from instance method to static so AddonApiController can
    // share the same DKP-ledger logic without depending on an
    // ActivityDataController instance. _dbContext references became the
    // explicit `dbContext` parameter; behavior is otherwise identical.
    internal static async Task AdjustTodLootDkpAsync(
        ApplicationDbContext dbContext,
        Tod tod,
        IReadOnlyList<TodLootDetail> lootDetails,
        DateTime occurredAtUtc,
        bool isRefund,
        CancellationToken cancellationToken)
    {
        var actionableLoot = lootDetails
            .Where(detail => !string.IsNullOrWhiteSpace(detail.ItemWinner) && detail.WinningDkpSpent.GetValueOrDefault() > 0)
            .ToList();
        if (actionableLoot.Count == 0)
        {
            return;
        }

        var linkshell = tod.Linkshell ?? await dbContext.Linkshells
            .FirstOrDefaultAsync(ls => ls.Id == tod.LinkshellId, cancellationToken);

        var structure = NormalizeLootStructure(linkshell?.LootStructure ?? "Dkp");
        if (structure == "LootCouncil")
        {
            // Loot council linkshells skip DKP math entirely.
            return;
        }
        var isHybrid = structure == "Hybrid";

        var winnerNames = actionableLoot
            .Select(detail => detail.ItemWinner!.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var memberships = await dbContext.AppUserLinkshells
            .Where(link => link.LinkshellId == tod.LinkshellId && link.AppUserId != null && winnerNames.Contains(link.CharacterName!))
            .ToListAsync(cancellationToken);

        var membershipsByCharacterName = memberships
            .Where(link => !string.IsNullOrWhiteSpace(link.CharacterName) && !string.IsNullOrWhiteSpace(link.AppUserId))
            .GroupBy(link => link.CharacterName!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        if (membershipsByCharacterName.Count == 0)
        {
            return;
        }

        var appUserIds = membershipsByCharacterName.Values
            .Select(link => link.AppUserId!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var nextSequenceByAppUserId = appUserIds.Count == 0
            ? new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
            : await dbContext.DkpLedgerEntries
                .Where(entry => entry.LinkshellId == tod.LinkshellId && entry.AppUserId != null && appUserIds.Contains(entry.AppUserId))
                .GroupBy(entry => entry.AppUserId!)
                .Select(group => new { AppUserId = group.Key, NextSequence = group.Max(entry => entry.Sequence) + 1 })
                .ToDictionaryAsync(item => item.AppUserId, item => item.NextSequence, StringComparer.OrdinalIgnoreCase, cancellationToken);

        var ledgerEntries = new List<DkpLedgerEntry>();
        foreach (var detail in actionableLoot)
        {
            if (!membershipsByCharacterName.TryGetValue(detail.ItemWinner!.Trim(), out var winnerMembership) || string.IsNullOrWhiteSpace(winnerMembership.AppUserId))
            {
                continue;
            }

            var rawValue = detail.WinningDkpSpent.GetValueOrDefault();
            double amount;
            string detailsText;
            if (isHybrid)
            {
                var pct = Math.Clamp((double)rawValue, 0, 100);
                var currentBalance = Math.Max(0, winnerMembership.LinkshellDkp ?? 0);
                if (isRefund)
                {
                    if (detail.ActualDeductedDkp.HasValue)
                    {
                        amount = detail.ActualDeductedDkp.Value;
                        detailsText = $"Refunded Hybrid DKP ({pct}%, {amount:0.##} DKP) for removed ToD loot on {tod.MonsterName ?? "Unknown monster"}.";
                    }
                    else
                    {
                        // Legacy approximation when the deducted amount wasn't stored.
                        if (pct >= 100d)
                        {
                            continue;
                        }
                        amount = Math.Round(currentBalance * pct / (100d - pct), 2);
                        detailsText = $"Refunded Hybrid DKP ({pct}%) for removed ToD loot on {tod.MonsterName ?? "Unknown monster"}.";
                    }
                }
                else
                {
                    amount = -Math.Round(currentBalance * pct / 100d, 2);
                    detail.ActualDeductedDkp = Math.Abs(amount);
                    detailsText = $"Hybrid DKP spent ({pct}%, {Math.Abs(amount):0.##} DKP) on ToD loot from {tod.MonsterName ?? "Unknown monster"}.";
                }
            }
            else
            {
                if (isRefund)
                {
                    amount = detail.ActualDeductedDkp ?? (double)rawValue;
                    detailsText = $"Refunded DKP for deleted ToD loot on {tod.MonsterName ?? "Unknown monster"}.";
                }
                else
                {
                    amount = -(double)rawValue;
                    detail.ActualDeductedDkp = Math.Abs(amount);
                    detailsText = $"DKP spent on ToD loot from {tod.MonsterName ?? "Unknown monster"}.";
                }
            }

            winnerMembership.LinkshellDkp = (winnerMembership.LinkshellDkp ?? 0d) + amount;

            var currentSequence = nextSequenceByAppUserId.GetValueOrDefault(winnerMembership.AppUserId, 1);
            nextSequenceByAppUserId[winnerMembership.AppUserId] = currentSequence + 1;

            ledgerEntries.Add(new DkpLedgerEntry
            {
                AppUserId = winnerMembership.AppUserId,
                LinkshellId = tod.LinkshellId,
                EntryType = isRefund ? "LootRefund" : "LootSpent",
                Amount = amount,
                Sequence = currentSequence,
                OccurredAt = occurredAtUtc,
                CharacterName = winnerMembership.CharacterName,
                ItemName = detail.ItemName,
                Details = detailsText
            });
        }

        if (ledgerEntries.Count > 0)
        {
            await dbContext.DkpLedgerEntries.AddRangeAsync(ledgerEntries, cancellationToken);
        }
    }

    private static ActivityTodDto MapTodDto(Tod tod)
    {
        return new ActivityTodDto(
            tod.Id,
            tod.LinkshellId,
            tod.MonsterName ?? "Unknown monster",
            tod.DayNumber,
            tod.Time,
            tod.Claim,
            tod.Cooldown,
            tod.RepopTime,
            tod.Interval,
            tod.TodLootDetails.Count,
            tod.TodLootDetails
                .OrderBy(detail => detail.Id)
                .Select(detail => new ActivityTodLootDto(
                    detail.Id,
                    detail.ItemName,
                    detail.ItemWinner,
                    detail.WinningDkpSpent))
                .ToList(),
            tod.ImagePath);
    }

    internal static double ResolveTodCooldownHours(string? cooldown)
    {
        if (string.IsNullOrWhiteSpace(cooldown))
        {
            return 22d;
        }

        if (SupportedTodCooldowns.Contains(cooldown.Trim()))
        {
            return string.Equals(cooldown.Trim(), TodManagerViewModel.SeventyTwoHourCooldown, StringComparison.OrdinalIgnoreCase)
                ? 72d
                : 22d;
        }

        var match = System.Text.RegularExpressions.Regex.Match(cooldown.Trim(), @"^\s*(\d+(?:\.\d+)?)\s*(?:Hours?|Hr|H)?\s*$",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (match.Success && double.TryParse(match.Groups[1].Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var hours) && hours > 0)
        {
            return hours;
        }

        return 22d;
    }

    private static bool IsAcceptableTodCooldown(string? cooldown)
    {
        if (string.IsNullOrWhiteSpace(cooldown))
        {
            return false;
        }

        if (SupportedTodCooldowns.Contains(cooldown.Trim()))
        {
            return true;
        }

        var match = System.Text.RegularExpressions.Regex.Match(cooldown.Trim(), @"^\s*(\d+(?:\.\d+)?)\s*(?:Hours?|Hr|H)?\s*$",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return match.Success
            && double.TryParse(match.Groups[1].Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var hours)
            && hours > 0;
    }

    private static string? SanitizeUploadedImagePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        var trimmed = path.Trim();
        if (!trimmed.StartsWith("/uploads/tods/", StringComparison.Ordinal))
        {
            return null;
        }

        if (trimmed.Contains("..", StringComparison.Ordinal))
        {
            return null;
        }

        return trimmed;
    }

    internal static string GetDefaultTodCooldown(string? monsterName)
    {
        if (string.IsNullOrWhiteSpace(monsterName))
        {
            return TodManagerViewModel.TwentyTwoHourCooldown;
        }
        var trimmed = monsterName.Trim();
        if (HnmConfig.LongWindowHnms.Contains(trimmed))
        {
            return TodManagerViewModel.SeventyTwoHourCooldown;
        }
        if (HnmConfig.SkyFarmNms.Contains(trimmed))
        {
            return TodManagerViewModel.TwoHourCooldown;
        }
        return TodManagerViewModel.TwentyTwoHourCooldown;
    }

    internal static string GetDefaultTodInterval(string? monsterName)
    {
        return !string.IsNullOrWhiteSpace(monsterName) && HnmConfig.LongWindowHnms.Contains(monsterName.Trim())
            ? TodManagerViewModel.OneHourInterval
            : TodManagerViewModel.TenMinuteInterval;
    }

    private static string? ValidateAuctionRequest(
        string? title,
        DateTime? startTimeUtc,
        DateTime? endTimeUtc,
        IReadOnlyList<ActivityAuctionItemInput> items)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return "Auction title is required.";
        }

        if (!startTimeUtc.HasValue)
        {
            return "Auction start time is required.";
        }

        if (!endTimeUtc.HasValue)
        {
            return "Auction end time is required.";
        }

        if (endTimeUtc <= startTimeUtc)
        {
            return "Auction end time must be after its start time.";
        }

        if (items.Count == 0)
        {
            return "Add at least one auction item.";
        }

        foreach (var item in items)
        {
            if (string.IsNullOrWhiteSpace(item.ItemName))
            {
                return "Each auction item needs a name.";
            }

            if (!item.StartingBidDkp.HasValue || item.StartingBidDkp < 0)
            {
                return "Each auction item needs a starting bid of 0 or higher.";
            }
        }

        return null;
    }

    private static bool IsAuctionCreator(string currentUserId, Auction auction)
    {
        return string.Equals(auction.CreatedByUserId, currentUserId, StringComparison.OrdinalIgnoreCase);
    }

    private static bool CanEditAuction(string currentUserId, Auction auction, DateTime referenceUtc)
    {
        return IsAuctionCreator(currentUserId, auction)
               && !HasAuctionEnded(auction, referenceUtc);
    }

    private static bool CanStartAuction(string currentUserId, Auction auction, DateTime referenceUtc)
    {
        return IsAuctionCreator(currentUserId, auction)
               && !auction.StartedAt.HasValue
               && (!auction.StartTime.HasValue || referenceUtc < auction.StartTime.Value)
               && !HasAuctionEnded(auction, referenceUtc);
    }

    private static bool HasAuctionStarted(Auction auction, DateTime referenceUtc)
    {
        return auction.StartedAt.HasValue
               || (auction.StartTime.HasValue && referenceUtc >= auction.StartTime.Value);
    }

    private static bool HasAuctionEnded(Auction auction, DateTime referenceUtc)
    {
        return auction.EndTime.HasValue && referenceUtc >= auction.EndTime.Value;
    }

    private static bool IsAuctionLive(Auction auction, DateTime referenceUtc)
    {
        return HasAuctionStarted(auction, referenceUtc) && !HasAuctionEnded(auction, referenceUtc);
    }

    private static TimeSpan ResolveAuctionDuration(Auction auction, DateTime referenceUtc)
    {
        if (auction.StartTime.HasValue && auction.EndTime.HasValue && auction.EndTime > auction.StartTime)
        {
            return auction.EndTime.Value - auction.StartTime.Value;
        }

        if (auction.EndTime.HasValue && auction.EndTime > referenceUtc)
        {
            return auction.EndTime.Value - referenceUtc;
        }

        return TimeSpan.Zero;
    }

    private static ActivityAuctionDto MapAuctionDto(Auction auction, string currentUserId, DateTime nowUtc)
    {
        var isCreator = IsAuctionCreator(currentUserId, auction);
        var status = HasAuctionEnded(auction, nowUtc)
            ? "Ended"
            : HasAuctionStarted(auction, nowUtc)
                ? "Live"
                : "Pending";

        return new ActivityAuctionDto(
            auction.Id,
            auction.LinkshellId,
            auction.AuctionTitle,
            auction.CreatedBy,
            auction.StartTime,
            auction.EndTime,
            auction.StartedAt,
            status,
            CanEditAuction(currentUserId, auction, nowUtc),
            CanStartAuction(currentUserId, auction, nowUtc),
            isCreator && auction.StartedAt.HasValue && (!auction.EndTime.HasValue || nowUtc >= auction.EndTime.Value),
            auction.AuctionItems
                .OrderBy(item => item.Id)
                .Select(item => new ActivityAuctionItemDto(
                    item.Id,
                    item.ItemName,
                    item.ItemType,
                    item.StartingBidDkp,
                    item.CurrentHighestBid,
                    item.CurrentHighestBidder,
                    item.CurrentHighestBidderAppUserId,
                    item.StartTime,
                    item.EndTime,
                    item.Status,
                    item.Notes,
                    item.Bids.Count,
                    item.SourceItemId))
                .ToList());
    }

    private static ActivityAuctionHistoryDto MapAuctionHistoryDto(AuctionHistory history)
    {
        return new ActivityAuctionHistoryDto(
            history.Id,
            history.LinkshellId,
            history.AuctionTitle,
            history.CreatedBy,
            history.StartTime,
            history.EndTime,
            history.StartedAt,
            history.ClosedAt,
            history.AuctionItems
                .OrderBy(item => item.Id)
                .Select(item => new ActivityAuctionItemDto(
                    item.Id,
                    item.ItemName,
                    item.ItemType,
                    item.StartingBidDkp,
                    item.CurrentHighestBid,
                    item.CurrentHighestBidder,
                    item.CurrentHighestBidderAppUserId,
                    item.StartTime,
                    item.EndTime,
                    item.Status,
                    item.Notes,
                    0,
                    item.SourceItemId))
                .ToList());
    }

    [HttpPost("linkshells/{linkshellId:int}/rules")]
    public async Task<IActionResult> CreateRuleAsync(
        int linkshellId,
        [FromBody] ActivityCreateRuleRequest request,
        CancellationToken cancellationToken)
    {
        var title = request.Title?.Trim() ?? string.Empty;
        var details = request.Details?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(title))
        {
            return BadRequest(new { error = "Rule title is required." });
        }
        if (string.IsNullOrWhiteSpace(details))
        {
            return BadRequest(new { error = "Rule details are required." });
        }

        var appUser = await ResolveAppUserAsync(cancellationToken);
        if (appUser is null)
        {
            return Unauthorized(new
            {
                error = "Sign in with ASP.NET Identity or provide a Discord bearer token to create rules."
            });
        }

        var membership = await GetMembershipAsync(appUser.Id, linkshellId, cancellationToken);
        if (!await CanAsync(membership, r => r.CanManageRules, cancellationToken))
        {
            return Forbid();
        }

        var linkshell = await _dbContext.Linkshells.FirstOrDefaultAsync(item => item.Id == linkshellId, cancellationToken);
        if (linkshell is null)
        {
            return NotFound(new { error = "The selected linkshell was not found." });
        }

        var rule = new Rule
        {
            LinkshellId = linkshellId,
            LinkshellName = linkshell.LinkshellName,
            RuleTitle = title,
            RuleDetails = details,
            CreatedByAppUserId = appUser.Id,
            CreatedByCharacterName = membership!.CharacterName ?? appUser.CharacterName,
            CreatedAt = DateTime.UtcNow
        };
        _dbContext.Rules.Add(rule);
        await _dbContext.SaveChangesAsync(cancellationToken);
        return Ok(new { success = true, id = rule.Id });
    }

    [HttpPost("rules/{ruleId:int}/update")]
    public async Task<IActionResult> UpdateRuleAsync(
        int ruleId,
        [FromBody] ActivityCreateRuleRequest request,
        CancellationToken cancellationToken)
    {
        var title = request.Title?.Trim() ?? string.Empty;
        var details = request.Details?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(title))
        {
            return BadRequest(new { error = "Rule title is required." });
        }
        if (string.IsNullOrWhiteSpace(details))
        {
            return BadRequest(new { error = "Rule details are required." });
        }

        var appUser = await ResolveAppUserAsync(cancellationToken);
        if (appUser is null)
        {
            return Unauthorized(new
            {
                error = "Sign in with ASP.NET Identity or provide a Discord bearer token to update rules."
            });
        }

        var rule = await _dbContext.Rules.FirstOrDefaultAsync(item => item.Id == ruleId, cancellationToken);
        if (rule is null)
        {
            return NotFound(new { error = "The rule was not found." });
        }

        var membership = await GetMembershipAsync(appUser.Id, rule.LinkshellId, cancellationToken);
        if (!await CanAsync(membership, r => r.CanManageRules, cancellationToken))
        {
            return Forbid();
        }

        rule.RuleTitle = title;
        rule.RuleDetails = details;
        await _dbContext.SaveChangesAsync(cancellationToken);
        return Ok(new { success = true });
    }

    [HttpPost("rules/{ruleId:int}/delete")]
    public async Task<IActionResult> DeleteRuleAsync(int ruleId, CancellationToken cancellationToken)
    {
        var appUser = await ResolveAppUserAsync(cancellationToken);
        if (appUser is null)
        {
            return Unauthorized(new
            {
                error = "Sign in with ASP.NET Identity or provide a Discord bearer token to delete rules."
            });
        }

        var rule = await _dbContext.Rules.FirstOrDefaultAsync(item => item.Id == ruleId, cancellationToken);
        if (rule is null)
        {
            return NotFound(new { error = "The rule was not found." });
        }

        var membership = await GetMembershipAsync(appUser.Id, rule.LinkshellId, cancellationToken);
        if (!await CanAsync(membership, r => r.CanManageRules, cancellationToken))
        {
            return Forbid();
        }

        _dbContext.Rules.Remove(rule);
        await _dbContext.SaveChangesAsync(cancellationToken);
        return Ok(new { success = true });
    }

    [HttpPost("linkshells/{linkshellId:int}/announcements")]
    public async Task<IActionResult> CreateAnnouncementAsync(
        int linkshellId,
        [FromBody] ActivityCreateAnnouncementRequest request,
        CancellationToken cancellationToken)
    {
        var title = request.Title?.Trim() ?? string.Empty;
        var details = request.Details?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(title))
        {
            return BadRequest(new { error = "Announcement title is required." });
        }
        if (string.IsNullOrWhiteSpace(details))
        {
            return BadRequest(new { error = "Announcement details are required." });
        }

        var appUser = await ResolveAppUserAsync(cancellationToken);
        if (appUser is null)
        {
            return Unauthorized(new
            {
                error = "Sign in with ASP.NET Identity or provide a Discord bearer token to create announcements."
            });
        }

        var membership = await GetMembershipAsync(appUser.Id, linkshellId, cancellationToken);
        if (!await CanAsync(membership, r => r.CanManageAnnouncements, cancellationToken))
        {
            return Forbid();
        }

        var linkshell = await _dbContext.Linkshells.FirstOrDefaultAsync(item => item.Id == linkshellId, cancellationToken);
        if (linkshell is null)
        {
            return NotFound(new { error = "The selected linkshell was not found." });
        }

        var announcement = new Announcement
        {
            LinkshellId = linkshellId,
            LinkshellName = linkshell.LinkshellName,
            AnnouncementTitle = title,
            AnnouncementDetails = details,
            CreatedByAppUserId = appUser.Id,
            CreatedByCharacterName = membership!.CharacterName ?? appUser.CharacterName,
            CreatedAt = DateTime.UtcNow
        };
        _dbContext.Announcements.Add(announcement);
        await _dbContext.SaveChangesAsync(cancellationToken);
        return Ok(new { success = true, id = announcement.Id });
    }

    [HttpPost("announcements/{announcementId:int}/update")]
    public async Task<IActionResult> UpdateAnnouncementAsync(
        int announcementId,
        [FromBody] ActivityCreateAnnouncementRequest request,
        CancellationToken cancellationToken)
    {
        var title = request.Title?.Trim() ?? string.Empty;
        var details = request.Details?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(title))
        {
            return BadRequest(new { error = "Announcement title is required." });
        }
        if (string.IsNullOrWhiteSpace(details))
        {
            return BadRequest(new { error = "Announcement details are required." });
        }

        var appUser = await ResolveAppUserAsync(cancellationToken);
        if (appUser is null)
        {
            return Unauthorized(new
            {
                error = "Sign in with ASP.NET Identity or provide a Discord bearer token to update announcements."
            });
        }

        var announcement = await _dbContext.Announcements.FirstOrDefaultAsync(item => item.Id == announcementId, cancellationToken);
        if (announcement is null)
        {
            return NotFound(new { error = "The announcement was not found." });
        }

        var membership = await GetMembershipAsync(appUser.Id, announcement.LinkshellId, cancellationToken);
        if (!await CanAsync(membership, r => r.CanManageAnnouncements, cancellationToken))
        {
            return Forbid();
        }

        announcement.AnnouncementTitle = title;
        announcement.AnnouncementDetails = details;
        await _dbContext.SaveChangesAsync(cancellationToken);
        return Ok(new { success = true });
    }

    [HttpPost("announcements/{announcementId:int}/delete")]
    public async Task<IActionResult> DeleteAnnouncementAsync(int announcementId, CancellationToken cancellationToken)
    {
        var appUser = await ResolveAppUserAsync(cancellationToken);
        if (appUser is null)
        {
            return Unauthorized(new
            {
                error = "Sign in with ASP.NET Identity or provide a Discord bearer token to delete announcements."
            });
        }

        var announcement = await _dbContext.Announcements.FirstOrDefaultAsync(item => item.Id == announcementId, cancellationToken);
        if (announcement is null)
        {
            return NotFound(new { error = "The announcement was not found." });
        }

        var membership = await GetMembershipAsync(appUser.Id, announcement.LinkshellId, cancellationToken);
        if (!await CanAsync(membership, r => r.CanManageAnnouncements, cancellationToken))
        {
            return Forbid();
        }

        _dbContext.Announcements.Remove(announcement);
        await _dbContext.SaveChangesAsync(cancellationToken);
        return Ok(new { success = true });
    }

    [HttpPost("linkshells/{linkshellId:int}/items")]
    public async Task<IActionResult> CreateItemAsync(
        int linkshellId,
        [FromBody] ActivityCreateItemRequest request,
        CancellationToken cancellationToken)
    {
        var itemName = request.ItemName?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(itemName))
        {
            return BadRequest(new { error = "Item name is required." });
        }
        if (request.Quantity < 0)
        {
            return BadRequest(new { error = "Quantity cannot be negative." });
        }

        var appUser = await ResolveAppUserAsync(cancellationToken);
        if (appUser is null)
        {
            return Unauthorized(new
            {
                error = "Sign in with ASP.NET Identity or provide a Discord bearer token to manage items."
            });
        }

        var membership = await GetMembershipAsync(appUser.Id, linkshellId, cancellationToken);
        if (!await CanAsync(membership, r => r.CanManageInventory, cancellationToken))
        {
            return Forbid();
        }

        var linkshell = await _dbContext.Linkshells.FirstOrDefaultAsync(ls => ls.Id == linkshellId, cancellationToken);
        if (linkshell is null)
        {
            return NotFound(new { error = "The selected linkshell was not found." });
        }

        var now = DateTime.UtcNow;
        var item = new Item
        {
            LinkshellId = linkshellId,
            LinkshellName = linkshell.LinkshellName,
            ItemName = itemName,
            ItemType = string.IsNullOrWhiteSpace(request.ItemType) ? null : request.ItemType.Trim(),
            Quantity = request.Quantity,
            Notes = string.IsNullOrWhiteSpace(request.Notes) ? null : request.Notes.Trim(),
            CreatedByAppUserId = appUser.Id,
            CreatedByCharacterName = membership!.CharacterName ?? appUser.CharacterName,
            CreatedAt = now,
            UpdatedAt = now
        };
        _dbContext.Items.Add(item);
        await _dbContext.SaveChangesAsync(cancellationToken);
        return Ok(new { success = true, id = item.Id });
    }

    [HttpPost("items/{itemId:int}/update")]
    public async Task<IActionResult> UpdateItemAsync(
        int itemId,
        [FromBody] ActivityUpdateItemRequest request,
        CancellationToken cancellationToken)
    {
        var itemName = request.ItemName?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(itemName))
        {
            return BadRequest(new { error = "Item name is required." });
        }
        if (request.Quantity < 0)
        {
            return BadRequest(new { error = "Quantity cannot be negative." });
        }

        var appUser = await ResolveAppUserAsync(cancellationToken);
        if (appUser is null)
        {
            return Unauthorized(new
            {
                error = "Sign in with ASP.NET Identity or provide a Discord bearer token to manage items."
            });
        }

        var item = await _dbContext.Items.FirstOrDefaultAsync(entry => entry.Id == itemId, cancellationToken);
        if (item is null)
        {
            return NotFound(new { error = "The item was not found." });
        }

        var membership = await GetMembershipAsync(appUser.Id, item.LinkshellId, cancellationToken);
        if (!await CanAsync(membership, r => r.CanManageInventory, cancellationToken))
        {
            return Forbid();
        }

        item.ItemName = itemName;
        item.ItemType = string.IsNullOrWhiteSpace(request.ItemType) ? null : request.ItemType.Trim();
        item.Quantity = request.Quantity;
        item.Notes = string.IsNullOrWhiteSpace(request.Notes) ? null : request.Notes.Trim();
        item.UpdatedAt = DateTime.UtcNow;
        await _dbContext.SaveChangesAsync(cancellationToken);
        return Ok(new { success = true });
    }

    [HttpPost("items/{itemId:int}/delete")]
    public async Task<IActionResult> DeleteItemAsync(int itemId, CancellationToken cancellationToken)
    {
        var appUser = await ResolveAppUserAsync(cancellationToken);
        if (appUser is null)
        {
            return Unauthorized(new
            {
                error = "Sign in with ASP.NET Identity or provide a Discord bearer token to manage items."
            });
        }

        var item = await _dbContext.Items.FirstOrDefaultAsync(entry => entry.Id == itemId, cancellationToken);
        if (item is null)
        {
            return NotFound(new { error = "The item was not found." });
        }

        var membership = await GetMembershipAsync(appUser.Id, item.LinkshellId, cancellationToken);
        if (!await CanAsync(membership, r => r.CanManageInventory, cancellationToken))
        {
            return Forbid();
        }

        _dbContext.Items.Remove(item);
        await _dbContext.SaveChangesAsync(cancellationToken);
        return Ok(new { success = true });
    }

    [HttpPost("linkshells/{linkshellId:int}/revenue")]
    public async Task<IActionResult> CreateRevenueEntryAsync(
        int linkshellId,
        [FromBody] ActivityCreateRevenueRequest request,
        CancellationToken cancellationToken)
    {
        var entryType = request.EntryType?.Trim() ?? string.Empty;
        if (!string.Equals(entryType, "Income", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(entryType, "Expense", StringComparison.OrdinalIgnoreCase))
        {
            return BadRequest(new { error = "Entry type must be Income or Expense." });
        }
        if (request.Value < 0)
        {
            return BadRequest(new { error = "Value cannot be negative." });
        }

        var appUser = await ResolveAppUserAsync(cancellationToken);
        if (appUser is null)
        {
            return Unauthorized(new
            {
                error = "Sign in with ASP.NET Identity or provide a Discord bearer token to manage revenue."
            });
        }

        var membership = await GetMembershipAsync(appUser.Id, linkshellId, cancellationToken);
        if (!await CanAsync(membership, r => r.CanManageTreasury, cancellationToken))
        {
            return Forbid();
        }

        var linkshell = await _dbContext.Linkshells.FirstOrDefaultAsync(ls => ls.Id == linkshellId, cancellationToken);
        if (linkshell is null)
        {
            return NotFound(new { error = "The selected linkshell was not found." });
        }

        var normalizedType = string.Equals(entryType, "Income", StringComparison.OrdinalIgnoreCase) ? "Income" : "Expense";
        var occurredAt = request.OccurredAt?.ToUniversalTime() ?? DateTime.UtcNow;
        var entry = new RevenueEntry
        {
            LinkshellId = linkshellId,
            LinkshellName = linkshell.LinkshellName,
            EntryType = normalizedType,
            Category = string.IsNullOrWhiteSpace(request.Category) ? null : request.Category.Trim(),
            Value = request.Value,
            Details = string.IsNullOrWhiteSpace(request.Details) ? null : request.Details.Trim(),
            OccurredAt = occurredAt,
            CreatedByAppUserId = appUser.Id,
            CreatedByCharacterName = membership!.CharacterName ?? appUser.CharacterName,
            CreatedAt = DateTime.UtcNow
        };
        _dbContext.RevenueEntries.Add(entry);
        await _dbContext.SaveChangesAsync(cancellationToken);
        return Ok(new { success = true, id = entry.Id });
    }

    [HttpPost("revenue/{entryId:int}/delete")]
    public async Task<IActionResult> DeleteRevenueEntryAsync(int entryId, CancellationToken cancellationToken)
    {
        var appUser = await ResolveAppUserAsync(cancellationToken);
        if (appUser is null)
        {
            return Unauthorized(new
            {
                error = "Sign in with ASP.NET Identity or provide a Discord bearer token to manage revenue."
            });
        }

        var entry = await _dbContext.RevenueEntries.FirstOrDefaultAsync(item => item.Id == entryId, cancellationToken);
        if (entry is null)
        {
            return NotFound(new { error = "The revenue entry was not found." });
        }

        var membership = await GetMembershipAsync(appUser.Id, entry.LinkshellId, cancellationToken);
        if (!await CanAsync(membership, r => r.CanManageTreasury, cancellationToken))
        {
            return Forbid();
        }

        _dbContext.RevenueEntries.Remove(entry);
        await _dbContext.SaveChangesAsync(cancellationToken);
        return Ok(new { success = true });
    }

    private async Task<AppUser?> ResolveAppUserAsync(CancellationToken cancellationToken)
    {
        if (TryGetBearerToken(out var accessToken))
        {
            try
            {
                var localUser = await _discordIdentityService.GetCurrentLocalUserAsync(accessToken, cancellationToken);
                if (!string.IsNullOrWhiteSpace(localUser.AppUser?.Id))
                {
                    return await _userManager.FindByIdAsync(localUser.AppUser.Id);
                }
            }
            catch (DiscordApiException) when (!_environment.IsDevelopment())
            {
                return null;
            }
            catch (DiscordApiException)
            {
                return null;
            }
        }

        if (User.Identity?.IsAuthenticated == true)
        {
            return await _userManager.GetUserAsync(User);
        }

        return null;
    }

    private bool TryGetBearerToken(out string accessToken)
    {
        accessToken = string.Empty;

        if (!AuthenticationHeaderValue.TryParse(Request.Headers.Authorization, out var headerValue))
        {
            return false;
        }

        if (!"Bearer".Equals(headerValue.Scheme, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(headerValue.Parameter))
        {
            return false;
        }

        accessToken = headerValue.Parameter;
        return true;
    }

    private async Task<AppUserLinkshell?> GetMembershipAsync(string appUserId, int linkshellId, CancellationToken cancellationToken)
    {
        return await _dbContext.AppUserLinkshells
            .Include(link => link.Linkshell)
            .FirstOrDefaultAsync(link => link.AppUserId == appUserId && link.LinkshellId == linkshellId, cancellationToken);
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

    private async Task<bool> CanAsync(
        AppUserLinkshell? membership,
        Func<LinkshellRole, bool> selector,
        CancellationToken cancellationToken)
    {
        if (membership is null)
        {
            return false;
        }

        var role = await GetEffectiveRoleAsync(membership.Rank, membership.LinkshellId, cancellationToken);
        return role is not null && selector(role);
    }

    private async Task<LinkshellRole?> GetEffectiveRoleAsync(
        string? rank,
        int linkshellId,
        CancellationToken cancellationToken)
    {
        await EnsureDefaultRolesAsync(linkshellId, cancellationToken);
        var rankName = string.IsNullOrWhiteSpace(rank) ? "Member" : rank.Trim();
        var role = await _dbContext.LinkshellRoles
            .FirstOrDefaultAsync(r => r.LinkshellId == linkshellId && r.Name == rankName, cancellationToken);
        if (role is null)
        {
            role = await _dbContext.LinkshellRoles
                .FirstOrDefaultAsync(r => r.LinkshellId == linkshellId && r.Name == "Member", cancellationToken);
        }
        return role;
    }

    private async Task<List<LinkshellRole>> EnsureDefaultRolesAsync(int linkshellId, CancellationToken cancellationToken)
    {
        var existing = await _dbContext.LinkshellRoles
            .Where(r => r.LinkshellId == linkshellId)
            .ToListAsync(cancellationToken);

        var existingByName = existing.ToDictionary(r => r.Name, StringComparer.OrdinalIgnoreCase);
        var added = new List<LinkshellRole>();

        if (!existingByName.ContainsKey("Leader"))
        {
            added.Add(new LinkshellRole
            {
                LinkshellId = linkshellId,
                Name = "Leader",
                IsSystem = true,
                SortOrder = 0,
                CanManageRoles = true,
                CanManageMembers = true,
                CanManageEvents = true,
                CanModerateLiveEvent = true,
                CanAddLoot = true,
                CanManageInventory = true,
                CanManageTreasury = true,
                CanManageRules = true,
                CanManageAnnouncements = true,
                CanManageTods = true,
                CanAuditDkp = true,
                CanManageAuctions = true,
                CanCustomizeLinkshell = true
            });
        }

        if (!existingByName.ContainsKey("Officer"))
        {
            added.Add(new LinkshellRole
            {
                LinkshellId = linkshellId,
                Name = "Officer",
                IsSystem = true,
                SortOrder = 1,
                CanManageRoles = false,
                CanManageMembers = false,
                CanManageEvents = true,
                CanModerateLiveEvent = true,
                CanAddLoot = true,
                CanManageInventory = true,
                CanManageTreasury = false,
                CanManageRules = true,
                CanManageAnnouncements = true,
                CanManageTods = true,
                CanAuditDkp = false,
                CanManageAuctions = true,
                CanCustomizeLinkshell = false
            });
        }

        if (!existingByName.ContainsKey("Member"))
        {
            added.Add(new LinkshellRole
            {
                LinkshellId = linkshellId,
                Name = "Member",
                IsSystem = true,
                SortOrder = 2
            });
        }

        if (added.Count > 0)
        {
            await _dbContext.LinkshellRoles.AddRangeAsync(added, cancellationToken);
            await _dbContext.SaveChangesAsync(cancellationToken);
            existing.AddRange(added);
        }

        return existing.OrderBy(r => r.SortOrder).ThenBy(r => r.Name).ToList();
    }

    private static double CalculateAccumulatedDurationHours(AppUserEvent participation, DateTime referenceUtc, DateTime? eventStartUtc)
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

    private static AppUserLinkshell? ResolveLootWinnerMembership(
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

    private static string? NormalizeLookupKey(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private static bool IsLeader(AppUserLinkshell? membership)
    {
        return membership?.Rank?.Equals("Leader", StringComparison.OrdinalIgnoreCase) == true;
    }

    private static string? NormalizeMemberRole(string? role)
    {
        if (string.IsNullOrWhiteSpace(role))
        {
            return null;
        }

        return role.Trim().ToLowerInvariant() switch
        {
            "member" => "Member",
            "officer" => "Officer",
            "leader" => "Leader",
            _ => null
        };
    }

    private static string CreateJobSignature(ActivityCreateJobRequest job)
    {
        return $"{job.JobName?.Trim()}|{job.SubJobName?.Trim()}|{job.JobType?.Trim()}|{job.Quantity}";
    }

    private static string CreateJobSignature(Job job)
    {
        return $"{job.JobName?.Trim()}|{job.SubJobName?.Trim()}|{job.JobType?.Trim()}|{job.Quantity}";
    }

    private bool TryConvertUserTimeZoneToUtc(string? localDateTimeValue, string? timeZoneId, out DateTime? utcDateTime)
    {
        utcDateTime = null;

        if (string.IsNullOrWhiteSpace(localDateTimeValue))
        {
            return true;
        }

        var trimmed = localDateTimeValue.Trim();

        if (HasExplicitUtcOffset(trimmed)
            && DateTime.TryParse(
                trimmed,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal,
                out var parsedUtc))
        {
            utcDateTime = DateTime.SpecifyKind(parsedUtc, DateTimeKind.Utc);
            return true;
        }

        if (!DateTime.TryParseExact(
                trimmed,
                ["yyyy-MM-ddTHH:mm", "yyyy-MM-ddTHH:mm:ss"],
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var localDateTime))
        {
            return false;
        }

        var zone = ResolveTimeZone(timeZoneId);
        utcDateTime = zone.AtLeniently(LocalDateTime.FromDateTime(localDateTime)).ToDateTimeUtc();
        return true;
    }

    private static bool HasExplicitUtcOffset(string value)
    {
        if (value.EndsWith("Z", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var tIndex = value.IndexOf('T');
        if (tIndex < 0)
        {
            return false;
        }

        for (var i = value.Length - 1; i > tIndex; i--)
        {
            var c = value[i];
            if (c == '+' || c == '-')
            {
                return true;
            }
            if (c == ':' || char.IsDigit(c))
            {
                continue;
            }
            return false;
        }

        return false;
    }

    private DateTimeZone ResolveTimeZone(string? timeZoneId)
    {
        if (!string.IsNullOrWhiteSpace(timeZoneId) && _dateTimeZoneProvider.Ids.Contains(timeZoneId))
        {
            return _dateTimeZoneProvider[timeZoneId];
        }

        return DateTimeZone.Utc;
    }
}

public sealed record ActivityOverviewDto(
    ActivityAppUserDto AppUser,
    IReadOnlyList<ActivityLinkshellDto> Linkshells,
    ActivityPrimaryLinkshellDto? PrimaryLinkshell,
    IReadOnlyList<ActivityEventDto> ActiveEvents,
    IReadOnlyList<ActivityInviteDto> PendingInvites,
    IReadOnlyList<ActivityInviteDto> SentInvites,
    IReadOnlyList<ActivityInviteDto> IncomingJoinRequests,
    IReadOnlyList<ActivityInviteDto> OutgoingJoinRequests,
    IReadOnlyList<ActivityHistoryDto> RecentHistory,
    IReadOnlyList<ActivityTodDto> RecentTods,
    ActivityOverviewStatsDto Stats);

public sealed record ActivityAppUserDto(
    string Id,
    string UserName,
    string? CharacterName,
    string? TimeZone,
    int? PrimaryLinkshellId,
    string? PrimaryLinkshellName);

public sealed record ActivityLinkshellDto(
    int Id,
    string Name,
    string? Rank,
    string? Status,
    double? LinkshellDkp,
    int MemberCount,
    int ItemCount,
    long Revenue,
    string? Details,
    ActivityPermissionsDto? Permissions,
    ActivityLinkshellSettingsDto Settings);

public sealed record ActivityLinkshellSettingsDto(
    string LootStructure,
    bool EnableHnmSection,
    bool EnableMissions,
    bool EnableAuctions,
    bool EnableToDs,
    bool EnableEndgame,
    bool EnableEvents,
    bool EnableDkp,
    bool EnableItems,
    bool EnableRevenue,
    string DkpRoundingIncrement);

public sealed record ActivityPermissionsDto(
    bool CanManageRoles,
    bool CanManageMembers,
    bool CanManageEvents,
    bool CanModerateLiveEvent,
    bool CanAddLoot,
    bool CanManageInventory,
    bool CanManageTreasury,
    bool CanManageRules,
    bool CanManageAnnouncements,
    bool CanManageTods,
    bool CanAuditDkp,
    bool CanManageAuctions,
    bool CanCustomizeLinkshell);

public sealed record ActivityPrimaryLinkshellDto(
    int Id,
    string Name,
    int MemberCount,
    string? Details,
    IReadOnlyList<ActivityMemberDto> Members,
    IReadOnlyList<ActivityRuleDto> Rules,
    IReadOnlyList<ActivityAnnouncementDto> Announcements,
    IReadOnlyList<ActivityItemDto> Items,
    IReadOnlyList<ActivityRevenueEntryDto> RevenueEntries);

public sealed record ActivityItemDto(
    int Id,
    int LinkshellId,
    string ItemName,
    string? ItemType,
    int Quantity,
    string? Notes,
    string? CreatedByAppUserId,
    string? CreatedByCharacterName,
    DateTime CreatedAt,
    DateTime UpdatedAt);

public sealed record ActivityRevenueEntryDto(
    int Id,
    int LinkshellId,
    string EntryType,
    string? Category,
    long Value,
    string? Details,
    DateTime OccurredAt,
    string? CreatedByAppUserId,
    string? CreatedByCharacterName,
    DateTime CreatedAt);

public sealed record ActivityCreateItemRequest(string ItemName, string? ItemType, int Quantity, string? Notes);

public sealed record ActivityUpdateItemRequest(string ItemName, string? ItemType, int Quantity, string? Notes);

public sealed record ActivityCreateRevenueRequest(string EntryType, string? Category, long Value, string? Details, DateTime? OccurredAt);

public sealed record ActivityRuleDto(
    int Id,
    int LinkshellId,
    string Title,
    string Details,
    string? CreatedByAppUserId,
    string? CreatedByCharacterName,
    DateTime CreatedAt);

public sealed record ActivityAnnouncementDto(
    int Id,
    int LinkshellId,
    string Title,
    string Details,
    string? CreatedByAppUserId,
    string? CreatedByCharacterName,
    DateTime CreatedAt);

public sealed record ActivityCreateRuleRequest(string Title, string Details);

public sealed record ActivityCreateAnnouncementRequest(string Title, string Details);

public sealed record ActivityLinkshellDetailDto(
    int Id,
    string Name,
    int MemberCount,
    string? Details,
    string? Status,
    IReadOnlyList<ActivityMemberDto> Members);

public sealed record ActivityMemberDto(
    int Id,
    string? AppUserId,
    string CharacterName,
    string? Rank,
    string? Status,
    double? LinkshellDkp);

public sealed record ActivityEventDto(
    int Id,
    int LinkshellId,
    string? Name,
    string? Type,
    string? Location,
    DateTime? StartTime,
    DateTime? EndTime,
    DateTime? CommencementStartTime,
    double? Duration,
    int? DkpPerHour,
    string? Details,
    int ParticipantCount,
    int RequestedSlots,
    ActivityParticipationDto? CurrentParticipation,
    IReadOnlyList<ActivityEventParticipantDto> Participants,
    IReadOnlyList<ActivityLootDto> Loot,
    IReadOnlyList<ActivityJobDto> Jobs,
    int WindowCount,
    IReadOnlyList<ActivityAttendanceWindowDto> AttendanceWindows);

public sealed record ActivityAttendanceWindowDto(
    int Id,
    int SequenceNumber,
    string? Label,
    DateTime PostedAt,
    IReadOnlyList<ActivityAttendanceWindowAttendeeDto> Attendees);

public sealed record ActivityAttendanceWindowAttendeeDto(
    int Id,
    string? CharacterName,
    string? JobName,
    string? SubJobName,
    string? Zone,
    DateTime VerifiedAt,
    string? VerifiedBy);

public sealed record ActivityParticipationDto(
    int Id,
    string? CharacterName,
    string? JobName,
    string? SubJobName,
    string? JobType,
    bool IsQuickJoin,
    bool? IsVerified,
    bool? IsOnBreak,
    IReadOnlyList<ActivityStatusLedgerDto> StatusLedger);

public sealed record ActivityEventParticipantDto(
    int Id,
    string? AppUserId,
    string? CharacterName,
    string? JobName,
    string? SubJobName,
    string? JobType,
    bool IsQuickJoin,
    bool? IsVerified,
    string? Proctor,
    DateTime? StartTime,
    DateTime? ResumeTime,
    DateTime? PauseTime,
    bool? IsOnBreak,
    double? Duration,
    double? EventDkp,
    IReadOnlyList<ActivityStatusLedgerDto> StatusLedger);

public sealed record ActivityStatusLedgerDto(
    int Id,
    string ActionType,
    DateTime OccurredAt,
    bool RequiresVerification,
    DateTime? VerifiedAt,
    string? VerifiedBy,
    DateTime? DeniedAt,
    string? DeniedBy,
    string? Source);

public sealed record ActivityJobDto(
    int Id,
    string? JobName,
    string? SubJobName,
    string? JobType,
    int? Quantity,
    int? SignedUp,
    IReadOnlyList<string> Enlisted);

public sealed record ActivityHistoryDto(
    int Id,
    int LinkshellId,
    string? Name,
    string? Type,
    string? Location,
    DateTime? EndTime,
    double? Duration,
    int ParticipantCount);

public sealed record ActivityHistoryDetailDto(
    int Id,
    int LinkshellId,
    string? Name,
    string? Type,
    string? Location,
    DateTime? StartTime,
    DateTime? EndTime,
    double? Duration,
    int? DkpPerHour,
    string? Details,
    IReadOnlyList<ActivityHistoryParticipantDto> Participants);

public sealed record ActivityHistoryParticipantDto(
    int Id,
    string? AppUserId,
    string? CharacterName,
    string? JobName,
    string? SubJobName,
    string? JobType,
    double? Duration,
    double? EventDkp,
    bool? IsVerified);

public sealed record ActivityLinkshellRolePermissions(
    string? Name,
    bool CanManageRoles,
    bool CanManageMembers,
    bool CanManageEvents,
    bool CanModerateLiveEvent,
    bool CanAddLoot,
    bool CanManageInventory,
    bool CanManageTreasury,
    bool CanManageRules,
    bool CanManageAnnouncements,
    bool CanManageTods,
    bool CanAuditDkp,
    bool CanManageAuctions,
    bool CanCustomizeLinkshell);

public sealed record ActivityLinkshellRoleDto(
    int Id,
    string Name,
    bool IsSystem,
    int SortOrder,
    bool CanManageRoles,
    bool CanManageMembers,
    bool CanManageEvents,
    bool CanModerateLiveEvent,
    bool CanAddLoot,
    bool CanManageInventory,
    bool CanManageTreasury,
    bool CanManageRules,
    bool CanManageAnnouncements,
    bool CanManageTods,
    bool CanAuditDkp,
    bool CanManageAuctions,
    bool CanCustomizeLinkshell);

public sealed record ActivityLinkshellRolesResponse(
    int LinkshellId,
    IReadOnlyList<ActivityLinkshellRoleDto> Roles);

public sealed record ActivityDkpHistoryDto(
    int? LinkshellId,
    string? LinkshellName,
    string? SelectedAppUserId,
    string? SelectedMemberName,
    double CurrentBalance,
    IReadOnlyList<ActivityDkpHistoryMemberDto> Members,
    IReadOnlyList<ActivityDkpLedgerEntryDto> Entries);

public sealed record ActivityDkpHistoryMemberDto(
    string AppUserId,
    string CharacterName,
    double CurrentBalance);

public sealed record ActivityDkpAuditRequest(
    int LinkshellId,
    string TargetAppUserId,
    string Mode,
    int? RelatedLedgerEntryId,
    double Amount,
    string Reason);

public sealed record ActivityDkpLedgerEntryDto(
    int Id,
    string EntryType,
    double Amount,
    double RunningBalance,
    DateTime OccurredAt,
    string? EventName,
    string? EventType,
    string? EventLocation,
    DateTime? EventStartTime,
    DateTime? EventEndTime,
    string? ItemName,
    string? Details);

public sealed record ActivityAuctionDto(
    int Id,
    int LinkshellId,
    string? Title,
    string? CreatedBy,
    DateTime? StartTime,
    DateTime? EndTime,
    DateTime? StartedAt,
    string Status,
    bool CanEdit,
    bool CanStart,
    bool CanClose,
    IReadOnlyList<ActivityAuctionItemDto> Items);

public sealed record ActivityAuctionItemDto(
    int Id,
    string? ItemName,
    string? ItemType,
    int? StartingBidDkp,
    int? CurrentHighestBid,
    string? CurrentHighestBidder,
    string? CurrentHighestBidderAppUserId,
    DateTime? StartTime,
    DateTime? EndTime,
    string? Status,
    string? Notes,
    int BidCount,
    int? SourceItemId);

public sealed record ActivityAuctionBidDto(
    int Id,
    string CharacterName,
    int BidAmount,
    DateTime CreatedAt);

public sealed record ActivityAuctionHistoryDto(
    int Id,
    int LinkshellId,
    string? Title,
    string? CreatedBy,
    DateTime? StartTime,
    DateTime? EndTime,
    DateTime? StartedAt,
    DateTime ClosedAt,
    IReadOnlyList<ActivityAuctionItemDto> Items);

public sealed record ActivityInviteDto(
    int Id,
    string AppUserId,
    int LinkshellId,
    string AppUserDisplayName,
    string LinkshellName,
    string Status);

public sealed record ActivityUserSearchResultDto(
    string Id,
    string DisplayName,
    string? UserName,
    string? PrimaryLinkshellName);

public sealed record ActivityLinkshellSearchResultDto(
    int Id,
    string Name,
    string? Details,
    int MemberCount,
    string? Status);

public sealed record ActivityParticipantInviteCandidateDto(
    string AppUserId,
    string DiscordUserId,
    string DisplayName,
    string? UserName,
    string? PrimaryLinkshellName);

public sealed record ActivityLootDto(
    int Id,
    string? ItemName,
    string? ItemWinner,
    int? WinningDkpSpent);

public sealed record ActivityTodDto(
    int Id,
    int LinkshellId,
    string MonsterName,
    int? DayNumber,
    DateTime? Time,
    bool Claim,
    string? Cooldown,
    DateTime? RepopTime,
    string? Interval,
    int LootCount,
    IReadOnlyList<ActivityTodLootDto> LootDetails,
    string? ImagePath);

public sealed record ActivityTodLootDto(
    int Id,
    string? ItemName,
    string? ItemWinner,
    int? WinningDkpSpent);

public sealed record ActivityOverviewStatsDto(
    int LinkshellCount,
    int ActiveEventCount,
    int CompletedEventCount,
    int LiveEventCount);

public sealed record ActivityEventSignupRequest(
    int JobId,
    string? JobName = null,
    string? SubJobName = null,
    string? JobType = null);

public sealed record ActivityQuickJoinRequest(
    string? JobName,
    string? SubJobName,
    string? JobType);

public sealed record ActivityCreateEventRequest(
    int LinkshellId,
    string EventName,
    string? EventType,
    string? EventLocation,
    string? StartTimeLocal,
    string? EndTimeLocal,
    double? Duration,
    int? DkpPerHour,
    string? Details,
    IReadOnlyList<ActivityCreateJobRequest> Jobs);

public sealed record ActivityCreateJobRequest(
    string? JobName,
    string? SubJobName,
    string? JobType,
    int? Quantity,
    string? Details);

public sealed record ActivityCreateLinkshellRequest(string Name, string? Details);

public sealed record ActivityUpdateLinkshellRequest(
    string Name,
    string? Details,
    string? LootStructure,
    bool? EnableHnmSection,
    bool? EnableMissions,
    bool? EnableAuctions,
    bool? EnableToDs,
    bool? EnableEndgame,
    bool? EnableEvents,
    bool? EnableDkp,
    bool? EnableItems,
    bool? EnableRevenue,
    string? DkpRoundingIncrement);

public sealed record ActivitySendInviteRequest(string AppUserId);

public sealed record ActivityParticipantInviteCandidatesRequest(int LinkshellId, IReadOnlyList<string> DiscordUserIds);

public sealed record ActivityStartEventRequest(IReadOnlyList<int>? AbsentParticipantIds);

public sealed record ActivityVerifyParticipantRequest(int ParticipantId, bool IsVerified);

public sealed record ActivityResetParticipantRequest(int ParticipantId);

public sealed record ActivityVerifyReturnRequest(int LedgerEntryId);

public sealed record ActivityForceBreakRequest(int ParticipantId);

public sealed record ActivityForceResumeRequest(int ParticipantId);

public sealed record ActivityAddLootRequest(string ItemName, string? ItemWinner, int? WinningDkpSpent);

public sealed record ActivityCreateTodRequest(
    int LinkshellId,
    string? MonsterName,
    int? DayNumber,
    bool Claim,
    string? TimeLocal,
    string? Cooldown,
    string? Interval,
    bool NoLoot,
    IReadOnlyList<ActivityCreateTodLootRequest> LootDetails,
    string? ImagePath);

public sealed record ActivityUpdateTodRequest(
    string? MonsterName,
    int? DayNumber,
    bool Claim,
    string? TimeLocal,
    string? Cooldown,
    string? Interval,
    bool NoLoot,
    IReadOnlyList<ActivityCreateTodLootRequest> LootDetails,
    string? ImagePath);

public sealed record ActivityCreateTodLootRequest(
    string? ItemName,
    string? ItemWinner,
    int? WinningDkpSpent);

public sealed record ActivityUpdateMemberRoleRequest(string Role);

public sealed record ActivityUpdateProfileRequest(string CharacterName, string? TimeZone);

public sealed record ActivityAuctionItemInput(
    int Id,
    string? ItemName,
    string? ItemType,
    int? StartingBidDkp,
    string? Notes,
    int? SourceItemId);

public sealed record ActivityCreateAuctionRequest(
    int LinkshellId,
    string Title,
    string? StartTimeLocal,
    string? EndTimeLocal,
    IReadOnlyList<ActivityAuctionItemInput> Items);

public sealed record ActivityAuctionBidRequest(int BidAmount);

public sealed record ActivityCloseAuctionRequest(IReadOnlyList<int>? DeliveredItemIds);
