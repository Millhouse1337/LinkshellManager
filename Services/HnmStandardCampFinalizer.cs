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
//     baseValue(seq)  = DkpAmount[seq] ?? ( isKillWindow ? 0 :
//                                           seq == 1     ? HnmStandardOpenBonus :
//                                                          HnmStandardWindowBonus )
//     closeValue(seq) = seq == close ? HnmStandardCloseBonus : 0
//     windowValue(seq) = baseValue(seq) + closeValue(seq)
//
//     dkp = round( Σ over every window the member was scanned in of windowValue(seq)
//                + (tagged the mob on the Claim Shield ? HnmStandardClaimBonus : 0)
//                + (scanned in the Post Kill window    ? HnmStandardKillBonus  : 0) , step )
//
// The close bonus ADDS to the window it lands on (since 2026-11-01). The Activity's Attendance
// Windows card shows the two as separate columns -- DKP and Closing window -- and the window pays
// what the two columns say together. Before that, the close bonus REPLACED the window's amount and
// ticking the close wiped a hand-set price so the bonus could show through.
//
// The close is ONLY the window an officer TICKED (EventAttendanceWindow.IsClosingWindow), or on a
// named Open/Close camp, its Close post -- see ResolveCloseWindow. There is no guess any more: a
// numbered camp nobody ticked pays no close bonus, and the addon will not end one until a close is
// ticked. The guess ("the pop window, else the latest window posted") used to land the close bonus
// on whichever window was newest, which is why every window on a camp ended up carrying one.
//
// A named camp with only its Open posted falls back onto window 1, and that is inert: window 1
// pays open + close only when an officer TICKED it (the camp popped in its opener).
//
// Each bonus reads Event.Hnm*BonusOverride first and falls back to the linkshell value when that
// is null — see HnmCampPricing.StandardBonuses, which owns that precedence for this service, for
// WdCampFinalizer, and for the rate the in-game addon is told.
//
// DkpAmount is the officer's price for ONE window, set from the Activity's Attendance Windows card
// or typed into the addon's "Dkp this window" box before a post. It REPLACES the window's base
// amount (the open, or the regular rate); the close bonus still adds on top — see WindowValue.
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

    // What ONE window pays each member scanned in it: its own amount, plus the open bonus on window
    // 1, plus the close bonus on the close. The Activity shows the three as its DKP, Open bonus and
    // Closing window columns. So window 1 of a camp that popped in its opener pays regular + open +
    // close.
    //
    // Mirrored in the Activity by EventsTabComponent.windowValue — the same rule in two languages.
    // They must move together.
    public static double WindowValue(
        int sequence, int closeWindow, double? explicitAmount,
        double windowBonus, double openBonus, double closeBonus,
        bool isKillWindow = false, bool closeIsMarked = false)
        => BaseWindowValue(explicitAmount, windowBonus, isKillWindow)
         + OpenWindowValue(sequence, openBonus, isKillWindow)
         + CloseWindowValue(sequence, closeWindow, closeBonus, isKillWindow, closeIsMarked);

    // The window's OWN amount, before either bonus: what the "DKP this window" box and the
    // Activity's DKP column edit. The regular window rate, on every window -- window 1 included.
    //
    // It used to be the open bonus on window 1, REPLACING the regular rate there, so the open paid
    // less than a plain window whenever the rate was higher than the bonus. Window 1 is a window
    // like any other now, and the open bonus is added on top (OpenWindowValue).
    //
    // An officer's explicit amount replaces the regular rate. An explicit 0 is a REAL zero, not
    // "unset": an officer who deliberately zeroes a window must be able to make it stick.
    //
    // A kill window is worth 0 as a window. It is the record of who was standing there when the
    // mob died, and the kill bonus pays for that (ComputeMemberDkp). Pricing it as a regular window
    // on top would pay the late arrivals twice for one appearance.
    public static double BaseWindowValue(double? explicitAmount, double windowBonus, bool isKillWindow = false)
    {
        if (explicitAmount is { } amount) return Math.Max(0d, amount);
        if (isKillWindow) return 0d;
        return windowBonus;
    }

    // The open bonus this window adds: the camp's open bonus on window 1, 0 everywhere else.
    //
    // Gated on window 1 SPECIFICALLY, not on "the earliest window posted": a camp can hold a Close
    // with no Open at all (an officer who only reached camp for the kill posts one window, and the
    // addon files it as 2), and that roster was never observed at the open. Pinned by
    // CloseOnlyCamp_* in HnmStandardMemberGatingTests.
    public static double OpenWindowValue(int sequence, double openBonus, bool isKillWindow = false)
        => !isKillWindow && sequence == 1 ? openBonus : 0d;

    // The close bonus this window adds: the camp's close bonus on the close, 0 everywhere else.
    //
    // One exception: a close that only FELL BACK onto window 1 (a named Open/Close camp with just
    // its Open posted) adds nothing. Window 1 adds the close only when an officer TICKED it
    // (`closeIsMarked`) -- the camp popped in its opener.
    public static double CloseWindowValue(
        int sequence, int closeWindow, double closeBonus,
        bool isKillWindow = false, bool closeIsMarked = false)
    {
        if (isKillWindow || closeWindow <= 0 || sequence != closeWindow) return 0d;
        if (sequence == 1 && !closeIsMarked) return 0d;
        return closeBonus;
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
    // window, priced by WindowValue (open + close when the officer ticked it, the open alone when
    // the close only fell back onto it). Pass atClose: false for it — a caller that passes both
    // would be describing a member scanned in two windows on a camp that only ever had one.
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

    // Which window counts as the camp's "close", or 0 for none.
    //
    // The officer's tick wins outright. Without one, only a named Open/Close camp has a close
    // (`closeIsNamedPost`, DiscordEventMessageBuilder.PostsOpenAndClose): its posts are roles, and
    // pressing Post Close rather than Post Open already said which one closes the camp, so the
    // close is its latest post. The addon offers no tick on those camps for that reason.
    //
    // A numbered camp nobody ticked has NO close and pays no close bonus. It used to fall back to
    // the pop window, else the latest window posted -- a guess that made every window in turn look
    // like the close while it was newest. The addon now refuses End Camp until a close is ticked,
    // and the Activity warns before ending one without it.
    //
    // `postedWindows` must EXCLUDE kill windows — the Post Kill roster is filed after the close and
    // would otherwise take the close bonus off the window that earned it. Callers filter.
    //
    // A named camp with only its Open posted answers 1, and that is inert: CloseWindowValue adds
    // the close to window 1 only when it was TICKED there (HasMarkedCloseWindow).
    public static int ResolveCloseWindow(
        IReadOnlyCollection<int> postedWindows, int? markedCloseWindow = null,
        bool closeIsNamedPost = false)
    {
        if (markedCloseWindow is > 0) return markedCloseWindow.Value;
        if (closeIsNamedPost && postedWindows.Count > 0) return postedWindows.Max();
        return 0;
    }

    // Whether an officer TICKED a closing window, rather than the close falling back to the latest
    // post. WindowValue's `closeIsMarked`: only a tick makes window 1 pay open + close. Kill rows
    // are skipped for the same reason ResolveCloseWindow skips them.
    public static bool HasMarkedCloseWindow(IEnumerable<EventAttendanceWindow> windows)
        => windows.Any(w => w.IsClosingWindow && !w.IsKillWindow);

    // The same question asked of the ROWS, which is how every caller outside this file has it.
    //
    // Exists so the two filters that make the answer correct — find the ticked box, drop the kill
    // windows — are applied in one place instead of at each call site. They were open-coded nowhere
    // before this because neither concept existed; adding two flags and trusting three callers to
    // remember both is how the addon's window number and the board's drifted apart the last time.
    public static int ResolveCloseWindow(
        IEnumerable<EventAttendanceWindow> windows, bool closeIsNamedPost = false)
    {
        var rows = windows as IReadOnlyCollection<EventAttendanceWindow> ?? windows.ToList();
        var marked = rows.FirstOrDefault(w => w.IsClosingWindow && !w.IsKillWindow);
        return ResolveCloseWindow(
            rows.Where(w => !w.IsKillWindow).Select(w => w.SequenceNumber).Distinct().ToList(),
            marked?.SequenceNumber,
            closeIsNamedPost);
    }

    // One account's scan in one window: the only facts about a window scan that pricing reads.
    public readonly record struct MemberWindowScan(
        string AppUserId, int SequenceNumber, double? DkpAmount, bool IsClosingWindow, bool IsKillWindow);

    // The camp priced: what each ACCOUNT is owed from its window scans plus the claim and kill
    // bonuses, and the close window and kill windows that decided it.
    public sealed record CampPricing(
        IReadOnlyDictionary<string, double> DkpByAppUserId, int CloseWindow, IReadOnlySet<int> KillWindows);

    // The pricing half of BuildRosterAsync, with the database reads taken out. End Camp pays from
    // it, and the Activity card projects a live camp's "Event Total" from it, so the number a member
    // is shown is the number they are paid. Pass the bonuses already gated the way the caller means
    // them (End Camp: by claimed / killed; the live projection: ungated).
    //
    // Keyed on the account, case-insensitively: a member scanned on an alt in one window and on
    // their main in another is one person. A tagger with no scan at all is owed the claim bonus
    // alone, the same as BuildRosterAsync's tagger rows.
    public static CampPricing PriceMembers(
        IReadOnlyCollection<MemberWindowScan> scans, IReadOnlySet<string> taggedAppUserIds,
        bool closeIsNamedPost,
        double windowBonus, double openBonus, double closeBonus,
        double claimBonus, double killBonus, double step)
    {
        // The officer's ticked box, if there is one. Kill windows are excluded from the named-camp
        // fallback below for the reason ResolveCloseWindow gives — the Post Kill roster is filed
        // after the close and must not take the close bonus off the window that earned it.
        var markedCloseWindow = scans
            .Where(s => s.IsClosingWindow && !s.IsKillWindow)
            .Select(s => (int?)s.SequenceNumber)
            .FirstOrDefault();
        var closeWindow = ResolveCloseWindow(
            scans.Where(s => !s.IsKillWindow).Select(s => s.SequenceNumber).Distinct().ToList(),
            markedCloseWindow, closeIsNamedPost);

        // Price every posted window ONCE, up front. The amount is a property of the window, so
        // resolving it per member would be the same lookup N times over.
        var valueBySequence = scans
            .GroupBy(s => s.SequenceNumber)
            .ToDictionary(
                g => g.Key,
                g => WindowValue(
                    g.Key, closeWindow, g.First().DkpAmount,
                    windowBonus, openBonus, closeBonus, g.First().IsKillWindow,
                    closeIsMarked: markedCloseWindow is not null));

        // Which sequences are the Post Kill rosters. A camp can hold more than one if the officer
        // posted a kill, corrected it and posted again; being in ANY of them is the kill.
        var killWindows = scans
            .Where(s => s.IsKillWindow)
            .Select(s => s.SequenceNumber)
            .ToHashSet();

        var dkpByAppUserId = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (var account in scans.GroupBy(s => s.AppUserId, StringComparer.OrdinalIgnoreCase))
        {
            var windows = account.Select(s => s.SequenceNumber).ToHashSet();
            dkpByAppUserId[account.Key] = ComputeMemberDkp(
                windows.Select(seq => valueBySequence.GetValueOrDefault(seq)),
                taggedAppUserIds.Contains(account.Key), windows.Overlaps(killWindows),
                claimBonus, killBonus, step);
        }
        if (claimBonus > 0)
        {
            foreach (var tagger in taggedAppUserIds)
            {
                dkpByAppUserId.TryAdd(tagger, DkpRounding.Round(claimBonus, step));
            }
        }

        return new CampPricing(dkpByAppUserId, closeWindow, killWindows);
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
        Event ev, Linkshell linkshell, bool claimed, bool killed,
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

        var step = DkpRounding.StepFor(linkshell.DkpRoundingIncrement);

        // Every amount below comes from here — the same pricing the Activity card's Event Total
        // projects a live camp with.
        var pricing = PriceMembers(
            scans.Select(s => new MemberWindowScan(
                    s.AppUserId, s.SequenceNumber, s.DkpAmount, s.IsClosingWindow, s.IsKillWindow))
                .ToList(),
            taggedAppUserIds, DiscordEventMessageBuilder.PostsOpenAndClose(ev),
            windowBonus, openBonus, closeBonus, claimBonus, killBonus, step);

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

        var members = new List<HnmCampMember>();
        foreach (var appUserId in windowsByAppUserId.Keys)
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

            // A member who scanned but qualified for nothing is still LISTED — they were at the
            // camp, and that's what the review page is for. They just carry 0, which an officer
            // can raise. (The old pay-direct path dropped them to avoid ledger noise; a pending
            // row isn't ledger noise.)
            //
            // The amount is PriceMembers': the windows they were scanned in, plus the claim (their
            // name on the Claim Shield) and the kill (on the Post Kill roster), each outcome
            // answering to its OWN evidence.
            members.Add(new HnmCampMember(
                AppUserId: appUserId,
                CharacterName: characterName.Trim(),
                JobName: participation?.JobName,
                SubJobName: participation?.SubJobName,
                Dkp: pricing.DkpByAppUserId.GetValueOrDefault(appUserId)));
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
            ev.Id, members.Count, pricing.CloseWindow, taggedAppUserIds.Count,
            pricing.KillWindows.Count, claimed, killed);
        return members;
    }
}
