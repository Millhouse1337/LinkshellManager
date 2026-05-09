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

public sealed partial class ActivityDataController
{
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

        var rolesByLinkshell = await EnsureDefaultRolesForLinkshellsAsync(linkshellIds, cancellationToken);
        var rolesByLinkshellAndName = rolesByLinkshell.ToDictionary(
            kvp => kvp.Key,
            kvp => kvp.Value.ToDictionary(r => r.Name, StringComparer.OrdinalIgnoreCase));

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

        // Resolve creator/starter character names for each active event. Prefer
        // the user's linkshell-specific character name; fall back to their
        // top-level AppUser.CharacterName, then UserName.
        var creatorStarterUserIds = activeEvents
            .SelectMany(evt => new[] { evt.CreatorUserId, evt.StarterUserId })
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id!)
            .Distinct()
            .ToList();

        var membershipNamesByPair = creatorStarterUserIds.Count > 0
            ? await _dbContext.AppUserLinkshells
                .Where(link => creatorStarterUserIds.Contains(link.AppUserId))
                .Select(link => new { link.LinkshellId, link.AppUserId, link.CharacterName })
                .AsNoTracking()
                .ToListAsync(cancellationToken)
            : new();

        var membershipNameLookup = membershipNamesByPair
            .Where(row => !string.IsNullOrWhiteSpace(row.CharacterName))
            .ToDictionary(row => (row.LinkshellId, row.AppUserId), row => row.CharacterName!);

        var fallbackNames = creatorStarterUserIds.Count > 0
            ? await _userManager.Users
                .Where(u => creatorStarterUserIds.Contains(u.Id))
                .Select(u => new { u.Id, u.CharacterName, u.UserName })
                .AsNoTracking()
                .ToDictionaryAsync(u => u.Id, u => u.CharacterName ?? u.UserName, cancellationToken)
            : new Dictionary<string, string?>();

        string? ResolveActorName(int linkshellId, string? userId)
        {
            if (string.IsNullOrWhiteSpace(userId)) return null;
            if (membershipNameLookup.TryGetValue((linkshellId, userId), out var name)) return name;
            return fallbackNames.GetValueOrDefault(userId);
        }

        var addonConfigured = await _dbContext.AddonApiTokens
            .AnyAsync(token => token.IssuedToAppUserId == appUser.Id && token.RevokedAt == null, cancellationToken);

        Response.Headers.CacheControl = "no-store";
        Response.Headers.Pragma = "no-cache";

        return Ok(new ActivityOverviewDto(
            new ActivityAppUserDto(
                appUser.Id,
                appUser.UserName ?? string.Empty,
                appUser.CharacterName,
                appUser.AltCharacterName1,
                appUser.AltCharacterName2,
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
                        member.AppUser?.AltCharacterName1,
                        member.AppUser?.AltCharacterName2,
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
                evt.WindowCountOverride ?? HnmConfig.GetWindowCount(evt.EventName),
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
                    .ToList(),
                ResolveActorName(evt.LinkshellId, evt.CreatorUserId),
                ResolveActorName(evt.LinkshellId, evt.StarterUserId))).ToList(),
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
                activeEvents.Count(evt => evt.CommencementStartTime.HasValue)),
            addonConfigured));
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
}
