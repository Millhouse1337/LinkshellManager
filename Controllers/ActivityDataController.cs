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
public sealed partial class ActivityDataController : ControllerBase
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
            .AsNoTracking()
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
            .AsNoTracking()
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
            .AsSplitQuery()
            .AsNoTracking()
            .ToListAsync(cancellationToken);

        var recentHistory = await _dbContext.EventHistories
            .Include(history => history.AppUserEventHistories)
            .Where(history => linkshellIds.Contains(history.LinkshellId))
            .OrderByDescending(history => history.EndTime ?? history.TimeStamp)
            .Take(8)
            .AsNoTracking()
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
            .AsNoTracking()
            .ToListAsync(cancellationToken);

        var sentInvites = await _dbContext.Invites
            .Include(invite => invite.Linkshell)
            .Include(invite => invite.AppUser)
            .Where(invite => linkshellIds.Contains(invite.LinkshellId) && invite.Status == PendingInviteStatus)
            .OrderBy(invite => invite.Linkshell!.LinkshellName)
            .ThenBy(invite => invite.AppUser!.CharacterName)
            .AsNoTracking()
            .ToListAsync(cancellationToken);

        var incomingJoinRequests = await _dbContext.Invites
            .Include(invite => invite.Linkshell)
            .Include(invite => invite.AppUser)
            .Where(invite => linkshellIds.Contains(invite.LinkshellId) && invite.Status == PendingJoinRequestStatus)
            .OrderBy(invite => invite.Linkshell!.LinkshellName)
            .ThenBy(invite => invite.AppUser!.CharacterName)
            .AsNoTracking()
            .ToListAsync(cancellationToken);

        var outgoingJoinRequests = await _dbContext.Invites
            .Include(invite => invite.Linkshell)
            .Include(invite => invite.AppUser)
            .Where(invite => invite.AppUserId == appUser.Id && invite.Status == PendingJoinRequestStatus)
            .OrderBy(invite => invite.Linkshell!.LinkshellName)
            .AsNoTracking()
            .ToListAsync(cancellationToken);

        var primaryLinkshell = linkshellMemberships.FirstOrDefault(link => link.LinkshellId == primaryLinkshellId)?.Linkshell;
        var primaryLinkshellMembers = primaryLinkshellId.HasValue
            ? await _dbContext.AppUserLinkshells
                .Include(link => link.AppUser)
                .Where(link => link.LinkshellId == primaryLinkshellId.Value)
                .OrderBy(link => link.CharacterName)
                .AsNoTracking()
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
            .AsNoTracking()
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
            .AsNoTracking()
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
            .AsNoTracking()
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
                // Surface the monster name in the Event/Context column. Without
                // this the cell renders blank because ToD loot has no parent
                // Event row to source it from. Type and location stay null so
                // the discord activity's conditional subtitle stays hidden
                // (otherwise it would render "Behemoth · ToD · Unknown location").
                EventName = tod.MonsterName,
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
            .AsNoTracking()
            .FirstOrDefaultAsync(r => r.LinkshellId == linkshellId && r.Name == rankName, cancellationToken);
        if (role is null)
        {
            role = await _dbContext.LinkshellRoles
                .AsNoTracking()
                .FirstOrDefaultAsync(r => r.LinkshellId == linkshellId && r.Name == "Member", cancellationToken);
        }
        return role;
    }

    private async Task<List<LinkshellRole>> EnsureDefaultRolesAsync(int linkshellId, CancellationToken cancellationToken)
    {
        var existing = await _dbContext.LinkshellRoles
            .Where(r => r.LinkshellId == linkshellId)
            .AsNoTracking()
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

