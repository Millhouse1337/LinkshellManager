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

    public WindowEventDkpLedgerService(ApplicationDbContext db, ILogger<WindowEventDkpLedgerService> logger)
    {
        _db = db;
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

        var nextSequenceByAppUserId = await _db.DkpLedgerEntries
            .Where(entry => entry.LinkshellId == windowEvent.LinkshellId &&
                            entry.AppUserId != null &&
                            candidateAppUserIds.Contains(entry.AppUserId))
            .GroupBy(entry => entry.AppUserId!)
            .Select(group => new { AppUserId = group.Key, NextSequence = group.Max(entry => entry.Sequence) + 1 })
            .ToDictionaryAsync(item => item.AppUserId, item => item.NextSequence, StringComparer.OrdinalIgnoreCase, cancellationToken);

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

        var ledgerEntries = new List<DkpLedgerEntry>();
        foreach (var item in candidates)
        {
            var membership = item.Membership!;
            var appUserId = membership.AppUserId!;
            var sequence = nextSequenceByAppUserId.GetValueOrDefault(appUserId, 1);
            nextSequenceByAppUserId[appUserId] = sequence + 1;
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
            membership.LinkshellDkp = (membership.LinkshellDkp ?? 0d) + amount;
            ledgerEntries.Add(new DkpLedgerEntry
            {
                AppUserId = appUserId,
                LinkshellId = windowEvent.LinkshellId,
                EntryType = "SnapshotEarned",
                Amount = amount,
                Sequence = sequence,
                OccurredAt = item.Snapshot.CapturedAtUtc,
                CharacterName = membership.CharacterName,
                EventName = string.IsNullOrWhiteSpace(windowEvent.Name) ? "Window Event" : windowEvent.Name,
                EventType = windowEvent.EntryType,
                EventLocation = item.Entry.Zone,
                EventStartTime = windowEvent.FirstCapturedAtUtc,
                EventEndTime = windowEvent.LastCapturedAtUtc,
                Details = $"DKP earned from posted snapshot Window Event #{windowEvent.Id}.",
                SourceWindowEventId = windowEvent.Id,
                AttInputRowNumber = attInputRowNumber
            });
        }

        await _db.DkpLedgerEntries.AddRangeAsync(ledgerEntries, cancellationToken);
        await _db.SaveChangesAsync(cancellationToken);

        _logger.LogInformation(
            "Window Event ledger materialized: event {WindowEventId} -> {Count} DKP rows.",
            windowEvent.Id,
            ledgerEntries.Count);
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
