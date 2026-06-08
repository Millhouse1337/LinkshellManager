using LinkshellManagerDiscordApp.Data;
using Microsoft.EntityFrameworkCore;

namespace LinkshellManagerDiscordApp.Services;

// Computes each member's Active/Inactive activity state from event attendance,
// on read (so it always reflects the current per-linkshell config + any credit
// reconciliation). The rule is a streak hysteresis over the member's sequence of
// "counting" events (EventHistory.CountsTowardActive), oldest → newest, that
// ended after the member joined:
//   * a credited AppUserEventHistory.ActiveCredit row = an ATTENDANCE
//   * anything else (no row, or row with credit unchecked) = an ABSENCE
//   * InactiveAfterAbsences consecutive absences  -> Inactive
//   * ActiveAfterAttendances consecutive attendances -> Active (back)
// Members start Active; the badge is only meaningful when the linkshell opts in
// (EnableActivityTracking) — when off this returns an empty map and callers hide it.
public sealed class MemberActivityService
{
    private readonly ApplicationDbContext _db;

    public MemberActivityService(ApplicationDbContext db)
    {
        _db = db;
    }

    // appUserId -> isActive for every member of the linkshell. Empty when activity
    // tracking is disabled for the linkshell.
    public async Task<Dictionary<string, bool>> ComputeActiveByAppUserAsync(
        int linkshellId, CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

        var config = await _db.Linkshells
            .AsNoTracking()
            .Where(l => l.Id == linkshellId)
            .Select(l => new { l.EnableActivityTracking, l.InactiveAfterAbsences, l.ActiveAfterAttendances })
            .FirstOrDefaultAsync(cancellationToken);
        if (config is null || !config.EnableActivityTracking)
        {
            return result;
        }

        var inactiveAfter = Math.Max(1, config.InactiveAfterAbsences);
        var activeAfter = Math.Max(1, config.ActiveAfterAttendances);

        var members = await _db.AppUserLinkshells
            .AsNoTracking()
            .Where(m => m.LinkshellId == linkshellId && m.AppUserId != null)
            .Select(m => new { AppUserId = m.AppUserId!, m.DateJoined })
            .ToListAsync(cancellationToken);
        if (members.Count == 0)
        {
            return result;
        }

        // Counting events for this linkshell, oldest first.
        var countingEvents = await _db.EventHistories
            .AsNoTracking()
            .Where(h => h.LinkshellId == linkshellId && h.CountsTowardActive && h.EndTime != null)
            .OrderBy(h => h.EndTime)
            .Select(h => new { h.Id, h.EndTime })
            .ToListAsync(cancellationToken);

        if (countingEvents.Count == 0)
        {
            // No counting events yet → everyone is Active.
            foreach (var member in members)
            {
                result[member.AppUserId] = true;
            }
            return result;
        }

        var historyIds = countingEvents.Select(e => e.Id).ToList();
        var creditedRows = await _db.AppUserEventHistories
            .AsNoTracking()
            .Where(r => historyIds.Contains(r.EventHistoryId) && r.ActiveCredit && r.AppUserId != null)
            .Select(r => new { r.EventHistoryId, AppUserId = r.AppUserId! })
            .ToListAsync(cancellationToken);
        var creditedByEvent = creditedRows
            .GroupBy(r => r.EventHistoryId)
            .ToDictionary(
                g => g.Key,
                g => new HashSet<string>(g.Select(x => x.AppUserId), StringComparer.OrdinalIgnoreCase));

        foreach (var member in members)
        {
            var isActive = true; // members start Active
            var absenceStreak = 0;
            var attendanceStreak = 0;

            foreach (var ev in countingEvents)
            {
                // Only events that ended after the member joined count for/against them.
                if (member.DateJoined.HasValue && ev.EndTime.HasValue && ev.EndTime.Value < member.DateJoined.Value)
                {
                    continue;
                }

                var attended = creditedByEvent.TryGetValue(ev.Id, out var credited)
                    && credited.Contains(member.AppUserId);

                if (attended)
                {
                    attendanceStreak++;
                    absenceStreak = 0;
                    if (!isActive && attendanceStreak >= activeAfter)
                    {
                        isActive = true;
                    }
                }
                else
                {
                    absenceStreak++;
                    attendanceStreak = 0;
                    if (isActive && absenceStreak >= inactiveAfter)
                    {
                        isActive = false;
                    }
                }
            }

            result[member.AppUserId] = isActive;
        }

        return result;
    }

    // Single-member convenience (detail views). Defaults to Active when tracking is
    // off or the member has no counting history.
    public async Task<bool> IsActiveAsync(int linkshellId, string appUserId, CancellationToken cancellationToken)
    {
        var map = await ComputeActiveByAppUserAsync(linkshellId, cancellationToken);
        return !map.TryGetValue(appUserId, out var active) || active;
    }

    // Persists the computed result onto each member's manual Status field so there
    // is a SINGLE status (Active/Pending/Inactive) everywhere. The attendance rule
    // owns Active <-> Inactive; "Pending" is a manual, sticky state the rule never
    // overwrites. No-op when activity tracking is disabled (the compute returns an
    // empty map). Call after anything that changes the streak: event close, credit
    // reconciliation, or a thresholds/enable change. Returns the number changed.
    public async Task<int> ApplyComputedStatusAsync(int linkshellId, CancellationToken cancellationToken)
    {
        var map = await ComputeActiveByAppUserAsync(linkshellId, cancellationToken);
        if (map.Count == 0)
        {
            return 0; // tracking off, or no app-linked members → leave Status alone
        }

        var members = await _db.AppUserLinkshells
            .Where(m => m.LinkshellId == linkshellId && m.AppUserId != null)
            .ToListAsync(cancellationToken);

        var changed = 0;
        foreach (var member in members)
        {
            if (member.AppUserId is null || !map.TryGetValue(member.AppUserId, out var active))
            {
                continue;
            }
            // Pending is a manual state leadership controls; never auto-overwrite it.
            if (string.Equals(member.Status, "Pending", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            var desired = active ? "Active" : "Inactive";
            if (!string.Equals(member.Status, desired, StringComparison.Ordinal))
            {
                member.Status = desired;
                changed++;
            }
        }

        if (changed > 0)
        {
            await _db.SaveChangesAsync(cancellationToken);
        }
        return changed;
    }
}
