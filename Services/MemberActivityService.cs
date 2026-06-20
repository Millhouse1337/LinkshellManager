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
    //   * Each counting event updates two always-live trailing streaks: an ATTENDANCE
    //     grows `Credit` (the "Active streak") and zeroes `Absent`; a MISS grows `Absent`
    //     and zeroes `Credit`. Exactly one is non-zero — it reads as the current reason.
    //   * Status follows by hysteresis: `Absent` reaching InactiveAfterAbsences flips an
    //     Active member to Inactive; `Credit` reaching ActiveAfterAttendances flips an
    //     Inactive member back to Active. Counters are NEVER reset on a flip — they keep
    //     climbing so the roster always shows the real streak.
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

        // The earliest counting event each member was credited for. Used as a fallback
        // "joined" cutoff when DateJoined is missing (e.g. members imported from a DKP
        // sheet never get one set): without a lower bound the machine would count the
        // linkshell's ENTIRE event history as absences and mark the member Inactive off
        // events from before they ever participated — the "randomly Inactive" bug.
        var firstCreditedEndByUser = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        foreach (var ev in countingEvents) // oldest -> newest, so the first hit is earliest
        {
            if (!ev.EndTime.HasValue || !creditedByEvent.TryGetValue(ev.Id, out var credited))
            {
                continue;
            }
            foreach (var uid in credited)
            {
                if (!firstCreditedEndByUser.ContainsKey(uid))
                {
                    firstCreditedEndByUser[uid] = ev.EndTime.Value;
                }
            }
        }

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
                active = v >= activeAfter; // at/above the attendance bar => Active
                credit = v;                // show the streak; absent stays 0 (mutually exclusive)
                cutoff = member.ManualStreakSetAt ?? member.DateJoined;
            }
            else if (member.ManualAbsentStreak.HasValue)
            {
                var v = Math.Max(0, member.ManualAbsentStreak.Value);
                active = v < inactiveAfter; // at/above the absence bar => Inactive
                absent = v;                 // show the streak; credit stays 0
                cutoff = member.ManualStreakSetAt ?? member.DateJoined;
            }

            // Still no lower bound — missing join date, or a legacy manual override saved
            // before seed timestamps existed. Start at the member's first credited
            // attendance so pre-participation events never count; if they were never
            // credited, exclude everything so they stay Active rather than mass-absent.
            if (cutoff is null)
            {
                cutoff = firstCreditedEndByUser.TryGetValue(member.AppUserId, out var firstSeen)
                    ? firstSeen
                    : DateTime.MaxValue;
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

                // Both streaks are always live, pure trailing counts: an attendance grows
                // the active streak and zeroes the absent streak; a miss does the reverse —
                // so exactly one is non-zero and it always reads as the reason for the
                // status. Counters are NEVER reset on a flip; they just keep climbing.
                if (attended)
                {
                    credit++;
                    absent = 0;
                    // Two attendances in a row (ActiveAfterAttendances) reactivate an
                    // Inactive member.
                    if (!active && credit >= activeAfter) { active = true; }
                }
                else
                {
                    absent++;
                    credit = 0;
                    // Misses in a row reaching InactiveAfterAbsences drop an Active
                    // member to Inactive.
                    if (active && absent >= inactiveAfter) { active = false; }
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
        // so a manual value accumulates with later attendance. Exactly one column is
        // non-zero (the trailing run of the most recent identical outcomes): an Inactive
        // member who keeps missing shows a climbing Absent streak — the reason they're
        // Inactive — while one attending back shows a climbing Active credit.
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
