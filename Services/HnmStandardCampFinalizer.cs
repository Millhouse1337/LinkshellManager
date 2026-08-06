using LinkshellManagerDiscordApp.Data;
using LinkshellManagerDiscordApp.Models;
using Microsoft.EntityFrameworkCore;

namespace LinkshellManagerDiscordApp.Services;

// The Standard-mode counterpart to WdCampFinalizer: the single home for "what did this Standard
// HNM camp pay?".
//
// Standard camps have no self-serve check-in — presence comes from the addon's attendance
// SNAPSHOTS. Each snapshot post upserts one EventAttendanceWindow for the window the board was
// showing and one AppUserEventWindow per scanned character, so "who was here in window N" is
// already recorded per player. This service reads that and pays, per member:
//
//     windowValue(seq) = DkpAmount[seq] ?? ( HnmStandardWindowBonus
//                                          + (seq == 1           ? HnmStandardOpenBonus  : 0)
//                                          + (seq == closeWindow ? HnmStandardCloseBonus : 0) )
//
//     dkp = round( Σ over every window the member was scanned in of windowValue(seq)
//                + (camp claimed AND scanned in close window ? HnmStandardClaimBonus : 0)
//                + (camp killed  AND scanned in close window ? HnmStandardKillBonus  : 0) , step )
//
// Each bonus reads Event.Hnm*BonusOverride first and falls back to the linkshell value when that
// is null — see HnmCampPricing.StandardBonuses, which owns that precedence for this service, for
// WdCampFinalizer, and for the rate the in-game addon is told.
//
// DkpAmount is the officer's price for ONE window, set from the Activity's Attendance Windows card
// or typed into the addon's "Dkp this window" box before a post. It REPLACES that window's default
// contribution rather than adding to it — see WindowValue.
//
// Claim and kill are gated on the CLOSE window, not merely on having been scanned somewhere. They
// reward the outcome — being there when the mob was claimed / killed — and the close window is
// where that happened. Ungated, someone scanned into one middle window collected the identical
// outcome bonus as the people who camped every window through the kill. A priced middle window
// must never become a claim/kill qualifier either: pricing a window says what IT pays, nothing
// about the outcome.
//
// The per-window base rate is HnmStandardWindowBonus, and it is still NOT Event.DkpPerHour. That
// column is 0 on HNM boards created through the Activity, but HnmAutoEventService copies a prior
// event's rate onto auto-created boards — so folding it in would make turning on a bonus silently
// start paying a per-window rate that appears nowhere in the HNM Settings card. The rate a camp
// pays per window is only ever the one an officer typed into "Regular window" (or the per-camp
// override), and it defaults to 0, which is the open/close-only payout every camp had before it.
//
// This no longer PAYS anything. End Camp hands the roster to HnmCampReviewHandoffService, which
// stages it as a pending row in the Event System page's attendance sections; an officer's Post is what credits DKP. So
// the job here is to answer "what did this camp earn?", not "move the balances".
//
// Read-only, but the CALL ORDER is still load-bearing: the pop deletes AppUserEvents and
// AppUserEventWindow cascades off AppUserEventId, so the presence data is gone the moment the
// teardown commits. Build the roster BEFORE the wipe.
public sealed class HnmStandardCampFinalizer
{
    private readonly ApplicationDbContext _db;
    private readonly ILogger<HnmStandardCampFinalizer> _logger;

    public HnmStandardCampFinalizer(
        ApplicationDbContext db, ILogger<HnmStandardCampFinalizer> logger)
    {
        _db = db;
        _logger = logger;
    }

    // The pure earn formula (no DB), extracted for unit testing. The caller passes the
    // ALREADY-gated bonuses — each is 0 when the player/camp didn't qualify for it.
    public static double ComputeDkp(
        double openBonus, double closeBonus, double claimBonus, double killBonus, double step)
        => DkpRounding.Round(openBonus + closeBonus + claimBonus + killBonus, step);

    // What ONE window pays each member scanned in it.
    //
    // An officer's explicit amount wins outright: it REPLACES the window's default contribution
    // rather than adding to it, so the number typed into "DKP this window" is exactly what that
    // window pays. That is the only reading under which the box round-trips — a control labelled
    // "DKP this window" that showed 5 and paid 5.5 would be lying about its own name.
    //
    // An explicit 0 is a REAL zero, not "unset". An officer who deliberately zeroes the Open must
    // be able to make it stick; treating 0 as absent is precisely the bug the addon's own
    // migrations.lua carried, where a saved 0 was silently rewritten to 1 on every reload.
    //
    // The open bonus is gated on window 1 SPECIFICALLY, not on "the earliest window posted". That
    // distinction is load-bearing: a camp can hold a Close with no Open at all (an officer who
    // only reached camp for the kill posts one window, and the addon files it as 2). Treating the
    // earliest posted window as the open would hand that camp's roster an open bonus for a roster
    // nobody ever observed at the open. The close is resolved instead of hardcoded — see
    // ResolveCloseWindow — so the same camp still closes out on window 2. Net effect for a
    // Close-only camp: close + claim + kill, and no open. Pinned by CloseOnlyCamp_* in
    // HnmStandardMemberGatingTests.
    //
    // `windowBonus` is the BASE every window pays — the regular in-between windows that used to be
    // worth nothing at all unless an officer priced them one at a time. Open and close ADD to it
    // rather than replacing it, which is the only reading under which they are "bonuses": a camp
    // paying 0.25 a window with a 1.0 open pays 1.25 for window 1, not 1.0. It defaults to 0, so a
    // linkshell that never sets it gets exactly the open/close-only payout it had before.
    //
    // Mirrored in the Activity by EventsTabComponent.windowValue — the same rule in two languages.
    // They must move together.
    public static double WindowValue(
        int sequence, int closeWindow, double? explicitAmount,
        double windowBonus, double openBonus, double closeBonus)
        => explicitAmount is { } amount
            ? Math.Max(0d, amount)
            : windowBonus
              + (sequence == 1 ? openBonus : 0d)
              + (closeWindow > 0 && sequence == closeWindow ? closeBonus : 0d);

    // One member's payout: every window they were scanned in, plus the outcome bonuses. The caller
    // has already run each scanned sequence through WindowValue, so this is the sum — which is why
    // a priced middle window pays the person who was only ever in it.
    //
    // Rounds the TOTAL, not each window. Three windows priced 0.1 pay 0.25 on a Quarter grid, not
    // 0.75: snapping per window would let the grid multiply the error by the window count.
    //
    // Pure (no DB), like ComputeDkp above, so the gating is testable without staging a camp.
    public static double ComputeMemberDkp(
        IEnumerable<double> earnedWindowValues, bool atClose,
        double claimBonus, double killBonus, double step)
        => DkpRounding.Round(
            earnedWindowValues.Sum() + (atClose ? claimBonus : 0d) + (atClose ? killBonus : 0d),
            step);

    // THE gating rule for a camp nobody has priced by hand AND that pays no regular-window rate:
    // open needs the open window, and close / claim / kill all need the close window.
    //
    // Deliberately has no windowBonus parameter. This overload knows only two booleans, and a rate
    // that pays PER WINDOW needs the count of them — a member scanned in six windows earns six
    // times it. Camps with a regular-window rate go through the IEnumerable overload above (which
    // BuildRosterAsync always uses); this one stays the spec for the rate-free default.
    //
    // Expressed THROUGH the per-window sum above rather than beside it, so the override-free and
    // the priced forms can never disagree. HnmStandardMemberGatingTests is the spec for both.
    public static double ComputeMemberDkp(
        bool atOpen, bool atClose,
        double openBonus, double closeBonus, double claimBonus, double killBonus, double step)
    {
        var earned = new List<double>(2);
        if (atOpen) earned.Add(openBonus);
        if (atClose) earned.Add(closeBonus);
        return ComputeMemberDkp(earned, atClose, claimBonus, killBonus, step);
    }

    // Which window counts as the camp's "close". The pop window when a snapshot was actually
    // posted for it; otherwise the latest window that HAS a snapshot (an officer who stopped
    // scanning three windows before the pop still closes out the people in that last scan).
    // 0 when the camp has no snapshots at all — then nobody is at the open or the close.
    public static int ResolveCloseWindow(IReadOnlyCollection<int> postedWindows, int popWindow)
    {
        if (postedWindows.Count == 0) return 0;
        return postedWindows.Contains(popWindow) ? popWindow : postedWindows.Max();
    }

    // Who this camp owes and how much, read off the addon's window scans. Read-only: stages
    // nothing, saves nothing. MUST be called before the caller tears the roster down.
    //
    // Deliberately does NOT bail when no bonuses are configured. The roster is the attendance
    // record those attendance sections exist to show, and amounts stay editable during review —
    // so a camp that currently pays 0 still produces a row an officer can price. (The old
    // StageCreditAsync returned early here, because with no review step a 0-DKP camp had nowhere
    // to be seen anyway.)
    public async Task<List<HnmCampMember>> BuildRosterAsync(
        Event ev, Linkshell linkshell, int popWindow, bool claimed, bool killed,
        CancellationToken cancellationToken)
    {
        // Per-camp overrides win over the linkshell defaults; null falls back, so a camp
        // whose creator never opened "Change DKP" pays the linkshell rate as before. Resolved by
        // HnmCampPricing so the rate the addon is quoted before a post is the one paid after it.
        var (windowBonus, openBonus, closeBonus, claimBonus, killBonus) =
            HnmCampPricing.StandardBonuses(ev, linkshell, claimed, killed);

        // Every (account, window) the addon scanned for this camp.
        //
        // Reads the DENORMALIZED AppUserId on the snapshot row, not a join back through
        // AppUserEvent. On the 25-window wyrm camps the roster is cleared every window, which
        // deletes participations — the snapshots survive that (SetNull) but their AppUserEventId
        // is gone, so walking the participation would have dropped every window but the last.
        var scans = await _db.AppUserEventWindows
            .Where(w => w.EventAttendanceWindow!.EventId == ev.Id && w.AppUserId != null)
            .Select(w => new
            {
                AppUserId = w.AppUserId!,
                w.CharacterName,
                w.AppUserEventId,
                w.EventAttendanceWindow!.SequenceNumber,
                // The officer's price for this window, null when they never set one. Rides on the
                // join already being walked — no extra round trip.
                w.EventAttendanceWindow!.DkpAmount
            })
            .ToListAsync(cancellationToken);
        if (scans.Count == 0) return new List<HnmCampMember>();

        var closeWindow = ResolveCloseWindow(scans.Select(s => s.SequenceNumber).Distinct().ToList(), popWindow);

        // Price every posted window ONCE, up front. The amount is a property of the window, so
        // resolving it per member would be the same lookup N times over.
        var valueBySequence = scans
            .GroupBy(s => s.SequenceNumber)
            .ToDictionary(
                g => g.Key,
                g => WindowValue(
                    g.Key, closeWindow, g.First().DkpAmount, windowBonus, openBonus, closeBonus));

        var participations = await _db.AppUserEvents
            .Where(p => p.EventId == ev.Id)
            .ToListAsync(cancellationToken);
        var participationById = participations.ToDictionary(p => p.Id);

        // Fold the scans up to the ACCOUNT, not the participation row. An account can hold two
        // participations for one event (a website join plus an addon post under an alt) and only
        // one of them carries the window rows — crediting "the first participation" would pay 0
        // to a player who was scanned every window under their alt.
        var windowsByAppUserId = new Dictionary<string, HashSet<int>>(StringComparer.OrdinalIgnoreCase);
        var participationByAppUserId = new Dictionary<string, AppUserEvent>(StringComparer.OrdinalIgnoreCase);
        var characterNameByAppUserId = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        var scanCountByAppUserId = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var scan in scans)
        {
            var appUserId = scan.AppUserId;
            if (!windowsByAppUserId.TryGetValue(appUserId, out var windows))
            {
                windows = new HashSet<int>();
                windowsByAppUserId[appUserId] = windows;
            }
            windows.Add(scan.SequenceNumber);

            // Last non-empty name wins, so a member whose participation was cleared away still has
            // a character name on their history row.
            if (!string.IsNullOrWhiteSpace(scan.CharacterName))
            {
                characterNameByAppUserId[appUserId] = scan.CharacterName;
            }

            // Represent the account with whichever participation the addon scanned most — that's
            // the row whose character/job actually reflects who showed up. Null once a roster
            // clear has removed it; the credit loop below falls back to the denormalized name.
            if (scan.AppUserEventId is not { } participationId
                || !participationById.TryGetValue(participationId, out var participation))
            {
                continue;
            }

            var count = scanCountByAppUserId.GetValueOrDefault(appUserId);
            if (!participationByAppUserId.ContainsKey(appUserId) || count == 0)
            {
                participationByAppUserId[appUserId] = participation;
            }
            scanCountByAppUserId[appUserId] = count + 1;
        }
        if (windowsByAppUserId.Count == 0) return new List<HnmCampMember>();

        var memberships = await _db.AppUserLinkshells
            .Where(m => m.LinkshellId == ev.LinkshellId && m.AppUserId != null)
            .ToListAsync(cancellationToken);
        var membershipByAppUserId = memberships
            .GroupBy(m => m.AppUserId!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        var step = DkpRounding.StepFor(linkshell.DkpRoundingIncrement);

        var members = new List<HnmCampMember>();
        foreach (var (appUserId, windows) in windowsByAppUserId)
        {
            // Participation may be GONE — a wyrm roster clear deletes it while the snapshots
            // survive. The scan is the evidence of presence either way, so fall back to the
            // denormalized character name and the membership rather than skipping them.
            participationByAppUserId.TryGetValue(appUserId, out var participation);
            membershipByAppUserId.TryGetValue(appUserId, out var membership);

            var characterName = participation?.CharacterName
                ?? characterNameByAppUserId.GetValueOrDefault(appUserId)
                ?? membership?.CharacterName;
            if (string.IsNullOrWhiteSpace(characterName)) continue;  // nothing to review

            // Only the OUTCOME bonuses are gated here now — the per-window credit is whatever
            // each window they were scanned in is priced at (see WindowValue for why the open is
            // gated on window 1 specifically rather than on the earliest window posted).
            var atClose = closeWindow > 0 && windows.Contains(closeWindow);

            // A member who scanned but qualified for nothing is still LISTED — they were at the
            // camp, and that's what the review page is for. They just carry 0, which an officer
            // can raise. (The old pay-direct path dropped them to avoid ledger noise; a pending
            // row isn't ledger noise.)
            members.Add(new HnmCampMember(
                AppUserId: appUserId,
                CharacterName: characterName.Trim(),
                JobName: participation?.JobName,
                SubJobName: participation?.SubJobName,
                Dkp: ComputeMemberDkp(
                    windows.Select(seq => valueBySequence.GetValueOrDefault(seq)),
                    atClose, claimBonus, killBonus, step)));
        }

        _logger.LogInformation(
            "Standard HNM camp roster built: event {EventId} has {Count} member(s) "
            + "(close window {Close}, claimed={Claimed}, killed={Killed}).",
            ev.Id, members.Count, closeWindow, claimed, killed);
        return members;
    }
}
