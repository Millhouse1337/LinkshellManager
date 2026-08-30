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
//     windowValue(seq) = DkpAmount[seq] ?? ( isKillWindow ? 0 :
//                                            seq == 1     ? HnmStandardOpenBonus  :
//                                            seq == close ? HnmStandardCloseBonus :
//                                                           HnmStandardWindowBonus )
//
//     dkp = round( Σ over every window the member was scanned in of windowValue(seq)
//                + (tagged the mob on the Claim Shield ? HnmStandardClaimBonus : 0)
//                + (scanned in the Post Kill window    ? HnmStandardKillBonus  : 0) , step )
//
// Each window pays ONE amount — the open, the close, or the regular rate — rather than the rate
// with bonuses added on top. That is a deliberate reversal: the bonuses used to ADD to the
// per-window rate, so window 1 paid rate + open. Combined with a close that was GUESSED as "the
// newest window posted", the result was every window on a camp carrying the close bonus and window
// 1 carrying open + close + rate. The close is now an explicit officer mark
// (EventAttendanceWindow.IsClosingWindow) and each window names its own price.
//
// A window that is BOTH the open and the close pays the OPEN, alone. One window, one amount, with
// no exception — not even for the camp that genuinely opened and closed in a single roster read.
// That camp is the OFFICER'S to settle: they add the close by hand from the review page, where the
// amounts are editable, because only they can say whether one scan really was both ends of a camp.
//
// Paying both automatically is what made posting the opening window read as 2 DKP on a linkshell
// configured for 1 open + 1 close. The close FALLS BACK to the latest window posted whenever
// nobody has ticked one (ResolveCloseWindow), so on a fresh camp the open window is always also
// the close — every camp hit the exception on its first post, and the "rare" case was the norm.
//
// The open is tested FIRST for the same reason: sequence 1 is the open by definition, it is what
// the officer just posted, and it must not read as anything else while the close is still a guess.
//
// Each bonus reads Event.Hnm*BonusOverride first and falls back to the linkshell value when that
// is null — see HnmCampPricing.StandardBonuses, which owns that precedence for this service, for
// WdCampFinalizer, and for the rate the in-game addon is told.
//
// DkpAmount is the officer's price for ONE window, set from the Activity's Attendance Windows card
// or typed into the addon's "Dkp this window" box before a post. It REPLACES that window's default
// contribution rather than adding to it — see WindowValue.
//
// Claim and kill are each gated on the EVIDENCE that the member was part of that outcome, and the
// two pieces of evidence are different things:
//
//   claim — their name is on a ClaimShieldCapture for this event. The addon replays chat and only
//           counts someone who actually landed an action on the mob (claim_shield.lua), so this is
//           the list of people who tagged it. Being at the camp is not tagging it.
//   kill  — they were scanned in the Post Kill window. That roster is read when the mob dies, which
//           is the only read that can include the people who turned up only for the fight and
//           exclude the ones who had already left.
//
// Both used to gate on "scanned in the close window", which stood in for both outcomes because
// there was nothing better to gate on. There is now: a tagger who logged out before the pop earns
// their claim, and a late arrival who helped kill it earns their kill.
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
    // A window pays ONE of the three amounts, with NO exception: a camp with 0.25 window / 0.5 open
    // / 1.5 close pays 0.5 for window 1, 0.25 for the middle windows and 1.5 for the close.
    // `windowBonus` is what the in-between windows pay and defaults to 0, which is the
    // open/close-only payout most camps run on.
    //
    // A window that is both ends at once — a single-post NM claimed and dead in one roster read, a
    // camp that popped in its opener — is NOT an exception to that. It pays the open, and the close
    // is the officer's to add by hand from the review page. This used to pay open + close, on the
    // reasoning that the camp really did earn both; what that missed is that the close falls back
    // to "the latest window posted" whenever nobody has ticked one, so EVERY camp's opening post
    // was both ends of the camp for as long as it was the only window. Posting the open on a
    // 1 open / 1 close linkshell read as 2 DKP, which is not a payout an officer asked for.
    //
    // The open is tested before the close so sequence 1 always prices as the open — it is what the
    // officer posted, and the only one of the two that is a fact rather than a mark or a guess.
    //
    // A kill window is worth 0 as a window. It is not a roster read of the camp, it is the record
    // of who was standing there when the mob died, and the kill bonus is what pays for that
    // (ComputeMemberDkp). Pricing it as a regular window on top would pay the late arrivals twice
    // for one appearance.
    //
    // Mirrored in the Activity by EventsTabComponent.windowValue — the same rule in two languages.
    // They must move together.
    public static double WindowValue(
        int sequence, int closeWindow, double? explicitAmount,
        double windowBonus, double openBonus, double closeBonus,
        bool isKillWindow = false)
    {
        if (explicitAmount is { } amount) return Math.Max(0d, amount);
        if (isKillWindow) return 0d;

        var isOpen = sequence == 1;
        var isClose = closeWindow > 0 && sequence == closeWindow;
        if (isOpen) return openBonus;
        if (isClose) return closeBonus;
        return windowBonus;
    }

    // One member's payout: every window they were scanned in, plus the outcome bonuses. The caller
    // has already run each scanned sequence through WindowValue, so this is the sum — which is why
    // a priced middle window pays the person who was only ever in it.
    //
    // Rounds the TOTAL, not each window. Three windows priced 0.1 pay 0.25 on a Quarter grid, not
    // 0.75: snapping per window would let the grid multiply the error by the window count.
    //
    // `tagged` is "their name is on this camp's Claim Shield"; `inKillWindow` is "they were scanned
    // in the Post Kill roster". Two separate gates because they are two separate claims about the
    // member, and one person can easily be one and not the other.
    //
    // Pure (no DB), like ComputeDkp above, so the gating is testable without staging a camp.
    public static double ComputeMemberDkp(
        IEnumerable<double> earnedWindowValues, bool tagged, bool inKillWindow,
        double claimBonus, double killBonus, double step)
        => DkpRounding.Round(
            earnedWindowValues.Sum() + (tagged ? claimBonus : 0d) + (inKillWindow ? killBonus : 0d),
            step);

    // THE gating rule for a camp nobody has priced by hand AND that pays no regular-window rate:
    // the open needs window 1, the close needs the marked closing window, the claim needs a Claim
    // Shield tag, and the kill needs the Post Kill roster.
    //
    // Deliberately has no windowBonus parameter. This overload knows only booleans, and a rate that
    // pays PER WINDOW needs the count of them — a member scanned in six windows earns six times it.
    // Camps with a regular-window rate go through the IEnumerable overload above (which
    // BuildRosterAsync always uses); this one stays the spec for the rate-free default.
    //
    // Expressed THROUGH WindowValue and the per-window sum above rather than beside them, so the
    // override-free and the priced forms can never disagree — the two bonuses are not added here,
    // they are PRICED, by the same function that prices a real camp.
    //
    // `atOpen` and `atClose` are about DIFFERENT windows — scanned in window 1, and scanned in the
    // marked closing window — so a member who was in both earns both. That is two windows and two
    // payments, not one window paying twice, and the sequences below say so out loud.
    //
    // A camp whose open IS its close cannot be expressed here, and deliberately so: it has ONE
    // window, that window prices as the open (see WindowValue), and the close is the officer's to
    // add by hand. Pass atClose: false for it — a caller that passes both would be describing a
    // member scanned in two windows on a camp that only ever had one.
    // ComputeMemberDkp_NoExplicitAmounts_MatchesTheBooleanOverload pins the agreement.
    public static double ComputeMemberDkp(
        bool atOpen, bool atClose, bool tagged, bool inKillWindow,
        double openBonus, double closeBonus, double claimBonus, double killBonus, double step)
    {
        // Any close AFTER the open would do; 2 is the earliest camp shape that has both ends.
        const int closeSequence = 2;
        var earned = new List<double>(2);
        if (atOpen) earned.Add(WindowValue(1, closeSequence, null, 0d, openBonus, closeBonus));
        if (atClose)
        {
            earned.Add(WindowValue(closeSequence, closeSequence, null, 0d, openBonus, closeBonus));
        }

        return ComputeMemberDkp(earned, tagged, inKillWindow, claimBonus, killBonus, step);
    }

    // Which window counts as the camp's "close".
    //
    // The officer's explicit mark wins outright and needs no corroboration — a checked "closing
    // window" box is a statement about the camp, not a guess to be second-guessed.
    //
    // Everything else is the OLD derivation, kept only as a fallback for camps where nobody ticked
    // the box: the pop window when a snapshot exists for it, otherwise the latest window that has
    // one. That derivation is why this parameter list exists at all, and it is exactly what went
    // wrong — since the addon only ever posts windows that have already opened, "the latest posted
    // window" is whichever one was posted most recently, so every window in turn looked like the
    // close while it was newest. Harmless at payout (only the final answer is paid), ruinous as a
    // QUOTE, which is what HnmCampPricing.DefaultWindowValue was doing with it. That quote no
    // longer asks this question; see DefaultWindowValue.
    //
    // `postedWindows` must EXCLUDE kill windows — the Post Kill roster is filed after the close and
    // would otherwise take the close bonus off the window the officer marked. Callers filter.
    //
    // This can still answer 1 — one window posted, nothing ticked — and that answer is now inert at
    // payout: WindowValue prices sequence 1 as the open whether or not it is also the close, so the
    // fallback landing on the opening window costs nobody anything. It used to pay open + close.
    //
    // 0 when the camp has no snapshots at all — then nobody is at the open or the close.
    public static int ResolveCloseWindow(
        IReadOnlyCollection<int> postedWindows, int popWindow, int? markedCloseWindow = null)
    {
        if (markedCloseWindow is > 0) return markedCloseWindow.Value;
        if (postedWindows.Count == 0) return 0;
        return postedWindows.Contains(popWindow) ? popWindow : postedWindows.Max();
    }

    // The same question asked of the ROWS, which is how every caller outside this file has it.
    //
    // Exists so the two filters that make the answer correct — find the ticked box, drop the kill
    // windows — are applied in one place instead of at each call site. They were open-coded nowhere
    // before this because neither concept existed; adding two flags and trusting three callers to
    // remember both is how the addon's window number and the board's drifted apart the last time.
    public static int ResolveCloseWindow(IEnumerable<EventAttendanceWindow> windows, int popWindow)
    {
        var rows = windows as IReadOnlyCollection<EventAttendanceWindow> ?? windows.ToList();
        var marked = rows.FirstOrDefault(w => w.IsClosingWindow && !w.IsKillWindow);
        return ResolveCloseWindow(
            rows.Where(w => !w.IsKillWindow).Select(w => w.SequenceNumber).Distinct().ToList(),
            popWindow,
            marked?.SequenceNumber);
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
                // Set only when the scan caught them on an alt. The payout review is a roster
                // document — it lists who is owed DKP, not which character was standing there —
                // so this is what the fallback name below prefers.
                w.MainCharacterName,
                w.AppUserEventId,
                w.EventAttendanceWindow!.SequenceNumber,
                // The officer's price for this window, null when they never set one. Rides on the
                // join already being walked — no extra round trip.
                w.EventAttendanceWindow!.DkpAmount,
                w.EventAttendanceWindow!.IsClosingWindow,
                w.EventAttendanceWindow!.IsKillWindow
            })
            .ToListAsync(cancellationToken);

        // NO early return on an empty scan list any more. A camp can owe DKP with not one snapshot
        // posted: the Claim Shield fires off chat, so the taggers are recorded whether or not an
        // officer ever read the roster. Bailing here paid them nothing, and the officer who forgot
        // to post a window is exactly the officer who most needs the record.

        // The officer's ticked box, if there is one. Kill windows are excluded from the fallback
        // derivation below for the reason ResolveCloseWindow gives — the Post Kill roster is filed
        // after the close and must not take the close bonus off the window that earned it.
        var markedCloseWindow = scans
            .Where(s => s.IsClosingWindow && !s.IsKillWindow)
            .Select(s => (int?)s.SequenceNumber)
            .FirstOrDefault();
        var closeWindow = ResolveCloseWindow(
            scans.Where(s => !s.IsKillWindow).Select(s => s.SequenceNumber).Distinct().ToList(),
            popWindow,
            markedCloseWindow);

        // Price every posted window ONCE, up front. The amount is a property of the window, so
        // resolving it per member would be the same lookup N times over.
        var valueBySequence = scans
            .GroupBy(s => s.SequenceNumber)
            .ToDictionary(
                g => g.Key,
                g => WindowValue(
                    g.Key, closeWindow, g.First().DkpAmount,
                    windowBonus, openBonus, closeBonus, g.First().IsKillWindow));

        // Which sequences are the Post Kill rosters. A camp can hold more than one if the officer
        // posted a kill, corrected it and posted again; being in ANY of them is the kill.
        var killWindows = scans
            .Where(s => s.IsKillWindow)
            .Select(s => s.SequenceNumber)
            .ToHashSet();

        // Who tagged the mob, straight off this camp's Claim Shield captures. Accounts only —
        // an unmatched name resolved to no membership, so there is no balance to credit and no
        // way to tell whether it was even a linkshell member. Those rows stay visible in the
        // Claim Shield panel, which is where an officer fixes them (by fixing the roster name).
        //
        // Every capture on the event contributes names, won or lost — a tag is a tag whether or not
        // the lottery went our way. WHETHER the claim bonus is paid at all is still the officer's
        // call at End Camp: `claimBonus` arrives here already zeroed when the camp wasn't claimed
        // (HnmCampPricing.StandardBonuses). The Claim Shield decides WHO, not IF.
        var taggedAppUserIds = (await _db.ClaimShieldCaptureMembers
                .Where(m => m.Capture!.EventId == ev.Id && m.AppUserId != null)
                .Select(m => m.AppUserId!)
                .Distinct()
                .ToListAsync(cancellationToken))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

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
            // a character name on their history row. Their MAIN when the scan was of an alt: the
            // row that comes out of this is a DKP credit, and crediting "Athmilk" when the roster
            // (and every other DKP row they own) says "Edicius" reads as a second person.
            var scanRosterName = scan.MainCharacterName ?? scan.CharacterName;
            if (!string.IsNullOrWhiteSpace(scanRosterName))
            {
                characterNameByAppUserId[appUserId] = scanRosterName;
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

        // Deliberately falls through on an empty map, for the same reason the scan guard above
        // does: the Claim Shield taggers are appended after this loop and owe nothing to it.

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
            //
            // Each outcome answers to its OWN evidence: the claim to the Claim Shield tag list, the
            // kill to the Post Kill roster. Neither reads the close window any more.
            var tagged = taggedAppUserIds.Contains(appUserId);
            var inKillWindow = windows.Overlaps(killWindows);

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
                    tagged, inKillWindow, claimBonus, killBonus, step)));
        }

        // Someone can tag the mob and never appear in a single scan — they died on the tag, or
        // logged out, or the officer's first snapshot came after they left. The claim is evidence
        // of presence in its own right (the addon only counts an action that LANDED on the mob), so
        // they get a row for it rather than being dropped for having no window.
        //
        // Runs after the scan loop and skips anyone already listed, so a tagger who was also
        // scanned keeps their window credit and is not duplicated.
        if (claimBonus > 0)
        {
            var listedAppUserIds = members
                .Where(m => m.AppUserId is not null)
                .Select(m => m.AppUserId!)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            foreach (var appUserId in taggedAppUserIds)
            {
                if (listedAppUserIds.Contains(appUserId)) continue;
                if (!membershipByAppUserId.TryGetValue(appUserId, out var membership)) continue;

                var taggerName = membership.CharacterName;
                if (string.IsNullOrWhiteSpace(taggerName)) continue;

                members.Add(new HnmCampMember(
                    AppUserId: appUserId,
                    CharacterName: taggerName.Trim(),
                    JobName: null,
                    SubJobName: null,
                    Dkp: DkpRounding.Round(claimBonus, step)));
            }
        }

        _logger.LogInformation(
            "Standard HNM camp roster built: event {EventId} has {Count} member(s) "
            + "(close window {Close}, {Tagged} tagged on the Claim Shield, "
            + "kill window(s) {KillWindows}, claimed={Claimed}, killed={Killed}).",
            ev.Id, members.Count, closeWindow, taggedAppUserIds.Count,
            killWindows.Count, claimed, killed);
        return members;
    }
}
