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
    private static readonly HashSet<string> LongWindowTodMonsters = new(StringComparer.OrdinalIgnoreCase)
    {
        "Tiamat",
        "Jormungand",
        "Vrtra"
    };

    private readonly ApplicationDbContext _dbContext;
    private readonly DiscordIdentityService _discordIdentityService;
    private readonly AppUserProfileService _appUserProfileService;
    private readonly UserManager<AppUser> _userManager;
    private readonly IHostEnvironment _environment;
    private readonly IDateTimeZoneProvider _dateTimeZoneProvider;

    public ActivityDataController(
        ApplicationDbContext dbContext,
        DiscordIdentityService discordIdentityService,
        AppUserProfileService appUserProfileService,
        UserManager<AppUser> userManager,
        IHostEnvironment environment,
        IDateTimeZoneProvider dateTimeZoneProvider)
    {
        _dbContext = dbContext;
        _discordIdentityService = discordIdentityService;
        _appUserProfileService = appUserProfileService;
        _userManager = userManager;
        _environment = environment;
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

        var activeEvents = await _dbContext.Events
            .Include(evt => evt.Jobs)
            .Include(evt => evt.AppUserEvents)
                .ThenInclude(participation => participation.StatusLedgerEntries)
            .Include(evt => evt.EventLootDetails)
            .Where(evt => linkshellIds.Contains(evt.LinkshellId))
            .OrderBy(evt => evt.StartTime)
            .Take(8)
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
                link.Linkshell?.Details)).ToList(),
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
                        member.LinkshellDkp)).ToList()),
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
                                item.VerifiedBy))
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
                                item.VerifiedBy))
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
                    job.Enlisted)).ToList())).ToList(),
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
        if (!CanManageLinkshell(membership))
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
        if (!CanManageLinkshell(membership))
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
        if (!CanManageLinkshell(membership))
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
        if (!CanManageLinkshell(membership))
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
        if (!CanManageLinkshell(membership))
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
        if (!CanManageLinkshell(membership))
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
        if (!IsLeader(currentMembership))
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
        if (!IsLeader(currentMembership))
        {
            return Forbid();
        }

        var targetMembership = await _dbContext.AppUserLinkshells
            .FirstOrDefaultAsync(link => link.Id == memberId && link.LinkshellId == linkshellId, cancellationToken);

        if (targetMembership is null)
        {
            return NotFound(new { error = "The selected member was not found." });
        }

        var normalizedRole = NormalizeMemberRole(request.Role);
        if (normalizedRole is null)
        {
            return BadRequest(new { error = "Role must be Member, Officer, or Leader." });
        }

        if (normalizedRole == "Leader")
        {
            if (string.Equals(targetMembership.AppUserId, appUser.Id, StringComparison.Ordinal))
            {
                return BadRequest(new { error = "You are already the leader of this linkshell." });
            }

            currentMembership!.Rank = "Officer";
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
        if (!CanManageLinkshell(membership))
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
        if (request.JobId <= 0)
        {
            return BadRequest(new { error = "A job selection is required." });
        }

        var appUser = await ResolveAppUserAsync(cancellationToken);
        if (appUser is null)
        {
            return Unauthorized(new
            {
                error = "Sign in with ASP.NET Identity or provide a Discord bearer token to sign up for events."
            });
        }

        var job = await _dbContext.Jobs
            .Include(item => item.Event)
            .FirstOrDefaultAsync(item => item.Id == request.JobId && item.EventId == eventId, cancellationToken);

        if (job?.Event is null)
        {
            return NotFound(new { error = "The selected event job was not found." });
        }

        var displayName = appUser.CharacterName ?? appUser.UserName ?? "Unknown";
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
        if (!CanManageLinkshell(membership))
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
        if (!CanManageLinkshell(membership))
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
        if (!CanManageLinkshell(currentMembership))
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
    public async Task<IActionResult> StartEventAsync(int eventId, CancellationToken cancellationToken)
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
        if (!CanManageLinkshell(membership))
        {
            return Forbid();
        }

        eventEntity.CommencementStartTime ??= DateTime.UtcNow;
        foreach (var participation in eventEntity.AppUserEvents)
        {
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
        if (!CanManageLinkshell(membership))
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
        if (!CanManageLinkshell(membership))
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
        if (!CanManageLinkshell(membership))
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
        if (!CanManageLinkshell(membership))
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
        if (string.IsNullOrWhiteSpace(monsterName) || !SupportedTodMonsters.Contains(monsterName))
        {
            return BadRequest(new { error = "Select a valid monster." });
        }

        if (!TryConvertUserTimeZoneToUtc(request.TimeLocal, appUser.TimeZone, out var todTimeUtc) || !todTimeUtc.HasValue)
        {
            return BadRequest(new { error = "Enter a valid Time of Death using your local time." });
        }

        var cooldown = string.IsNullOrWhiteSpace(request.Cooldown)
            ? GetDefaultTodCooldown(monsterName)
            : request.Cooldown.Trim();
        if (!SupportedTodCooldowns.Contains(cooldown))
        {
            return BadRequest(new { error = "Select a valid cooldown." });
        }

        var interval = string.IsNullOrWhiteSpace(request.Interval)
            ? GetDefaultTodInterval(monsterName)
            : request.Interval.Trim();
        if (!SupportedTodIntervals.Contains(interval))
        {
            return BadRequest(new { error = "Select a valid interval." });
        }

        var normalizedLootDetails = request.Claim && !request.NoLoot
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
                return BadRequest(new { error = "Each ToD loot row needs a positive DKP spent value." });
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
            TotalClaims = request.Claim ? 1 : 0
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
            await AdjustTodLootDkpAsync(tod, normalizedLootDetails, nowUtc, isRefund: false, cancellationToken);
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

        await AdjustTodLootDkpAsync(tod, tod.TodLootDetails.ToList(), DateTime.UtcNow, isRefund: true, cancellationToken);
        _dbContext.TodLootDetails.RemoveRange(tod.TodLootDetails);
        _dbContext.Tods.Remove(tod);
        await _dbContext.SaveChangesAsync(cancellationToken);

        return Ok(new { success = true });
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
            .FirstOrDefaultAsync(evt => evt.Id == eventId, cancellationToken);

        if (eventEntity is null)
        {
            return NotFound(new { error = "The selected event was not found." });
        }

        var membership = await GetMembershipAsync(appUser.Id, eventEntity.LinkshellId, cancellationToken);
        if (!CanManageLinkshell(membership))
        {
            return Forbid();
        }

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
            var roundedDuration = Math.Round(durationHours * 4) / 4;
            var eventDkp = roundedDuration * (eventEntity.DkpPerHour ?? 0);

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
                linkshellMembership.LinkshellDkp = (linkshellMembership.LinkshellDkp ?? 0) + eventDkp;
                nextSequenceByAppUserId[participation.AppUserId] = 2;
            }

            if (!string.IsNullOrWhiteSpace(participation.AppUserId))
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
        foreach (var lootDetail in eventEntity.EventLootDetails.OrderBy(detail => detail.Id))
        {
            if (lootDetail.WinningDkpSpent.GetValueOrDefault() <= 0)
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

            var amount = -lootDetail.WinningDkpSpent.GetValueOrDefault();
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
                Details = $"DKP spent on loot: {lootDetail.ItemName ?? "Unknown item"}."
            });
            nextSequenceByAppUserId[winnerMembership.AppUserId] = currentSequence + 1;
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
        if (!CanManageLinkshell(membership))
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
                Notes = item.Notes?.Trim()
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
                Notes = itemRequest.Notes?.Trim()
            });
        }

        if (remainingItems.Count > 0)
        {
            _dbContext.Bids.RemoveRange(remainingItems.Values.SelectMany(item => item.Bids));
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
    public async Task<IActionResult> CloseAuctionAsync(int auctionId, CancellationToken cancellationToken)
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
                .Select(item => new AuctionItem
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
                    Status = string.IsNullOrWhiteSpace(item.CurrentHighestBidderAppUserId) ? "NoBids" : "Closed",
                    Notes = item.Notes
                })
                .ToList()
        };

        _dbContext.AuctionHistories.Add(history);

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

    private static List<TodLootDetail> NormalizeTodLootDetails(IReadOnlyList<ActivityCreateTodLootRequest>? lootDetails)
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

    private async Task AdjustTodLootDkpAsync(
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

        var winnerNames = actionableLoot
            .Select(detail => detail.ItemWinner!.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var memberships = await _dbContext.AppUserLinkshells
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
            : await _dbContext.DkpLedgerEntries
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

            var dkpValue = detail.WinningDkpSpent.GetValueOrDefault();
            var amount = isRefund ? dkpValue : -dkpValue;
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
                Details = isRefund
                    ? $"Refunded DKP for deleted ToD loot on {tod.MonsterName ?? "Unknown monster"}."
                    : $"DKP spent on ToD loot from {tod.MonsterName ?? "Unknown monster"}."
            });
        }

        if (ledgerEntries.Count > 0)
        {
            await _dbContext.DkpLedgerEntries.AddRangeAsync(ledgerEntries, cancellationToken);
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
                .ToList());
    }

    private static double ResolveTodCooldownHours(string? cooldown)
    {
        return string.Equals(cooldown, TodManagerViewModel.SeventyTwoHourCooldown, StringComparison.OrdinalIgnoreCase)
            ? 72d
            : 22d;
    }

    private static string GetDefaultTodCooldown(string? monsterName)
    {
        return !string.IsNullOrWhiteSpace(monsterName) && LongWindowTodMonsters.Contains(monsterName.Trim())
            ? TodManagerViewModel.SeventyTwoHourCooldown
            : TodManagerViewModel.TwentyTwoHourCooldown;
    }

    private static string GetDefaultTodInterval(string? monsterName)
    {
        return !string.IsNullOrWhiteSpace(monsterName) && LongWindowTodMonsters.Contains(monsterName.Trim())
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
               && !HasAuctionStarted(auction, referenceUtc)
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
            isCreator && !auction.StartedAt.HasValue,
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
                    item.Bids.Count))
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
                    0))
                .ToList());
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

        if (!DateTime.TryParseExact(
                localDateTimeValue.Trim(),
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
    string? Details);

public sealed record ActivityPrimaryLinkshellDto(
    int Id,
    string Name,
    int MemberCount,
    string? Details,
    IReadOnlyList<ActivityMemberDto> Members);

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
    IReadOnlyList<ActivityJobDto> Jobs);

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
    string? VerifiedBy);

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
    int BidCount);

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
    IReadOnlyList<ActivityTodLootDto> LootDetails);

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

public sealed record ActivityEventSignupRequest(int JobId);

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

public sealed record ActivityUpdateLinkshellRequest(string Name, string? Details);

public sealed record ActivitySendInviteRequest(string AppUserId);

public sealed record ActivityParticipantInviteCandidatesRequest(int LinkshellId, IReadOnlyList<string> DiscordUserIds);

public sealed record ActivityVerifyParticipantRequest(int ParticipantId, bool IsVerified);

public sealed record ActivityResetParticipantRequest(int ParticipantId);

public sealed record ActivityVerifyReturnRequest(int LedgerEntryId);

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
    IReadOnlyList<ActivityCreateTodLootRequest> LootDetails);

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
    string? Notes);

public sealed record ActivityCreateAuctionRequest(
    int LinkshellId,
    string Title,
    string? StartTimeLocal,
    string? EndTimeLocal,
    IReadOnlyList<ActivityAuctionItemInput> Items);

public sealed record ActivityAuctionBidRequest(int BidAmount);
