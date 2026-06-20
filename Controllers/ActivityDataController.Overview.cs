using System.Globalization;
using System.Net.Http.Headers;
using LinkshellManagerDiscordApp.Data;
using LinkshellManagerDiscordApp.Models;
using LinkshellManagerDiscordApp.Services;
using LinkshellManagerDiscordApp.Utils;
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

        // The single DiscordGuildId drives two complementary checks:
        //  1. Membership — drop any linkshell tied to a guild the user can't
        //     prove membership in (membership-verified; not tied = no call).
        //  2. Launch context — hide linkshells tied to a different Discord
        //     server than the one the Activity is launched in (the web, with no
        //     guild header, is never filtered by this one).
        var accessibleLinkshellIds = await FilterAccessibleLinkshellIdsAsync(
            linkshellMemberships
                // Only *locked* linkshells gate access; a set-but-unlocked server
                // passes null so it's always allowed.
                .Select(link => (link.LinkshellId,
                    link.Linkshell?.LockToDiscordGuild == true ? link.Linkshell?.DiscordGuildId : null))
                .ToList(),
            cancellationToken);
        linkshellMemberships = linkshellMemberships
            .Where(link => accessibleLinkshellIds.Contains(link.LinkshellId)
                && !IsBlockedByGuildLock(link.Linkshell))
            .ToList();

        var linkshellIds = linkshellMemberships
            .Select(link => link.LinkshellId)
            .Distinct()
            .ToList();
        // Only surface a primary linkshell the user is actually allowed to see —
        // never fall back to (or keep) one that was just filtered out above.
        var primaryLinkshellId = appUser.PrimaryLinkshellId.HasValue
            && linkshellMemberships.Any(link => link.LinkshellId == appUser.PrimaryLinkshellId.Value)
                ? appUser.PrimaryLinkshellId
                : linkshellMemberships.FirstOrDefault()?.LinkshellId;

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

        // Net treasury (income minus expense), not the raw sum — an Expense entry
        // subtracts. Mirrors the Manage Revenue panel's "Net". EntryType is the
        // normalized "Income"/"Expense" the Activity writes; legacy/web source
        // strings aren't "Expense", so they count as income.
        var revenueTotals = await _dbContext.RevenueEntries
            .Where(entry => linkshellIds.Contains(entry.LinkshellId))
            .GroupBy(entry => entry.LinkshellId)
            .Select(group => new { LinkshellId = group.Key, Total = group.Sum(entry => entry.EntryType == "Expense" ? -entry.Value : entry.Value) })
            .ToDictionaryAsync(item => item.LinkshellId, item => item.Total, cancellationToken);

        var rolesByLinkshell = await EnsureDefaultRolesForLinkshellsAsync(linkshellIds, cancellationToken);
        var rolesByLinkshellAndName = rolesByLinkshell.ToDictionary(
            kvp => kvp.Key,
            kvp => kvp.Value.ToDictionary(r => r.Name, StringComparer.OrdinalIgnoreCase));

        // Cap is generous because this list feeds both the Live ops panel and the
        // Pending Events queue in one fetch — splitting at 8 was clipping queued
        // rows whenever the linkshell had more than a couple of live events going.
        var activeEvents = await _dbContext.Events
            .Include(evt => evt.AppUserEvents)
                .ThenInclude(participation => participation.StatusLedgerEntries)
            .Include(evt => evt.EventLootDetails)
            .Include(evt => evt.PartySetup)
            .Include(evt => evt.AttendanceWindows)
                .ThenInclude(window => window.Attendees)
                    .ThenInclude(attendee => attendee.AppUserEvent)
            // Scope to the SELECTED linkshell only (the rest of this overview is
            // primary-scoped too) — otherwise the Event System queue shows pending/live
            // events from every linkshell the user belongs to, not just the active one.
            .Where(evt => evt.LinkshellId == primaryLinkshellId)
            .OrderBy(evt => evt.StartTime)
            .Take(50)
            .AsSplitQuery()
            .AsNoTracking()
            .ToListAsync(cancellationToken);

        var recentHistory = await _dbContext.EventHistories
            .Include(history => history.AppUserEventHistories)
            // Past events on the Event page are scoped to the selected linkshell too.
            .Where(history => history.LinkshellId == primaryLinkshellId)
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

        // Current credit / absent streaks per member (consecutive recent counting
        // events) for the roster's active-credit + absent-streak indicators.
        var primaryStreaks = primaryLinkshellId.HasValue
            ? await new MemberActivityService(_dbContext).ComputeStreaksByAppUserAsync(primaryLinkshellId.Value, cancellationToken)
            : new Dictionary<string, MemberStreaks>();

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

        // News-feed sources for the dashboard: recent auctions + DKP adjustments.
        var primaryAuctions = primaryLinkshellId.HasValue
            ? await _dbContext.Auctions
                .AsNoTracking()
                .Where(auction => auction.LinkshellId == primaryLinkshellId.Value)
                .OrderByDescending(auction => auction.EndTime ?? auction.StartedAt ?? auction.StartTime)
                .Take(10)
                .ToListAsync(cancellationToken)
            : new List<Auction>();

        var primaryDkpAudits = primaryLinkshellId.HasValue
            ? await _dbContext.DkpLedgerEntries
                .AsNoTracking()
                .Where(entry => entry.LinkshellId == primaryLinkshellId.Value
                                && (entry.EntryType == "AuditMisc" || entry.EntryType == "AuditAdjustment"))
                .OrderByDescending(entry => entry.OccurredAt)
                .Take(10)
                .ToListAsync(cancellationToken)
            : new List<DkpLedgerEntry>();

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
                .Where(link => creatorStarterUserIds.Contains(link.AppUserId!))
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

        var addonGloballyDisabled = await _globalSettings.IsAddonGloballyDisabledAsync(cancellationToken);

        Response.Headers.CacheControl = "no-store";
        Response.Headers.Pragma = "no-cache";

        // Job levels live per-membership; surface the primary linkshell's (or any
        // membership's) so the profile "My Jobs" editor can pre-fill. Catalog-
        // aligned (index 0 = WAR ... 14 = SMN) — same shape the web profile uses.
        var profileJobLevels = ProfileJobLevels.ToCatalogLevels(
            linkshellMemberships.FirstOrDefault(link => link.LinkshellId == primaryLinkshellId)?.JobLevels
            ?? linkshellMemberships.FirstOrDefault(link => link.JobLevels != null)?.JobLevels);
        // Alt-character job levels live on the AppUser (shared across linkshells).
        var alt1JobLevels = ProfileJobLevels.ToCatalogLevels(appUser.Alt1JobLevels);
        var alt2JobLevels = ProfileJobLevels.ToCatalogLevels(appUser.Alt2JobLevels);
        // "Strong" flags, parallel to the level arrays above (main from the same
        // membership, alts from the AppUser).
        var strongJobs = ProfileJobLevels.ToCatalogFlags(
            linkshellMemberships.FirstOrDefault(link => link.LinkshellId == primaryLinkshellId)?.StrongJobs
            ?? linkshellMemberships.FirstOrDefault(link => link.StrongJobs != null)?.StrongJobs);
        var alt1StrongJobs = ProfileJobLevels.ToCatalogFlags(appUser.Alt1StrongJobs);
        var alt2StrongJobs = ProfileJobLevels.ToCatalogFlags(appUser.Alt2StrongJobs);
        // Per-job merit notes, parallel to the strong flags (main from the primary
        // membership, alts from the AppUser).
        var meritJobs = ProfileJobLevels.NormalizeMerits(
            linkshellMemberships.FirstOrDefault(link => link.LinkshellId == primaryLinkshellId)?.MeritJobs
            ?? linkshellMemberships.FirstOrDefault(link => link.MeritJobs != null)?.MeritJobs);
        var alt1MeritJobs = ProfileJobLevels.NormalizeMerits(appUser.Alt1MeritJobs);
        var alt2MeritJobs = ProfileJobLevels.NormalizeMerits(appUser.Alt2MeritJobs);

        // Biddable DKP per (linkshell, member): committed balance − bid locks − pending
        // live-event loot spend. Surfaced on the roster and next to each live-event
        // participant so spendable power is clear at a glance (DKP is per-linkshell, and
        // events can span linkshells, so it's keyed by linkshell).
        var biddableByLinkshell = new Dictionary<int, Dictionary<string, double>>();
        foreach (var lsId in linkshellIds)
        {
            biddableByLinkshell[lsId] = await AuctionDkpService.ComputeBiddableDkpByUserAsync(
                _dbContext, lsId, cancellationToken);
        }
        double BiddableDkp(int lsId, string? userId) =>
            userId != null && biddableByLinkshell.TryGetValue(lsId, out var byUser)
                ? byUser.GetValueOrDefault(userId)
                : 0d;

        // Enabled repeat-on-ToD board lead times for this linkshell, keyed by lower-cased
        // monster, so the HNM edit form can repopulate the "Repeat post" toggle + lead.
        var hnmBoardLeadByMonster = (await _dbContext.HnmRecurringBoards
                .Where(b => b.LinkshellId == primaryLinkshellId && b.Enabled)
                .Select(b => new { b.MonsterName, b.LeadHours })
                .ToListAsync(cancellationToken))
            .GroupBy(b => (b.MonsterName ?? string.Empty).Trim().ToLowerInvariant())
            .ToDictionary(g => g.Key, g => g.First().LeadHours);
        double? HnmLead(Event e) =>
            !string.IsNullOrWhiteSpace(e.AssignedMonsterName)
            && hnmBoardLeadByMonster.TryGetValue(e.AssignedMonsterName.Trim().ToLowerInvariant(), out var lh)
                ? lh
                : (double?)null;

        return Ok(new ActivityOverviewDto(
            new ActivityAppUserDto(
                appUser.Id,
                appUser.UserName ?? string.Empty,
                appUser.CharacterName,
                appUser.AltCharacterName1,
                appUser.AltCharacterName2,
                appUser.TimeZone,
                appUser.PrimaryLinkshellId,
                appUser.PrimaryLinkshellName,
                profileJobLevels,
                alt1JobLevels,
                alt2JobLevels,
                strongJobs,
                alt1StrongJobs,
                alt2StrongJobs,
                CraftCatalog.Normalize(appUser.CraftLevels),
                CraftCatalog.Normalize(appUser.Alt1CraftLevels),
                CraftCatalog.Normalize(appUser.Alt2CraftLevels),
                meritJobs,
                alt1MeritJobs,
                alt2MeritJobs),
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
                MapLinkshellSettingsDto(link.Linkshell),
                link.Linkshell?.AuctionsLocked ?? false)).ToList(),
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
                        member.LinkshellDkp,
                        member.DateJoined,
                        member.AppUserId != null ? primaryStreaks.GetValueOrDefault(member.AppUserId).Credit : 0,
                        member.AppUserId != null ? primaryStreaks.GetValueOrDefault(member.AppUserId).Absent : 0,
                        IsPlaceholder: member.AppUser?.IsPlaceholder ?? false,
                        BiddableDkp: BiddableDkp(primaryLinkshellId!.Value, member.AppUserId))).ToList(),
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
                        entry.CreatedAt)).ToList(),
                    primaryAuctions.Select(auction =>
                    {
                        var closed = auction.EndTime is { } end && end <= DateTime.UtcNow;
                        var when = closed
                            ? auction.EndTime!.Value
                            : (auction.StartedAt ?? auction.StartTime ?? auction.EndTime ?? DateTime.UtcNow);
                        return new ActivityNewsAuctionDto(
                            auction.Id,
                            string.IsNullOrWhiteSpace(auction.AuctionTitle) ? "Auction" : auction.AuctionTitle!,
                            when,
                            closed);
                    }).ToList(),
                    primaryDkpAudits.Select(entry => new ActivityNewsDkpDto(
                        entry.CharacterName ?? "Member",
                        entry.Amount,
                        entry.EntryType == "AuditAdjustment",
                        entry.OccurredAt)).ToList()),
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
                evt.AutoStart,
                evt.CountsTowardActive,
                evt.AppUserEvents.Count,
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
                        participation.WithdrewFromEvent,
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
                            .ToList(),
                        BiddableDkp(evt.LinkshellId, participation.AppUserId)))
                    .ToList(),
                evt.EventLootDetails
                    .OrderByDescending(loot => loot.Id)
                    .Select(loot => new ActivityLootDto(
                        loot.Id,
                        loot.ItemName,
                        loot.ItemWinner,
                        loot.WinningDkpSpent))
                    .ToList(),
                evt.PartySetupId,
                evt.PartySetup != null ? evt.PartySetup.Name : null,
                evt.PartySetup != null ? evt.PartySetup.AssignedMonsterName : null,
                evt.AssignedMonsterName,
                evt.HnmDefeatedAt != null,
                evt.HnmRepostAt,
                evt.SourceTodId,
                HnmLead(evt) != null,
                HnmLead(evt),
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
                invite.AppUser?.CharacterName ?? invite.AppUser?.UserName ?? invite.DiscordDisplayName ?? "Unknown member",
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
            addonConfigured,
            addonGloballyDisabled));
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

        var linkshellCandidates = await _dbContext.AppUserLinkshells
            .Where(link => link.AppUserId == appUser.Id)
            .Select(link => new { link.LinkshellId, link.Linkshell!.DiscordGuildId })
            .Distinct()
            .ToListAsync(cancellationToken);

        var accessibleLinkshellIds = await FilterAccessibleLinkshellIdsAsync(
            linkshellCandidates.Select(c => (c.LinkshellId, c.DiscordGuildId)).ToList(),
            cancellationToken);
        var linkshellIds = accessibleLinkshellIds.ToList();

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

        // GetMembershipAsync also enforces the per-linkshell Discord guild lock.
        var membership = await GetMembershipAsync(appUser.Id, history.LinkshellId, cancellationToken);
        if (membership is null)
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
