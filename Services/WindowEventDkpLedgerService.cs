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
        // alt names. This lets a linkshell/zone-scope snapshot credit a member
        // who showed up on an alt. First-write-wins, so a membership's own
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

        var candidates = combined
            .Select(item => new
            {
                item.Snapshot,
                item.Entry,
                Membership = membershipsByCharacterName.GetValueOrDefault(item.Entry.CharacterName.Trim())
            })
            .Where(item => item.Membership is not null && !string.IsNullOrWhiteSpace(item.Membership.AppUserId))
            .ToList();

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


        var defaultAmount = windowEvent.DkpAmount.Value;
        var overridesByName = windowEvent.MemberDkpOverrides
            .Where(o => !string.IsNullOrWhiteSpace(o.CharacterName))
            .GroupBy(o => o.CharacterName.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().DkpAmount, StringComparer.OrdinalIgnoreCase);

        double AmountForCharacter(string? characterName)
            => !string.IsNullOrWhiteSpace(characterName) &&
               overridesByName.TryGetValue(characterName.Trim(), out var v)
                ? v
                : defaultAmount;

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

            await _dkpLedger.AppendAsync(
                membership,
                "SnapshotEarned",
                AmountForCharacter(item.Entry.CharacterName),
                item.Snapshot.CapturedAtUtc,
                windowEventPool,
                new DkpEntryContext(
                    CharacterName: membership.CharacterName,
                    EventName: string.IsNullOrWhiteSpace(windowEvent.Name) ? "Window Event" : windowEvent.Name,
                    EventType: windowEvent.EntryType,
                    EventLocation: item.Entry.Zone,
                    EventStartTime: windowEvent.FirstCapturedAtUtc,
                    EventEndTime: windowEvent.LastCapturedAtUtc,
                    Details: $"DKP earned from posted snapshot Window Event #{windowEvent.Id}.",
                    SourceWindowEventId: windowEvent.Id,
                    AttInputRowNumber: attInputRowNumber),
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
        var overridesByName = windowEvent.MemberDkpOverrides
            .Where(o => !string.IsNullOrWhiteSpace(o.CharacterName))
            .GroupBy(o => o.CharacterName.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().DkpAmount, StringComparer.OrdinalIgnoreCase);

        double AmountForCharacter(string? characterName)
            => !string.IsNullOrWhiteSpace(characterName) &&
               overridesByName.TryGetValue(characterName.Trim(), out var v)
                ? v
                : defaultAmount;

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

        // The edit may have changed the entry type, which changes which pool these rows belong to.
        var newPoolId = await _dkpPools.ResolveAsync(windowEvent.LinkshellId, newEntryType, cancellationToken);

        foreach (var entry in ledgerEntries)
        {
            var newAmountForEntry = AmountForCharacter(entry.CharacterName);
            AppUserLinkshell? membership = null;
            if (!string.IsNullOrWhiteSpace(entry.AppUserId))
            {
                membershipByAppUserId.TryGetValue(entry.AppUserId, out membership);
            }

            // Amend moves the balance by (new - old), so a no-op amount is genuinely a no-op.
            _dkpLedger.Amend(entry, newAmountForEntry, newDetails: null, membership);
            entry.EventType = newEntryType;
            _dkpLedger.Repoint(entry, newPoolId);
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
