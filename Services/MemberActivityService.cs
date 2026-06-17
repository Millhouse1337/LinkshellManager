using LinkshellManagerDiscordApp.Data;
using Microsoft.EntityFrameworkCore;

namespace LinkshellManagerDiscordApp.Services;

// A member's current trailing streaks over counting events. Mutually exclusive:
// the most recent run is either credited (Credit > 0) or absent (Absent > 0).
public readonly record struct MemberStreaks(int Credit, int Absent);

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

        var trackingOn = await _db.Linkshells
            .AsNoTracking()
            .Where(l => l.Id == linkshellId)
            .Select(l => l.EnableActivityTracking)
            .FirstOrDefaultAsync(cancellationToken);
        if (!trackingOn)
        {
            // Tracking off → no auto Active/Inactive; callers hide the badge.
            return result;
        }

        var machine = await ComputeActivityMachineAsync(linkshellId, cancellationToken);
        foreach (var kv in machine)
        {
            result[kv.Key] = kv.Value.Active;
        }
        return result;
    }

    // The single source of truth for both the Active/Inactive STATE and the two
    // streak columns, so they always agree. Per the linkshell's config thresholds
    // (InactiveAfterAbsences / ActiveAfterAttendances), processing counting events
    // oldest → newest with each member starting ACTIVE:
    //   * While ACTIVE: count consecutive ABSENCES in `Absent` (an attendance resets
    //     it to 0; `Credit` stays 0). On reaching InactiveAfterAbsences → flip to
    //     Inactive and reset BOTH counters to 0 ("until the next event").
    //   * While INACTIVE: count consecutive CREDITS in `Credit` (an absence resets it
    //     to 0; `Absent` stays 0). On reaching ActiveAfterAttendances → flip to Active
    //     and reset BOTH counters to 0.
    // Computed regardless of EnableActivityTracking so the roster can always show the
    // numbers; thresholds fall back to 3 / 2 if the linkshell row is missing.
    private async Task<Dictionary<string, (bool Active, int Credit, int Absent)>> ComputeActivityMachineAsync(
        int linkshellId, CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, (bool Active, int Credit, int Absent)>(StringComparer.OrdinalIgnoreCase);

        var config = await _db.Linkshells
            .AsNoTracking()
            .Where(l => l.Id == linkshellId)
            .Select(l => new { l.InactiveAfterAbsences, l.ActiveAfterAttendances })
            .FirstOrDefaultAsync(cancellationToken);
        var inactiveAfter = Math.Max(1, config?.InactiveAfterAbsences ?? 3);
        var activeAfter = Math.Max(1, config?.ActiveAfterAttendances ?? 2);

        var members = await _db.AppUserLinkshells
            .AsNoTracking()
            .Where(m => m.LinkshellId == linkshellId && m.AppUserId != null)
            .Select(m => new
            {
                AppUserId = m.AppUserId!,
                m.DateJoined,
                m.ManualActiveCreditStreak,
                m.ManualAbsentStreak,
                m.ManualStreakSetAt
            })
            .ToListAsync(cancellationToken);
        if (members.Count == 0)
        {
            return result;
        }

        var countingEvents = await _db.EventHistories
            .AsNoTracking()
            .Where(h => h.LinkshellId == linkshellId && h.CountsTowardActive && h.EndTime != null)
            .OrderBy(h => h.EndTime)
            .Select(h => new { h.Id, h.EndTime })
            .ToListAsync(cancellationToken);

        var historyIds = countingEvents.Select(e => e.Id).ToList();
        var creditedRows = historyIds.Count == 0
            ? new List<(int EventHistoryId, string AppUserId)>()
            : (await _db.AppUserEventHistories
                .AsNoTracking()
                .Where(r => historyIds.Contains(r.EventHistoryId) && r.ActiveCredit && r.AppUserId != null)
                .Select(r => new { r.EventHistoryId, AppUserId = r.AppUserId! })
                .ToListAsync(cancellationToken))
                .Select(r => (r.EventHistoryId, r.AppUserId))
                .ToList();
        var creditedByEvent = creditedRows
            .GroupBy(r => r.EventHistoryId)
            .ToDictionary(
                g => g.Key,
                g => new HashSet<string>(g.Select(x => x.AppUserId), StringComparer.OrdinalIgnoreCase));

        foreach (var member in members)
        {
            // Seed the machine: a manual override is a baseline set at
            // ManualStreakSetAt that subsequent events build on (so a manually-set
            // credit accumulates with later attendance). The seed maps the override
            // value to a starting (state, credit, absent); events that ended at/before
            // the seed time are superseded by it. With no override, members start
            // Active and replay everything after they joined.
            var active = true;
            var absent = 0;
            var credit = 0;
            DateTime? cutoff = member.DateJoined; // exclusive lower bound: events must end after this

            if (member.ManualActiveCreditStreak.HasValue)
            {
                var v = Math.Max(0, member.ManualActiveCreditStreak.Value);
                if (v >= activeAfter) { active = true; credit = 0; }   // already reactivated
                else { active = false; credit = v; }                   // inactive, building credit
                cutoff = member.ManualStreakSetAt ?? member.DateJoined;
            }
            else if (member.ManualAbsentStreak.HasValue)
            {
                var v = Math.Max(0, member.ManualAbsentStreak.Value);
                if (v >= inactiveAfter) { active = false; absent = 0; } // already went inactive
                else { active = true; absent = v; }                    // active, building absences
                cutoff = member.ManualStreakSetAt ?? member.DateJoined;
            }

            foreach (var ev in countingEvents) // oldest -> newest
            {
                // Skip events at/before the cutoff (member join, or the manual seed time).
                if (cutoff.HasValue && ev.EndTime.HasValue && ev.EndTime.Value <= cutoff.Value)
                {
                    continue;
                }

                var attended = creditedByEvent.TryGetValue(ev.Id, out var credited)
                    && credited.Contains(member.AppUserId);

                if (active)
                {
                    credit = 0; // active-credit column is dormant while active
                    if (attended)
                    {
                        absent = 0; // one attendance resets the absence streak
                    }
                    else
                    {
                        absent++;
                        if (absent >= inactiveAfter)
                        {
                            active = false;
                            absent = 0; // both columns reset until the next event
                            credit = 0;
                        }
                    }
                }
                else
                {
                    absent = 0; // absent-streak column is dormant while inactive
                    if (attended)
                    {
                        credit++;
                        if (credit >= activeAfter)
                        {
                            active = true;
                            credit = 0; // both columns reset until the next event
                            absent = 0;
                        }
                    }
                    else
                    {
                        credit = 0; // one absence resets the active-credit streak
                    }
                }
            }

            result[member.AppUserId] = (active, credit, absent);
        }

        return result;
    }

    // appUserId -> current streaks over counting events (EventHistory.CountsTowardActive,
    // ended after they joined), oldest -> newest. Credit = trailing consecutive events
    // CREDITED; Absent = trailing consecutive events NOT credited. They're mutually
    // exclusive (one is 0). Computed regardless of EnableActivityTracking so the roster
    // can always show the numbers.
    public async Task<Dictionary<string, MemberStreaks>> ComputeStreaksByAppUserAsync(
        int linkshellId, CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, MemberStreaks>(StringComparer.OrdinalIgnoreCase);

        // The same state machine drives Active/Inactive AND the two columns, and it
        // already seeds from any manual override (the roster "Count" set via Modify),
        // so a manual value accumulates with later attendance. The column the member
        // is NOT currently accumulating always shows 0 (an inactive member shows a
        // climbing active-credit count + a 0 absent streak, and vice-versa).
        var machine = await ComputeActivityMachineAsync(linkshellId, cancellationToken);
        foreach (var kv in machine)
        {
            result[kv.Key] = new MemberStreaks(kv.Value.Credit, kv.Value.Absent);
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
            // NOTE: a manual "Count" override is NOT cleared here — it's a persistent
            // seed the state machine builds on (ComputeActivityMachineAsync), so a
            // manually-set credit/absence accumulates with subsequent attendance.
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
