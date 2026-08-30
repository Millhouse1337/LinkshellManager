using LinkshellManagerDiscordApp.Data;
using LinkshellManagerDiscordApp.Models;
using Microsoft.EntityFrameworkCore;

namespace LinkshellManagerDiscordApp.Services;

// The single home for the Manual Check In earn formula:
//     dkp = round( WdDkpPerWindow * (lastWindow - arrival + 1)
//                  + (arrival == 1        ? WdOpenBonus  : 0)
//                  + (creditLast == last  ? WdCloseBonus : 0)
//                  + (claimed AND tagged the mob on the Claim Shield ? WdClaimBonus : 0)
//                  + (killed  ? WdKillBonus  : 0) )
//
// Open and close are the Wd counterparts of the Standard bonuses of the same name, and they gate
// on the member's own CHECK-IN RANGE rather than on a snapshot: open means they were checked in
// from window 1, close means they were still checked in at the camp's last credited window (no
// early Check Out). Both default to 0, so a linkshell that never sets them pays what it always did.
//
// Each of the five reads the camp's own Event.Hnm*Override first and falls back to the linkshell
// value when that is null, so one camp can be priced differently without moving the default. That
// precedence lives in HnmCampPricing.WdAmounts, shared with the Standard finalizer.
//
// Deliberately does NOT honour EventAttendanceWindow.DkpAmount, the per-window price an officer
// can set on a Standard camp. Credit here runs over the CHECK-IN RANGE
// (WdArrivalWindow .. min(WdDepartureWindow, popWindow)), not over posted snapshots — a member is
// paid for windows that have no EventAttendanceWindow row at all — so `rate × WindowsCredited`
// cannot be decomposed per posted window without changing what an unposted window pays. The
// Activity says as much on the card itself: "Informational only — DKP comes from Check In /
// Check Out."
//
// The coherent extension, if it is ever wanted, is
//     Σ over seq in arrival..last of (DkpAmount[seq] ?? rate)
// where a missing row falls back to the flat rate. That needs its own decision, because it
// re-prices every existing Manual Check In camp with sparse snapshots. Until then three things
// must agree: this service ignores the column, HnmCampPricing.WindowValueFor returns null for Wd,
// and the Activity hides the per-window editor on Wd camps.
//
// This no longer PAYS anything. End Camp hands the roster to HnmCampReviewHandoffService, which
// stages it as a pending row in the Event System page's attendance sections; an officer's Post is what credits DKP.
// So the job here is to answer "what did this camp earn?", not "move the balances" — the old
// FinalizeAsync (atomic claim + ledger write + board recycle) is gone, along with the
// "Awaiting Processing" grace that used to gate it.
public sealed class WdCampFinalizer
{
    private readonly ApplicationDbContext _db;
    private readonly ILogger<WdCampFinalizer> _logger;

    public WdCampFinalizer(ApplicationDbContext db, ILogger<WdCampFinalizer> logger)
    {
        _db = db;
        _logger = logger;
    }

    // Windows credited = lastWindow - arrivalWindow + 1 (both inclusive). A member who arrived
    // AFTER the last credited window (e.g. checked in after the monster popped) gets 0.
    public static int WindowsCredited(int arrivalWindow, int lastWindow)
    {
        var last = Math.Max(1, lastWindow);
        var arrival = Math.Max(1, arrivalWindow);
        return arrival > last ? 0 : last - arrival + 1;
    }

    // The pure Manual Check In earn formula (no DB), extracted for unit testing:
    //   dkp = round( rate * windowsCredited + open + close + claim + kill , step )
    //
    // The caller passes the ALREADY-gated outcome bonuses (0 when the camp wasn't claimed/killed).
    // Open and close are gated HERE, off the member's own range, because that is what decides them:
    //   open  — arrived in window 1 (there from the start)
    //   close — still checked in at `campLastWindow` (didn't leave early)
    // A member who earns no windows at all earns no bonuses either; they were never at the camp in
    // any window the payout covers, and paying them the open for a range that credits nothing would
    // hand DKP to someone who checked in after the mob was already down.
    public static double ComputeDkp(
        double rate, int arrivalWindow, int lastWindow, int campLastWindow,
        double openBonus, double closeBonus, double claimBonus, double killBonus, double step)
    {
        var windows = WindowsCredited(arrivalWindow, lastWindow);
        if (windows <= 0) return DkpRounding.Round(0d, step);
        return DkpRounding.Round(
            windows * rate
            + (arrivalWindow <= 1 ? openBonus : 0d)
            + (lastWindow >= campLastWindow ? closeBonus : 0d)
            + claimBonus + killBonus,
            step);
    }

    // Who this camp owes and how much, read off the self-serve check-ins
    // (AppUserEvent.WdArrivalWindow / WdDepartureWindow). Read-only: stages nothing, saves
    // nothing. MUST be called before the caller tears the roster down.
    public async Task<List<HnmCampMember>> BuildRosterAsync(
        Event ev, Linkshell linkshell, int popWindow, bool claimed, bool killed,
        CancellationToken cancellationToken)
    {
        // Nobody is paid past the officer-set pop window even if the counter auto-advanced while
        // the group was fighting.
        var effectiveCount = DiscordEventMessageBuilder.EffectiveWindowCount(ev);
        var lastWindow = Math.Clamp(popWindow, 1, effectiveCount);
        // Per-camp overrides win over the linkshell defaults; null falls back, so a camp
        // whose creator never opened "Change DKP" pays the linkshell rate as before.
        var (rate, openBonus, closeBonus, claimBonus, killBonus) =
            HnmCampPricing.WdAmounts(ev, linkshell, claimed, killed);
        var step = DkpRounding.StepFor(linkshell.DkpRoundingIncrement);

        // The claim bonus is no longer paid to everyone who checked in. It goes to the people whose
        // names are on this camp's Claim Shield — the ones the addon watched land an action on the
        // mob (claim_shield.lua) — for the same reason it does on a Standard camp: checking in is
        // presence, and the claim bonus pays for the tag. Accounts only; an unmatched capture name
        // has no balance to credit.
        //
        // Whether the bonus applies at all is still the officer's End Camp call — `claimBonus` is
        // already 0 here when the camp wasn't claimed. The Claim Shield decides WHO, not IF.
        var taggedAppUserIds = (await _db.ClaimShieldCaptureMembers
                .Where(m => m.Capture!.EventId == ev.Id && m.AppUserId != null)
                .Select(m => m.AppUserId!)
                .Distinct()
                .ToListAsync(cancellationToken))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var participations = await _db.AppUserEvents
            .Where(p => p.EventId == ev.Id)
            .ToListAsync(cancellationToken);

        // Fallback names for account-linked rows whose participation carries none.
        var membershipNameByAppUserId = await _db.AppUserLinkshells
            .Where(m => m.LinkshellId == ev.LinkshellId && m.AppUserId != null)
            .Select(m => new { m.AppUserId, m.CharacterName })
            .ToListAsync(cancellationToken);
        var nameByAppUserId = membershipNameByAppUserId
            .GroupBy(m => m.AppUserId!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().CharacterName, StringComparer.OrdinalIgnoreCase);

        var members = new List<HnmCampMember>();
        var seenAppUserIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var participation in participations)
        {
            if (participation.WdArrivalWindow is not { } arrival) continue;   // never checked in
            // One row per account. Account-less "HNM Outside Sign Up" rows have no id to dedupe
            // on; they ride along on their character name and resolve (or don't) at post time.
            if (!string.IsNullOrWhiteSpace(participation.AppUserId)
                && !seenAppUserIds.Add(participation.AppUserId))
            {
                continue;
            }

            // Credit runs arrival..min(departure, popWindow), inclusive. A member who checked out
            // early stops at their departure window; one who stayed runs to the pop/last window.
            var creditLast = Math.Min(participation.WdDepartureWindow ?? lastWindow, lastWindow);
            if (WindowsCredited(arrival, creditLast) <= 0) continue;   // arrived after they left

            var characterName = participation.CharacterName;
            if (string.IsNullOrWhiteSpace(characterName) && participation.AppUserId is { } id)
            {
                characterName = nameByAppUserId.GetValueOrDefault(id);
            }
            if (string.IsNullOrWhiteSpace(characterName)) continue;  // nothing an officer could review

            members.Add(new HnmCampMember(
                AppUserId: string.IsNullOrWhiteSpace(participation.AppUserId) ? null : participation.AppUserId,
                CharacterName: characterName.Trim(),
                JobName: participation.JobName,
                SubJobName: participation.SubJobName,
                Dkp: ComputeDkp(
                    rate, arrival, creditLast, lastWindow,
                    openBonus, closeBonus,
                    participation.AppUserId is { } tagId && taggedAppUserIds.Contains(tagId)
                        ? claimBonus
                        : 0d,
                    killBonus, step)));
        }

        _logger.LogInformation(
            "Manual Check In camp roster built: event {EventId} has {Count} member(s) "
            + "(last window {Last}, claim={Claim}, kill={Kill}).",
            ev.Id, members.Count, lastWindow, claimed, killed);
        return members;
    }
}
