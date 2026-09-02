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
    public async Task<IActionResult> GetOverviewAsync(
        [FromServices] HnmWindowStatsService hnmWindowStats,
        [FromServices] HnmClaimStatsService hnmClaimStats,
        CancellationToken cancellationToken)
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
            .Where(item => linkshellIds.Contains(item.LinkshellId) && !item.IsSold)
            .GroupBy(item => item.LinkshellId)
            .Select(group => new { LinkshellId = group.Key, Count = group.Count() })
            .ToDictionaryAsync(item => item.LinkshellId, item => item.Count, cancellationToken);

        var revenueTotals = await _treasury.GetCashOnHandByLinkshellAsync(linkshellIds, cancellationToken);

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
                // Time ?? TimeStamp: a ToD that was never entered (camp ended without a kill) has
                // no Time, but it's still the monster's most recent event — fall back to when the
                // row was written so it doesn't sink below the pop it superseded.
                .OrderByDescending(tod => tod.Time ?? tod.TimeStamp)
                .ThenByDescending(tod => tod.Id)
                .Take(25)
                .ToListAsync(cancellationToken)
            : new List<Tod>();

        // The HNM Claims donut is aggregated over ALL claimed ToDs, not the 25 above — those are
        // the most recent pops of any monster, so a run of Sky/ground NM kills pushed every HNM
        // claim out of the chart and the "All" toggle silently charted a tail.
        var hnmClaimStatsResult = await hnmClaimStats.BuildAsync(primaryLinkshellId, cancellationToken);
        // The same card's Windows tab. Separate query: it reads Tod.PopWindow over every pop,
        // claimed or not, rather than the claimed rows the donut counts.
        var hnmWindowStatsResult = await hnmWindowStats.BuildAsync(primaryLinkshellId, cancellationToken);

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

        // Which primary-linkshell members have actually opened/synced the Discord
        // Activity (a DiscordActivityUser row points at their AppUserId). Lets the
        // roster tell real app users apart from outside-sign-up-board-only members,
        // whose AppUserId alone no longer implies they've ever opened the Activity.
        var primaryMemberAppUserIds = primaryLinkshellMembers
            .Select(member => member.AppUserId)
            .Where(id => id != null)
            .Select(id => id!)
            .Distinct()
            .ToList();
        var primarySyncedAppUserIds = primaryMemberAppUserIds.Count == 0
            ? new HashSet<string>(StringComparer.Ordinal)
            : (await _dbContext.DiscordActivityUsers
                .Where(d => d.IdentityUserId != null && primaryMemberAppUserIds.Contains(d.IdentityUserId))
                .Select(d => d.IdentityUserId!)
                .ToListAsync(cancellationToken))
                .ToHashSet(StringComparer.Ordinal);

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

        // The legacy revenueEntries payload, kept for ONE release and now fed from the treasury journal
        // rather than the old RevenueEntries table.
        //
        // The Angular bundle ships separately from the server and there is no API versioning, so a
        // browser holding a cached bundle will call the new server. Reading the old table would show
        // such a client a treasury frozen at conversion day; dropping the field outright would render
        // NaN gil. Projecting the journal back into the old shape keeps it correct until the field goes.
        var primaryRevenue = primaryLinkshellId.HasValue
            ? await _dbContext.JournalEntries
                .AsNoTracking()
                .Include(entry => entry.Lines)
                .Where(entry => entry.LinkshellId == primaryLinkshellId.Value
                    && entry.Status == JournalEntryStatuses.Confirmed)
                .OrderByDescending(entry => entry.TransactionDate)
                .ThenByDescending(entry => entry.Sequence)
                .ToListAsync(cancellationToken)
            : new List<JournalEntry>();

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

        // Snapshots an officer attached to one of these camps. Same one-query-then-group shape as
        // the claim shields below, and for the same reason: most events have none.
        var linkedEventIds = activeEvents.Select(evt => evt.Id).ToList();
        var linkedSnapshotsByEvent = linkedEventIds.Count > 0
            ? (await _dbContext.AttendanceSnapshots
                    .AsNoTracking()
                    .Include(snapshot => snapshot.Entries)
                    .Where(snapshot => snapshot.LinkedEventId != null
                                       && linkedEventIds.Contains(snapshot.LinkedEventId.Value)
                                       && snapshot.SnapshotStatus != AttendanceSnapshotStatuses.Ignored)
                    .OrderBy(snapshot => snapshot.CapturedAtUtc)
                    .ToListAsync(cancellationToken))
                .GroupBy(snapshot => snapshot.LinkedEventId!.Value)
                .ToDictionary(group => group.Key, group => group.ToList())
            : new Dictionary<int, List<AttendanceSnapshot>>();

        List<ActivityLinkedSnapshotDto> LinkedSnapshotsFor(Event evt)
        {
            if (!linkedSnapshotsByEvent.TryGetValue(evt.Id, out var snapshots))
            {
                return new List<ActivityLinkedSnapshotDto>();
            }
            // The POST count, because this names a window. On a Standard king/dragon that is 2,
            // so a snapshot taken in the camp's first window reads "Open" — the spawn count (7)
            // would send it down GetDefaultWindowLabel's numbered branch instead.
            var windowCount = DiscordEventMessageBuilder.AttendancePostCount(evt);
            return snapshots.Select(snapshot => new ActivityLinkedSnapshotDto(
                snapshot.Id,
                snapshot.Name,
                snapshot.CapturedAtUtc,
                snapshot.CapturedByCharacterName,
                // Named the same way the window tabs above it are, so a snapshot taken in the
                // camp's first window reads "Open" here too rather than "Window 1".
                //
                // A Misc capture is labelled as such rather than left blank: it carries no window
                // number by construction, and an unlabelled row here would read as a capture whose
                // window nobody had got round to setting.
                AttendanceSnapshotSlotKinds.IsMisc(snapshot.SlotKind)
                    ? "Misc"
                    : snapshot.WindowNumber is { } window
                        ? HnmConfig.GetDefaultWindowLabel(evt.EventName, window, windowCount)
                          ?? $"Window {window}"
                        : null,
                snapshot.SnapshotStatus,
                snapshot.Entries
                    // Hand-added names sort last so an asserted attendee never sits among the
                    // scanned ones (the UI tints them too).
                    .OrderBy(entry => entry.AddedManually)
                    .ThenBy(entry => entry.CharacterName)
                    .Select(entry => new ActivityLinkedSnapshotEntryDto(
                        entry.Id,
                        entry.CharacterName,
                        entry.MainJob,
                        entry.MainJobLevel,
                        entry.SubJob,
                        entry.SubJobLevel,
                        entry.Zone,
                        entry.AddedManually))
                    .ToList()))
                .ToList();
        }

        // Claim-shield lotteries captured during these camps, grouped by event.
        // Loaded in one query rather than as an Include on activeEvents: most
        // events have none, and the ones that do have very few, so a join would
        // fan out every event row for the sake of a handful of captures.
        var claimShieldEventIds = activeEvents.Select(evt => evt.Id).ToList();
        var claimShieldsByEvent = claimShieldEventIds.Count > 0
            ? (await _dbContext.ClaimShieldCaptures
                    .AsNoTracking()
                    .Include(capture => capture.Members)
                    .Where(capture => capture.EventId != null
                                      && claimShieldEventIds.Contains(capture.EventId.Value))
                    .OrderBy(capture => capture.CapturedAtUtc)
                    .ToListAsync(cancellationToken))
                .GroupBy(capture => capture.EventId!.Value)
                .ToDictionary(group => group.Key, group => group.ToList())
            : new Dictionary<int, List<ClaimShieldCapture>>();

        // Which posted window a capture belongs to. Windows are posted AT a
        // moment, so a capture sits "in" the window whose post it follows --
        // the last window posted at or before it. A capture that predates every
        // window (the pop happened before anyone posted) falls to the first.
        //
        // This is the direction the officer actually wants: the game stamped the
        // lottery line, so the capture dates the window, not the other way round.
        static int? NearestWindowSequence(
            ClaimShieldCapture capture, ICollection<EventAttendanceWindow> windows)
        {
            if (windows.Count == 0) return null;
            var ordered = windows.OrderBy(window => window.PostedAt).ToList();
            var match = ordered.LastOrDefault(window => window.PostedAt <= capture.CapturedAtUtc);
            return (match ?? ordered[0]).SequenceNumber;
        }

        List<ActivityClaimShieldCaptureDto> ClaimShieldsFor(Event evt) =>
            claimShieldsByEvent.TryGetValue(evt.Id, out var captures)
                ? captures.Select(capture => new ActivityClaimShieldCaptureDto(
                    capture.Id,
                    capture.MonsterName,
                    capture.Won,
                    capture.TotalPlayers,
                    capture.CapturedAtUtc,
                    capture.CapturedMessage,
                    NearestWindowSequence(capture, evt.AttendanceWindows),
                    capture.Members
                        .OrderBy(member => member.CharacterName)
                        .Select(member => new ActivityClaimShieldMemberDto(
                            member.CharacterName, member.ActionMessage, member.Matched))
                        .ToList()))
                    .ToList()
                : new List<ActivityClaimShieldCaptureDto>();

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
        // Rides the polled overview rather than the monster-timings GET, so the editor learns about
        // it on the next poll instead of only when someone reopens the tab.
        var claimShieldGloballyDisabled = await _globalSettings.IsClaimShieldGloballyDisabledAsync(cancellationToken);

        // The app-wide admin override, resolved once for the whole payload. The first
        // flag decides THIS user's permissions (only for linkshells listed below, which
        // are already membership- and guild-lock-filtered); the second decides whose
        // roster rows get the ADMIN badge.
        var adminOverrideActive = await _adminOverride.IsActiveForAsync(appUser, cancellationToken);
        var adminOverrideEnabled = await _adminOverride.IsEnabledAsync(cancellationToken);

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

        // The same figure, narrowed to the DKP pool each LIVE EVENT draws from. That's the number
        // an officer needs when they type a loot cost: on a Sky event, a member with Sky+Sea+Dynamis
        // pooled together can spend the whole pooled total, and DKP sitting in an unrelated pool
        // isn't theirs to spend here. Computed once per distinct (linkshell, pool) actually in play
        // — normally one — so this costs the same round-trips it always did.
        var poolMapByLinkshell = new Dictionary<int, DkpPoolMap>();
        foreach (var lsId in linkshellIds)
        {
            poolMapByLinkshell[lsId] = await _dkpPools.GetMapAsync(lsId, cancellationToken);
        }
        int EventPoolId(int lsId, string? eventType) =>
            poolMapByLinkshell.TryGetValue(lsId, out var map) ? map.Resolve(eventType) : 0;
        string? EventPoolName(int lsId, string? eventType) =>
            poolMapByLinkshell.TryGetValue(lsId, out var map) && map.HasMultiplePools
                ? map.NameFor(map.Resolve(eventType))
                : null;

        var biddableByEventPool = new Dictionary<(int LinkshellId, int PoolId), Dictionary<string, double>>();
        foreach (var key in activeEvents
            .Select(evt => (evt.LinkshellId, PoolId: EventPoolId(evt.LinkshellId, evt.EventType)))
            .Distinct())
        {
            if (key.PoolId == 0) { continue; }
            biddableByEventPool[key] = await AuctionDkpService.ComputePoolBiddableDkpByUserAsync(
                _dbContext, _dkpPoolBalances, key.LinkshellId, key.PoolId, cancellationToken);
        }
        double EventBiddableDkp(int lsId, string? eventType, string? userId) =>
            userId != null
            && biddableByEventPool.TryGetValue((lsId, EventPoolId(lsId, eventType)), out var byUser)
                ? byUser.GetValueOrDefault(userId)
                : BiddableDkp(lsId, userId);

        // Enabled repeat-on-ToD board lead times, keyed linkshell -> lower-cased monster, so the
        // HNM event forms can repopulate the "Repeat post" toggle + lead.
        //
        // Every membership, not just the primary one: the same numbers are stamped onto each
        // linkshell's monster catalog below (ActivityMonsterSetupDto.RepeatLeadHours), and the
        // create-event form reads them for whichever linkshell the camp is being made in.
        var hnmBoardLeadsByLinkshell = (await _dbContext.HnmRecurringBoards
                .Where(b => linkshellIds.Contains(b.LinkshellId) && b.Enabled)
                .Select(b => new { b.LinkshellId, b.MonsterName, b.LeadHours })
                .ToListAsync(cancellationToken))
            .GroupBy(b => b.LinkshellId)
            .ToDictionary(
                byLinkshell => byLinkshell.Key,
                byLinkshell => byLinkshell
                    .GroupBy(b => (b.MonsterName ?? string.Empty).Trim().ToLowerInvariant())
                    .ToDictionary(g => g.Key, g => g.First().LeadHours));
        var hnmBoardLeadByMonster =
            primaryLinkshellId.HasValue
            && hnmBoardLeadsByLinkshell.TryGetValue(primaryLinkshellId.Value, out var primaryLeads)
                ? primaryLeads
                : new Dictionary<string, double>();
        // Matched on every spelling of the spawn (either half of a merge pair and the
        // combined "Base/Stronger" label) so a board keyed on one still pre-fills the
        // End Camp form's re-post toggle + lead for an event keyed on another.
        double? HnmLead(Event e)
        {
            foreach (var name in HnmConfig.MonsterMatchNamesLower(e.AssignedMonsterName))
            {
                if (hnmBoardLeadByMonster.TryGetValue(name, out var lh))
                {
                    return lh;
                }
            }
            return null;
        }

        // Source ToDs that carry NO observed Time of Death — the camp was ended without one, so
        // there is no predicted repop and the board's StartTime still points at the pop that just
        // passed. The defeated-board banner branches on this instead of promising a repop that was
        // never derived. One query for every event in the payload; absent from the set = fine.
        var todlessSourceTodIds = (await _dbContext.Tods
                .AsNoTracking()
                .Where(t => t.LinkshellId == primaryLinkshellId && t.Time == null)
                .Select(t => t.Id)
                .ToListAsync(cancellationToken))
            .ToHashSet();
        bool HnmTodRecorded(Event e) =>
            e.SourceTodId is not { } todId || !todlessSourceTodIds.Contains(todId);

        // Which linkshells have a dashboard banner (+ its version for cache-busting),
        // fetched WITHOUT the image bytes so the polled overview stays lean.
        var bannerVersions = (await _dbContext.LinkshellBanners
                .Where(b => linkshellIds.Contains(b.LinkshellId))
                .Select(b => new { b.LinkshellId, b.UpdatedAt })
                .ToListAsync(cancellationToken))
            .ToDictionary(b => b.LinkshellId, b => b.UpdatedAt);
        string? BannerUrl(int lsId) =>
            bannerVersions.TryGetValue(lsId, out var updated)
                ? $"/api/activity/linkshells/{lsId}/banner?v={updated.Ticks}"
                : null;

        // One query for every linkshell the viewer is in, rather than a lookup per membership.
        // A linkshell that has never opened the editor has no rows and projects the built-in
        // defaults, so the client always receives a complete catalog.
        var monsterTimingMaps = await _monsterTimings.GetMapsAsync(linkshellIds, cancellationToken);

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
                ResolvePermissionsFor(link.LinkshellId, link.Rank, rolesByLinkshellAndName, adminOverrideActive),
                MapLinkshellSettingsDto(
                    link.Linkshell,
                    MonsterSetupsFor(
                        monsterTimingMaps, link.LinkshellId,
                        hnmBoardLeadsByLinkshell.TryGetValue(link.LinkshellId, out var linkLeads)
                            ? linkLeads
                            : null)),
                link.Linkshell?.AuctionsLocked ?? false,
                BannerUrl(link.LinkshellId))).ToList(),
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
                        BiddableDkp: BiddableDkp(primaryLinkshellId!.Value, member.AppUserId),
                        HasSyncedActivity: member.AppUserId != null && primarySyncedAppUserIds.Contains(member.AppUserId),
                        IsAdmin: adminOverrideEnabled && (member.AppUser?.IsSuperAdmin ?? false))).ToList(),
                    primaryRules.Select(rule => new ActivityRuleDto(
                        rule.Id,
                        rule.LinkshellId,
                        rule.RuleTitle,
                        rule.RuleDetails,
                        rule.Category,
                        rule.CreatedByAppUserId,
                        rule.CreatedByCharacterName,
                        rule.CreatedAt)).ToList(),
                    primaryAnnouncements.Select(announcement => new ActivityAnnouncementDto(
                        announcement.Id,
                        announcement.LinkshellId,
                        announcement.AnnouncementTitle,
                        announcement.AnnouncementDetails,
                        announcement.Category,
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
                        item.UpdatedAt,
                        item.IsSold,
                        item.SoldPrice,
                        item.SoldByCharacterName)).ToList(),
                    primaryRevenue.Select(ToLegacyRevenueDto).ToList(),
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
                        EventBiddableDkp(evt.LinkshellId, evt.EventType, participation.AppUserId),
                        participation.WdArrivalWindow,
                        participation.WdDepartureWindow))
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
                evt.DayNumber,
                evt.HnmDefeatedAt != null,
                evt.HnmRepostAt,
                evt.SourceTodId,
                HnmLead(evt) != null,
                HnmLead(evt),
                DiscordEventMessageBuilder.EffectiveWindowCount(evt),
                DiscordEventMessageBuilder.AttendancePostCount(evt),
                evt.AttendanceWindows
                    .OrderBy(window => window.SequenceNumber)
                    .Select(window => new ActivityAttendanceWindowDto(
                        window.Id,
                        window.SequenceNumber,
                        // Not window.Label raw, for two reasons. Rows posted before the Open/Close
                        // rename still say "On Time" / "Claim/Kill", and a camp caught mid-flight
                        // would show one of each. And a row written while the label was derived
                        // from the SPAWN count has no label at all — GetDefaultWindowLabel only
                        // names a 2-post camp — so it fell back to "Window 1" on a camp whose
                        // windows are an Open and a Close. Re-deriving here from the post count
                        // heals those without a migration; a stored label still wins.
                        // (activeEvents is materialized, so this runs in memory.)
                        HnmConfig.NormalizeWindowLabel(window.Label)
                            ?? HnmConfig.GetDefaultWindowLabel(
                                evt.EventName,
                                window.SequenceNumber,
                                DiscordEventMessageBuilder.AttendancePostCount(evt)),
                        window.PostedAt,
                        window.Attendees
                            // Sorted on the SAME fallback the row renders, or a roster clear would
                            // sort half the table under the empty string.
                            .OrderBy(att => att.CharacterName
                                ?? (att.AppUserEvent != null ? att.AppUserEvent.CharacterName : null)
                                ?? string.Empty)
                            .Select(att => new ActivityAttendanceWindowAttendeeDto(
                                att.Id,
                                // The DENORMALIZED name on the snapshot row WINS, and falls back to
                                // the participation only for rows written before it was stamped.
                                // Two reasons it leads. The participation is DELETED by a roster
                                // clear — the 25-window wyrm camps wipe theirs every window — while
                                // the snapshot survives it via SetNull, so reading the name only
                                // through the navigation turned every window of a wyrm camp into
                                // "Unknown attendee" the moment the window advanced. And the window
                                // row is the one that knows which CHARACTER was scanned: a player on
                                // an alt has the alt here and their main on the participation.
                                // Same order the addon's GET /events/{id} applies; these two must
                                // agree, because they render the same roster.
                                att.CharacterName
                                    ?? (att.AppUserEvent != null ? att.AppUserEvent.CharacterName : null),
                                // Non-null only when the name above is one of the member's alts, in
                                // which case the UI reads "Athmilk (alt of Edicius)" — matching what
                                // the addon showed the officer when it scanned them.
                                att.MainCharacterName,
                                // Jobs have NO such fallback — they were only ever on the
                                // participation — so they stay null and render as "Anon" after a
                                // clear. Recording them per snapshot is a schema change; until
                                // then, a missing job on an old window is missing data, not a bug
                                // in this projection.
                                att.AppUserEvent != null ? att.AppUserEvent.JobName : null,
                                att.AppUserEvent != null ? att.AppUserEvent.SubJobName : null,
                                att.Zone,
                                att.VerifiedAt,
                                att.VerifiedBy))
                            .ToList(),
                        window.DkpAmount,
                        window.IsClosingWindow,
                        window.IsKillWindow))
                    .ToList(),
                LinkedSnapshotsFor(evt),
                ClaimShieldsFor(evt),
                ResolveActorName(evt.LinkshellId, evt.CreatorUserId),
                ResolveActorName(evt.LinkshellId, evt.StarterUserId),
                EventPoolName(evt.LinkshellId, evt.EventType),
                evt.AttendanceMode,
                evt.HnmWindowNumber,
                evt.NextWindowAt,
                evt.WdAwaitingProcessingSince,
                evt.WdFinalizedAt,
                evt.WdPopWindow,
                // What the UI shows — same helper the Discord board uses, so the two can't
                // disagree. Separate from HnmWindowNumber above, which pop-window logic needs.
                DiscordEventMessageBuilder.FocusWindow(evt),
                // Whether the Break Room applies. Same predicate the break endpoints enforce, so
                // the Activity can never show a control the server would refuse.
                EventBreakPolicy.SupportsBreakRoom(evt),
                // False = the camp ended with no Time of Death, so there is no repop to announce.
                HnmTodRecorded(evt),
                // Per-camp bonus overrides, so the edit form reopens "Change DKP" with this
                // camp's own numbers instead of silently resetting it to the linkshell default.
                evt.HnmOpenBonusOverride,
                evt.HnmCloseBonusOverride,
                evt.HnmClaimBonusOverride,
                evt.HnmKillBonusOverride,
                evt.HnmPerWindowOverride)).ToList(),
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
            new ActivityHnmClaimsDto(
                MapHnmClaimSlices(hnmClaimStatsResult.Last7Days),
                MapHnmClaimSlices(hnmClaimStatsResult.Last30Days),
                MapHnmClaimSlices(hnmClaimStatsResult.AllTime)),
            MapHnmWindows(hnmWindowStatsResult),
            new ActivityOverviewStatsDto(
                linkshellMemberships.Count,
                activeEvents.Count,
                recentHistory.Count,
                activeEvents.Count(evt => evt.CommencementStartTime.HasValue)),
            addonConfigured,
            addonGloballyDisabled,
            adminOverrideActive,
            claimShieldGloballyDisabled));
    }

    // The compact per-linkshell monster catalog for the polled overview. Falls back to the built-in
    // defaults when the linkshell has no rows yet, which is what lets the client drop its own copies
    // of the cadence / cooldown tables entirely.
    // `repeatLeadsByMonster` is the linkshell's ENABLED Repeat-on-ToD leads keyed by lower-cased
    // monster name (null/absent = it has none). Each row is matched on every spelling of its spawn,
    // so a board first created under "Fafnir" still answers for the catalog's combined
    // "Fafnir/Nidhogg" row — the same rule HnmRecurringBoardService.FindAsync applies, and the one
    // thing that keeps the create form's toggle from opening OFF on a monster that is repeating.
    private static IReadOnlyList<ActivityMonsterSetupDto> MonsterSetupsFor(
        IReadOnlyDictionary<int, MonsterTimingMap> maps, int linkshellId,
        IReadOnlyDictionary<string, double>? repeatLeadsByMonster = null)
    {
        double? LeadFor(string? monsterName)
        {
            if (repeatLeadsByMonster is null || repeatLeadsByMonster.Count == 0)
            {
                return null;
            }
            foreach (var name in HnmConfig.MonsterMatchNamesLower(monsterName))
            {
                if (repeatLeadsByMonster.TryGetValue(name, out var lead))
                {
                    return lead;
                }
            }
            return null;
        }

        if (maps.TryGetValue(linkshellId, out var map) && map.IsSeeded)
        {
            return map.Rows
                .Select(row => new ActivityMonsterSetupDto(
                    row.MonsterName,
                    row.WindowCount,
                    row.WindowCadenceMinutes,
                    row.CooldownMinutes,
                    MonsterTimingDefaults.NormalizeCategory(row.Category),
                    LeadFor(row.MonsterName)))
                .ToList();
        }

        return MonsterTimingDefaults.BuildAll()
            .Select(timing => new ActivityMonsterSetupDto(
                timing.MonsterName,
                timing.WindowCount,
                timing.WindowCadenceMinutes,
                timing.CooldownMinutes,
                timing.Category,
                LeadFor(timing.MonsterName)))
            .ToList();
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
