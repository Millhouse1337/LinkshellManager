using LinkshellManagerDiscordApp.Data;
using LinkshellManagerDiscordApp.Models;
using Microsoft.EntityFrameworkCore;

namespace LinkshellManagerDiscordApp.Services;

// Materializes reviewed snapshot/window-event DKP into the local ledger after
// the rows have been posted to AttInput. The sheet remains the external sink,
// but DKP history/audit needs local rows to correct a prior snapshot entry.
public sealed class WindowEventDkpLedgerService
{
    private readonly ApplicationDbContext _db;
    private readonly ILogger<WindowEventDkpLedgerService> _logger;

    private readonly DkpLedgerWriter _dkpLedger;
    private readonly DkpPoolResolver _dkpPools;

    public WindowEventDkpLedgerService(
        ApplicationDbContext db,
        DkpLedgerWriter dkpLedger,
        DkpPoolResolver dkpPools,
        ILogger<WindowEventDkpLedgerService> logger)
    {
        _db = db;
        _dkpLedger = dkpLedger;
        _dkpPools = dkpPools;
        _logger = logger;
    }

    public async Task<int> EnsurePostedWindowEventLedgerEntriesAsync(
        int windowEventId,
        CancellationToken cancellationToken,
        int? firstAttInputMemberRowNumber = null)
    {
        var windowEvent = await _db.WindowEvents
            .Include(w => w.Snapshots).ThenInclude(s => s.Entries)
            .Include(w => w.MemberDkpOverrides)
            // The camp's Past Event and everyone on it: written at End Camp, reconciled here.
            .Include(w => w.CampEventHistory).ThenInclude(h => h!.AppUserEventHistories)
            .FirstOrDefaultAsync(w => w.Id == windowEventId, cancellationToken);

        if (windowEvent is null ||
            !windowEvent.PostedToSheetAt.HasValue ||
            !windowEvent.DkpAmount.HasValue ||
            !WindowEventEntryTypes.IsValid(windowEvent.EntryType))
        {
            return 0;
        }

        var combined = BuildCombinedMembers(windowEvent.Snapshots);
        if (combined.Count == 0)
        {
            return 0;
        }

        var membershipsWithUser = await _db.AppUserLinkshells
            .Where(link => link.LinkshellId == windowEvent.LinkshellId && link.AppUserId != null)
            .Join(_db.Users,
                  link => link.AppUserId,
                  user => user.Id,
                  (link, user) => new { Membership = link, User = user })
            .ToListAsync(cancellationToken);

        // Index every name a member might have been captured under: the
        // membership's own CharacterName plus the account's CharacterName and
        // alt names. This lets a snapshot credit a member who showed up on an
        // alt — the addon captures whatever character is standing in the
        // alliance, which is often not the one on the roster.
        // First-write-wins, so a membership's own
        // CharacterName takes precedence over an alt that resolves to a
        // different member (same rule PostAttendanceAsync uses).
        var membershipsByCharacterName = new Dictionary<string, AppUserLinkshell>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in membershipsWithUser)
        {
            if (string.IsNullOrWhiteSpace(pair.Membership.AppUserId)) continue;
            foreach (var candidate in new[]
                     {
                         pair.Membership.CharacterName,
                         pair.User.CharacterName,
                         pair.User.AltCharacterName1,
                         pair.User.AltCharacterName2,
                     })
            {
                if (string.IsNullOrWhiteSpace(candidate)) continue;
                var key = candidate.Trim();
                if (!membershipsByCharacterName.ContainsKey(key))
                {
                    membershipsByCharacterName[key] = pair.Membership;
                }
            }
        }

        // Camp handoffs (HnmCampReviewHandoffService) stamp the account straight onto the entry,
        // so prefer that over the name lookup. Addon "/lsm now" captures leave AppUserId null and
        // fall through to the by-name path below exactly as before.
        //
        // This is what keeps a camp from silently underpaying: the roster is keyed on AppUserId,
        // but a member whose in-game character isn't one of the four names indexed above would
        // resolve to nothing by name and simply never be credited — no error, no log.
        var membershipByAppUserId = membershipsWithUser
            .Where(pair => !string.IsNullOrWhiteSpace(pair.Membership.AppUserId))
            .GroupBy(pair => pair.Membership.AppUserId!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().Membership, StringComparer.OrdinalIgnoreCase);

        var candidates = combined
            .Select(item => new
            {
                item.Snapshot,
                item.Entry,
                Membership = (!string.IsNullOrWhiteSpace(item.Entry.AppUserId)
                        ? membershipByAppUserId.GetValueOrDefault(item.Entry.AppUserId!)
                        : null)
                    ?? membershipsByCharacterName.GetValueOrDefault(item.Entry.CharacterName.Trim())
            })
            .Where(item => item.Membership is not null && !string.IsNullOrWhiteSpace(item.Membership.AppUserId))
            .ToList();

        // The per-character amounts the review settled on. Hoisted above the ledger work because
        // the archive reconcile below needs the same numbers.
        var defaultAmount = windowEvent.DkpAmount.Value;
        // A priced camp is paid from its CAPTURES: a member is owed the sum of what each window
        // they were scanned in pays, plus the bonus capture that carries what no window does. The
        // per-member override rows are not consulted at all there — the handoff writes none, and
        // the officer edits the captures.
        var overridesByName = windowEvent.PerCaptureDkp
            ? WindowEventCaptureDkp.SumByCharacter(windowEvent.Snapshots)
            : windowEvent.MemberDkpOverrides
                .Where(o => !string.IsNullOrWhiteSpace(o.CharacterName))
                .GroupBy(o => o.CharacterName.Trim(), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First().DkpAmount, StringComparer.OrdinalIgnoreCase);

        double AmountForCharacter(string? characterName)
            => !string.IsNullOrWhiteSpace(characterName) &&
               overridesByName.TryGetValue(characterName.Trim(), out var v)
                ? v
                : defaultAmount;

        // A camp-sourced review row archives an EventHistory. It is written at END CAMP now (see
        // HnmCampReviewHandoffService) rather than here, because a recycled board otherwise left
        // the camp with no past-event record at all until somebody reviewed it. What is left for
        // Post is to make that archive agree with the review: the officer may have removed
        // someone, added someone, or changed an amount.
        //
        // Deliberately BEFORE the already-credited filter below. That filter exists so a second
        // Post cannot double-credit the ledger, and it short-circuits the whole method — but an
        // edit-then-re-post that credits nobody new must still correct the archive.
        var campHistory = ResolveCampEventHistory(windowEvent);
        if (campHistory is not null)
        {
            SyncCampArchiveRoster(campHistory, windowEvent, combined, membershipsByCharacterName,
                membershipByAppUserId, AmountForCharacter);
            await _db.SaveChangesAsync(cancellationToken);
        }

        if (candidates.Count == 0)
        {
            return 0;
        }

        var candidateAppUserIds = candidates
            .Select(item => item.Membership!.AppUserId!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var existingAppUserIds = await _db.DkpLedgerEntries
            .Where(entry => entry.SourceWindowEventId == windowEvent.Id && entry.AppUserId != null)
            .Select(entry => entry.AppUserId!)
            .ToListAsync(cancellationToken);
        var existingSet = existingAppUserIds.ToHashSet(StringComparer.OrdinalIgnoreCase);

        candidates = candidates
            .Where(item => !existingSet.Contains(item.Membership!.AppUserId!))
            .ToList();
        if (candidates.Count == 0)
        {
            return 0;
        }


        // A window event's entry type ("Kings Camp", "Kill", …) is written straight into the
        // ledger's EventType column so it resolves to a pool, but those camp tags aren't assignable
        // on the DKP grouping card — they fall through to the default pool like any unmapped type.
        var windowEventPool = DkpPoolRef.Derived(windowEvent.EntryType);

        var written = 0;
        foreach (var item in candidates)
        {
            var membership = item.Membership!;
            int? attInputRowNumber = null;
            if (firstAttInputMemberRowNumber.HasValue)
            {
                var combinedIndex = combined.FindIndex(c =>
                    string.Equals(c.Entry.CharacterName.Trim(), item.Entry.CharacterName.Trim(), StringComparison.OrdinalIgnoreCase));
                if (combinedIndex >= 0)
                {
                    attInputRowNumber = firstAttInputMemberRowNumber.Value + combinedIndex;
                }
            }

            var amount = AmountForCharacter(item.Entry.CharacterName);

            await _dkpLedger.AppendAsync(
                membership,
                "SnapshotEarned",
                amount,
                item.Snapshot.CapturedAtUtc,
                windowEventPool,
                new DkpEntryContext(
                    CharacterName: membership.CharacterName,
                    EventName: string.IsNullOrWhiteSpace(windowEvent.Name) ? "Window Event" : windowEvent.Name,
                    EventType: windowEvent.EntryType,
                    EventLocation: campHistory is not null ? windowEvent.CampEventLocation : item.Entry.Zone,
                    EventStartTime: campHistory is not null ? windowEvent.CampStartedAtUtc : windowEvent.FirstCapturedAtUtc,
                    EventEndTime: campHistory is not null ? windowEvent.CampEndedAtUtc : windowEvent.LastCapturedAtUtc,
                    Details: campHistory is not null
                        ? $"HNM camp DKP, reviewed and posted as Window Event #{windowEvent.Id}."
                        : $"DKP earned from posted snapshot Window Event #{windowEvent.Id}.",
                    SourceWindowEventId: windowEvent.Id,
                    AttInputRowNumber: attInputRowNumber,
                    EventHistory: campHistory),
                cancellationToken);
            written++;
        }

        await _db.SaveChangesAsync(cancellationToken);

        _logger.LogInformation(
            "Window Event ledger materialized: event {WindowEventId} -> {Count} DKP rows.",
            windowEvent.Id,
            written);
        return written;
    }

    // Reconciles the DKP of an ALREADY-posted window event after its amount /
    // entry type / per-member overrides were edited. EnsurePosted... only inserts
    // missing rows, so it can't correct existing ones — this walks the existing
    // SnapshotEarned ledger entries for the window event, recomputes each
    // member's amount (override or default), applies the delta to LinkshellDkp,
    // and rewrites the entry amount + type. Was previously done by the (now
    // removed) AttInput sheet job; this is the DB-only equivalent.
    public async Task<int> ReconcilePostedWindowEventLedgerAsync(int windowEventId, CancellationToken cancellationToken)
    {
        var windowEvent = await _db.WindowEvents
            .Include(w => w.MemberDkpOverrides)
            // The captures carry the money on a priced camp, so an edit to one of them is exactly
            // what this method exists to reconcile.
            .Include(w => w.Snapshots).ThenInclude(s => s.Entries)
            .FirstOrDefaultAsync(w => w.Id == windowEventId, cancellationToken);

        if (windowEvent is null ||
            !windowEvent.PostedToSheetAt.HasValue ||
            !windowEvent.DkpAmount.HasValue ||
            !WindowEventEntryTypes.IsValid(windowEvent.EntryType))
        {
            return 0;
        }

        var defaultAmount = windowEvent.DkpAmount.Value;
        var newEntryType = windowEvent.EntryType!;
        // The per-character amounts, from whichever of the two shapes this row prices in: the sum
        // of a member's capture amounts on a priced camp, their per-member override otherwise. Same
        // dictionary either way, so the alt-name fallback below applies to both — a camp scanned
        // someone on an alt just as readily as a "/lsm now" capture did.
        var overridesByName = windowEvent.PerCaptureDkp
            ? WindowEventCaptureDkp.SumByCharacter(windowEvent.Snapshots)
            : windowEvent.MemberDkpOverrides
                .Where(o => !string.IsNullOrWhiteSpace(o.CharacterName))
                .GroupBy(o => o.CharacterName.Trim(), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First().DkpAmount, StringComparer.OrdinalIgnoreCase);

        var ledgerEntries = await _db.DkpLedgerEntries
            .Where(entry => entry.SourceWindowEventId == windowEventId && entry.AppUserId != null)
            .ToListAsync(cancellationToken);
        if (ledgerEntries.Count == 0)
        {
            return 0;
        }

        var appUserIds = ledgerEntries
            .Select(entry => entry.AppUserId!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var memberships = await _db.AppUserLinkshells
            .Where(link => link.LinkshellId == windowEvent.LinkshellId && appUserIds.Contains(link.AppUserId!))
            .ToListAsync(cancellationToken);
        var membershipByAppUserId = memberships
            .Where(link => !string.IsNullOrWhiteSpace(link.AppUserId))
            .GroupBy(link => link.AppUserId!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

        // EVERY name each credited account could have been captured under.
        //
        // This exists because the two halves of this service key overrides DIFFERENTLY. Ensure
        // looks an override up by the SNAPSHOT ENTRY name — the character actually standing in the
        // alliance — while the ledger row it writes stores the MEMBERSHIP name. For anyone who
        // showed up on an alt those are different strings, so an override applied at post time was
        // silently dropped here on the next edit and that member snapped back to the event default.
        //
        // Survivable while overrides were rare and hand-typed. The Misc rate materializes them
        // automatically for a whole class of members, so it would now be routine.
        var nameRows = await _db.AppUserLinkshells
            .Where(link => link.LinkshellId == windowEvent.LinkshellId && appUserIds.Contains(link.AppUserId!))
            .Join(_db.Users, link => link.AppUserId, user => user.Id, (link, user) => new
            {
                link.AppUserId,
                MembershipName = link.CharacterName,
                AccountName = user.CharacterName,
                Alt1 = user.AltCharacterName1,
                Alt2 = user.AltCharacterName2,
            })
            .ToListAsync(cancellationToken);

        var candidateNames = nameRows
            .Where(item => !string.IsNullOrWhiteSpace(item.AppUserId))
            .GroupBy(item => item.AppUserId!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group
                    .SelectMany(item => new[] { item.MembershipName, item.AccountName, item.Alt1, item.Alt2 })
                    .Where(name => !string.IsNullOrWhiteSpace(name))
                    .Select(name => name!.Trim())
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList(),
                StringComparer.OrdinalIgnoreCase);

        // The ledger row's own name wins, so nothing that already resolved changes; the account's
        // other names are consulted only as a fallback.
        double AmountForEntry(DkpLedgerEntry entry)
        {
            if (!string.IsNullOrWhiteSpace(entry.CharacterName) &&
                overridesByName.TryGetValue(entry.CharacterName.Trim(), out var direct))
            {
                return direct;
            }

            if (!string.IsNullOrWhiteSpace(entry.AppUserId) &&
                candidateNames.TryGetValue(entry.AppUserId, out var names))
            {
                foreach (var name in names)
                {
                    if (overridesByName.TryGetValue(name, out var viaAlt)) return viaAlt;
                }
            }

            return defaultAmount;
        }

        // The edit may have changed the entry type, which changes which pool these rows belong to.
        var newPoolId = await _dkpPools.ResolveAsync(windowEvent.LinkshellId, newEntryType, cancellationToken);

        // A camp-sourced event also wrote an EventHistory at post time. Its per-member EventDkp
        // rows are the Event History / DKP sheet view of this payout, so an edit has to move them
        // too — otherwise history keeps reporting the pre-edit amounts forever.
        var historyIds = ledgerEntries
            .Where(entry => entry.EventHistoryId.HasValue)
            .Select(entry => entry.EventHistoryId!.Value)
            .Distinct()
            .ToList();
        var historyRows = historyIds.Count == 0
            ? new List<AppUserEventHistory>()
            : await _db.AppUserEventHistories
                .Where(row => historyIds.Contains(row.EventHistoryId))
                .ToListAsync(cancellationToken);

        foreach (var entry in ledgerEntries)
        {
            var newAmountForEntry = AmountForEntry(entry);
            AppUserLinkshell? membership = null;
            if (!string.IsNullOrWhiteSpace(entry.AppUserId))
            {
                membershipByAppUserId.TryGetValue(entry.AppUserId, out membership);
            }

            // Amend moves the balance by (new - old), so a no-op amount is genuinely a no-op.
            _dkpLedger.Amend(entry, newAmountForEntry, newDetails: null, membership);
            entry.EventType = newEntryType;
            _dkpLedger.Repoint(entry, newPoolId);

            foreach (var row in historyRows.Where(row =>
                         row.EventHistoryId == entry.EventHistoryId &&
                         string.Equals(row.AppUserId, entry.AppUserId, StringComparison.OrdinalIgnoreCase)))
            {
                row.EventDkp = newAmountForEntry;
            }
        }

        await _db.SaveChangesAsync(cancellationToken);
        _logger.LogInformation(
            "Window Event ledger reconciled after edit: event {WindowEventId} -> {Count} entries.",
            windowEventId, ledgerEntries.Count);
        return ledgerEntries.Count;
    }

    public async Task<int> EnsurePostedWindowEventLedgerEntriesForLinkshellAsync(int linkshellId, CancellationToken cancellationToken)
    {
        var windowEventIds = await _db.WindowEvents
            .Where(w => w.LinkshellId == linkshellId &&
                        w.PostedToSheetAt.HasValue &&
                        w.DkpAmount.HasValue &&
                        w.EntryType != null)
            .Select(w => w.Id)
            .ToListAsync(cancellationToken);

        var created = 0;
        foreach (var windowEventId in windowEventIds)
        {
            created += await EnsurePostedWindowEventLedgerEntriesAsync(windowEventId, cancellationToken);
        }
        return created;
    }

    // The Past Event this camp was archived as, or null for an ordinary addon snapshot row (those
    // aren't camps and get no history, same as before).
    //
    // Normally it already exists: End Camp writes it (HnmCampReviewHandoffService) so a recycled
    // board still leaves a past-event record behind while its payout waits for review. The build
    // below is the BACKFILL for review rows staged before that was true — without it, every camp
    // sitting unposted at deploy time would post with no archive at all.
    //
    // Everything comes off the WindowEvent's Camp* columns rather than SourceEvent: the camp row
    // is RECYCLED for the next pop, so by post time its StartTime points at a future repop and
    // CommencementStartTime is null. SourceEventId can also be null already if the camp was
    // deleted — the review row still has to be postable.
    private EventHistory? ResolveCampEventHistory(WindowEvent windowEvent)
    {
        if (windowEvent.CampEventHistory is not null) return windowEvent.CampEventHistory;

        if (windowEvent.SourceEventId is null && windowEvent.CampEndedAtUtc is null)
        {
            return null;
        }

        var history = new EventHistory
        {
            LinkshellId = windowEvent.LinkshellId,
            EventName = windowEvent.Name,
            EventType = windowEvent.CampEventType,
            EventLocation = windowEvent.CampEventLocation,
            StartDate = windowEvent.CampStartedAtUtc?.Date,
            StartTime = windowEvent.CampStartedAtUtc,
            EndTime = windowEvent.CampEndedAtUtc,
            CommencementStartTime = windowEvent.CampStartedAtUtc,
            Duration = null,
            EventDkp = null,
            Details = $"HNM camp, reviewed and posted as Window Event #{windowEvent.Id}.",
            CountsTowardActive = true,
            TimeStamp = windowEvent.PostedToSheetAt ?? windowEvent.CampEndedAtUtc,
            AppUserEventHistories = new List<AppUserEventHistory>(),
        };
        _db.EventHistories.Add(history);
        // Linked so a LATER re-post reconciles this same row instead of archiving the camp twice.
        windowEvent.CampEventHistory = history;
        return history;
    }

    // Makes the camp's Past Event roster say exactly what the review says.
    //
    // End Camp stages the archive from the camp's OWN proposal, which is the right thing to show
    // while the payout is pending. Review then edits it: someone gets removed, someone gets added,
    // an amount changes. Without this the archive would keep quoting the proposal forever — a
    // past event crediting a member the officer had struck off.
    //
    // Keyed on ACCOUNT where there is one, because AppUserEventHistory is uniquely indexed on
    // (EventHistoryId, AppUserId): matching by character name would insert a second row for a
    // member who was scanned on their main at End Camp and on an alt afterwards, and the save
    // would throw. Account-less rows (an unsynced placeholder) fall back to the name, which is all
    // they have.
    private static void SyncCampArchiveRoster(
        EventHistory history,
        WindowEvent windowEvent,
        List<(AttendanceSnapshot Snapshot, AttendanceSnapshotEntry Entry)> combined,
        Dictionary<string, AppUserLinkshell> membershipsByCharacterName,
        Dictionary<string, AppUserLinkshell> membershipByAppUserId,
        Func<string?, double> amountForCharacter)
    {
        var byAppUserId = new Dictionary<string, AppUserEventHistory>(StringComparer.OrdinalIgnoreCase);
        var byCharacterName = new Dictionary<string, AppUserEventHistory>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in history.AppUserEventHistories)
        {
            if (!string.IsNullOrWhiteSpace(row.AppUserId)) byAppUserId[row.AppUserId] = row;
            else if (!string.IsNullOrWhiteSpace(row.CharacterName)) byCharacterName[row.CharacterName.Trim()] = row;
        }

        var kept = new HashSet<AppUserEventHistory>();
        foreach (var item in combined)
        {
            var characterName = item.Entry.CharacterName.Trim();
            if (characterName.Length == 0) continue;

            var membership = (!string.IsNullOrWhiteSpace(item.Entry.AppUserId)
                    ? membershipByAppUserId.GetValueOrDefault(item.Entry.AppUserId!)
                    : null)
                ?? membershipsByCharacterName.GetValueOrDefault(characterName);
            var appUserId = membership?.AppUserId;
            var amount = amountForCharacter(characterName);

            AppUserEventHistory? row = null;
            if (!string.IsNullOrWhiteSpace(appUserId)) byAppUserId.TryGetValue(appUserId, out row);
            if (row is null) byCharacterName.TryGetValue(characterName, out row);

            if (row is null)
            {
                row = new AppUserEventHistory
                {
                    CharacterName = characterName,
                    StartTime = windowEvent.CampStartedAtUtc,
                    Duration = null,
                    IsQuickJoin = true,
                    IsVerified = true,
                    ActiveCredit = true,
                };
                history.AppUserEventHistories.Add(row);
                if (!string.IsNullOrWhiteSpace(appUserId)) byAppUserId[appUserId] = row;
                else byCharacterName[characterName] = row;
            }
            // A row already claimed by an earlier entry stays claimed: two characters resolving to
            // one account is one attendance, and keeping the larger amount matches how End Camp
            // and the review card fold the same collision.
            else if (kept.Contains(row) && amount <= (row.EventDkp ?? 0d))
            {
                continue;
            }

            row.AppUserId = appUserId ?? row.AppUserId;
            row.CharacterName = characterName;
            row.JobName = item.Entry.MainJob ?? row.JobName;
            row.SubJobName = item.Entry.SubJob ?? row.SubJobName;
            row.EventDkp = amount;
            kept.Add(row);
        }

        // Anyone the review struck off. Their attendance was the camp's proposal, and the officer
        // said no -- so the archive must not keep crediting them.
        foreach (var row in history.AppUserEventHistories.Where(r => !kept.Contains(r)).ToList())
        {
            history.AppUserEventHistories.Remove(row);
        }
    }

    private static List<(AttendanceSnapshot Snapshot, AttendanceSnapshotEntry Entry)> BuildCombinedMembers(IEnumerable<AttendanceSnapshot> snapshots)
    {
        return snapshots
            .Where(s => s.SnapshotStatus == AttendanceSnapshotStatuses.Active)
            .SelectMany(s => s.Entries.Select(e => new { Snapshot = s, Entry = e }))
            .Where(x => !string.IsNullOrWhiteSpace(x.Entry.CharacterName))
            .GroupBy(x => x.Entry.CharacterName.Trim(), StringComparer.OrdinalIgnoreCase)
            .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
            .Select(g =>
            {
                var latest = g.OrderByDescending(x => x.Snapshot.CapturedAtUtc).First();
                return (latest.Snapshot, latest.Entry);
            })
            .ToList();
    }
}
