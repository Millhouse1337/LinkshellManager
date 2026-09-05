using LinkshellManagerDiscordApp.Data;
using LinkshellManagerDiscordApp.Models;
using Microsoft.EntityFrameworkCore;

namespace LinkshellManagerDiscordApp.Services;

// One member of an ended HNM camp and what that camp proposes to pay them. Produced by
// WdCampFinalizer / HnmStandardCampFinalizer (each owns its own mode's formula) and consumed by
// HnmCampReviewHandoffService below. Dkp may be 0 — presence is worth recording even when the
// linkshell has configured no bonuses.
public sealed record HnmCampMember(
    string? AppUserId,
    string CharacterName,
    string? JobName,
    string? SubJobName,
    double Dkp);

// Turns an ended HNM camp into a PENDING REVIEW ROW in the Event System page's attendance
// sections, instead of paying DKP straight into the ledger.
//
// Before this existed the two attendance modes each paid at End Camp — Manual Check In after a
// 15-minute "Awaiting Processing" grace, Standard immediately — and neither ever appeared where
// addon snapshots already get reviewed before their DKP is posted. The grace was standing in for
// that missing review step. Now both modes hand off here, the officer reviews, and their Post is
// what credits DKP (WindowEventDkpLedgerService).
//
// Shaped to fit the EXISTING snapshot pipeline rather than bolting a second one alongside it:
// credit is driven off AttendanceSnapshotEntry rows and per-character WindowEventMemberDkp
// overrides, so a camp row reviews, edits, posts, and reconciles exactly like a "/lsm now" one.
//
// STAGES ONLY — the caller must SaveChanges, in the same save as the roster teardown.
public sealed class HnmCampReviewHandoffService
{
    private readonly ApplicationDbContext _db;
    private readonly WdCampFinalizer _wdFinalizer;
    private readonly HnmStandardCampFinalizer _standardFinalizer;
    private readonly HnmAutoEventService _autoEvent;
    private readonly ILogger<HnmCampReviewHandoffService> _logger;

    public HnmCampReviewHandoffService(
        ApplicationDbContext db,
        WdCampFinalizer wdFinalizer,
        HnmStandardCampFinalizer standardFinalizer,
        HnmAutoEventService autoEvent,
        ILogger<HnmCampReviewHandoffService> logger)
    {
        _db = db;
        _wdFinalizer = wdFinalizer;
        _standardFinalizer = standardFinalizer;
        _autoEvent = autoEvent;
        _logger = logger;
    }

    // Builds the camp's roster and stages the review row for it. Returns the staged WindowEvent,
    // or null when the camp had nobody on it (an empty camp leaves no row to review).
    //
    // MUST be called BEFORE the caller wipes the roster: the Standard roster reads
    // AppUserEventWindow, which cascades off AppUserEventId.
    public async Task<WindowEvent?> StageHandoffAsync(
        Event ev, int popWindow, bool claimed, bool killed, CancellationToken cancellationToken)
    {
        var linkshell = ev.Linkshell
            ?? await _db.Linkshells.FirstOrDefaultAsync(l => l.Id == ev.LinkshellId, cancellationToken);
        if (linkshell is null) return null;

        var isWd = DiscordEventMessageBuilder.IsWd(ev);
        // FIRST, and before anything below touches the camp: both finalizers read the claim bonus
        // off ClaimShieldCapture.EventId, which the re-parent at the end of this method clears.
        var members = isWd
            ? await _wdFinalizer.BuildRosterAsync(ev, linkshell, popWindow, claimed, killed, cancellationToken)
            : await _standardFinalizer.BuildRosterAsync(ev, linkshell, popWindow, claimed, killed, cancellationToken);

        // This camp's lotteries, to be handed to the archive below.
        //
        // Loaded here rather than after the empty check, because they have to be detached from the
        // board EITHER WAY. The board is recycled for the next pop, and a capture still pointing at
        // it is one the next camp's finalizer counts as its own — which is how the claim bonus came
        // to be paid again on every subsequent pop of the same board.
        var captures = await _db.ClaimShieldCaptures
            .Where(capture => capture.EventId == ev.Id)
            .ToListAsync(cancellationToken);

        // Collapse to one row per CHARACTER NAME. Both the snapshot entries and the override rows
        // are keyed by name downstream — and WindowEventMemberDkp has a UNIQUE (WindowEventId,
        // CharacterName) index, so a duplicate name here would throw on save. Keep the HIGHEST
        // amount on a collision so a name clash can never silently underpay someone.
        var byCharacterName = new Dictionary<string, HnmCampMember>(StringComparer.OrdinalIgnoreCase);
        foreach (var member in members)
        {
            var key = member.CharacterName.Trim();
            if (key.Length == 0) continue;
            if (byCharacterName.TryGetValue(key, out var existing))
            {
                if (member.Dkp > existing.Dkp)
                {
                    byCharacterName[key] = member with { CharacterName = key };
                }
                _logger.LogWarning(
                    "HNM camp handoff: event {EventId} has two roster rows for character {Character}; "
                    + "keeping the larger amount.", ev.Id, key);
                continue;
            }
            byCharacterName[key] = member with { CharacterName = key };
        }
        if (byCharacterName.Count == 0)
        {
            // Nobody to review, so no archive to hand the captures to — but they still must not
            // follow the board into the next pop. Detached and left as linkshell-level records,
            // which is where a capture taken with no camp open already lives.
            foreach (var capture in captures) capture.EventId = null;
            _logger.LogInformation(
                "HNM camp handoff skipped: event {EventId} ended with nobody on the roster.", ev.Id);
            return null;
        }

        var nowUtc = DateTime.UtcNow;
        var monster = ev.AssignedMonsterName?.Trim();

        var windowEvent = new WindowEvent
        {
            LinkshellId = ev.LinkshellId,
            Name = ev.EventName,
            // NormalizedName is deliberately left NULL. It is the key the addon's
            // FindOrCreateWindowEventAsync matches on to fold a later "/lsm now <monster>" into an
            // open event — and folding un-priced scan entries into a camp row whose amounts are
            // already computed would quietly add members at the 0 baseline. Officers can still
            // attach a snapshot by hand from the card.
            NormalizedName = null,
            Status = WindowEventStatuses.Open,
            CreatedAtUtc = nowUtc,
            FirstCapturedAtUtc = nowUtc,
            LastCapturedAtUtc = nowUtc,
            // Pre-tag the camp so the sheet's Entry Type column is right without officer input.
            EntryType = WindowEventEntryTypes.FromMonsterName(monster ?? ev.EventName),
            // Everyone gets an explicit per-character override below, so this baseline only ever
            // applies to someone an officer ADDS during review. 0 (not the 1.5 default) keeps an
            // accidental add from silently paying — note WindowEventDkp.Resolve treats 0 as a real
            // value and won't fall through to its default.
            DkpAmount = 0d,
            PostedToSheetAt = null,          // ← pending review; Post is what credits DKP
            SourceEventId = ev.Id,
            // Snapshotted because they are NOT recoverable later: the pop re-points
            // Event.StartTime to the next predicted repop and clears CommencementStartTime.
            CampStartedAtUtc = ev.CommencementStartTime ?? ev.StartTime,
            CampEndedAtUtc = nowUtc,
            CampEventType = ev.EventType,
            CampEventLocation = ev.EventLocation,
        };
        _db.WindowEvents.Add(windowEvent);

        // ONE SNAPSHOT PER POSTED WINDOW, built from the camp's REAL scans.
        //
        // This used to be a single synthetic snapshot holding the deduped roster, named "Camp
        // roster · window N". It made the review card lie about the camp in four ways at once: it
        // reported "1 snapshot" for a camp that posted an Open, a Close and a Kill; it carried no
        // WindowNumber, so every capture rendered as "Unassigned"; and it had no AllianceNumber
        // and no per-entry Zone, so both columns sat empty. All four are the same omission --
        // the camp's per-window rows were right there in EventAttendanceWindow / AppUserEventWindow
        // and simply were not read.
        //
        // The MONEY is untouched. Amounts come from the per-character WindowEventMemberDkp rows
        // below, and AttendanceSectionsBuilder.BuildCombinedMembers takes
        // `overrideAmount ?? baseAmount` -- so folding one snapshot into several changes what the
        // card SHOWS about presence, never what it pays.
        //
        // Read before the caller tears the roster down, like everything else here: these rows
        // cascade off AppUserEventId.
        // Loaded ONCE and used twice: to build the snapshots below, and to re-parent the rows
        // onto the archive further down. Tracked (no AsNoTracking) precisely so the second use
        // gets these same instances and the re-parent actually persists.
        var campWindows = await _db.EventAttendanceWindows
            .Where(w => w.EventId == ev.Id)
            .Include(w => w.Attendees)
            .OrderBy(w => w.SequenceNumber)
            .ToListAsync(cancellationToken);

        // Jobs live on the ROSTER (they come off the participation), not on the scan row, so they
        // are merged in by name rather than re-derived.
        var rosterByName = byCharacterName.Values.ToDictionary(
            m => m.CharacterName, m => m, StringComparer.OrdinalIgnoreCase);

        // PRICE THE CAPTURES, not just the members.
        //
        // A Standard camp's windows are each worth a different thing — the open, the close, the
        // regular rate — so a single per-member total can only ever render as the same number in
        // every window, which is exactly what the review card showed. Writing each window's own
        // value onto its capture is what lets the card say "window 1 paid the open" and lets an
        // officer re-price ONE window.
        //
        // Resolved through the same ResolveCloseWindow and the same HnmCampPricing the finalizer
        // just used, so these amounts sum back to the totals it computed rather than to a second
        // opinion about the same camp.
        //
        // THE KILL POST is the exception, and deliberately so. WindowValue prices it at 0 as a
        // window, because it is not a roster read of the camp — it is the record of who was standing
        // there when the mob died, and the KILL BONUS is what pays for that. So the kill capture
        // carries that bonus rather than a zero, which is what makes it the kill post's own capture
        // instead of a row that silently pays nothing. Only the tag bonus is left for the Tags
        // capture below: the two are separate things earned by separate evidence.
        //
        // Manual Check In camps are excluded (HonoursWindowAmount): their credit comes from the
        // check-in RANGE, so a member is paid for windows that have no capture at all and there is
        // no honest per-capture number to write. They keep the per-member rows below.
        var perCapture = HnmCampPricing.HonoursWindowAmount(ev);
        var closeWindow = HnmStandardCampFinalizer.ResolveCloseWindow(campWindows, popWindow);
        var (_, _, _, _, killBonus) = HnmCampPricing.StandardBonuses(ev, linkshell, claimed, killed);
        var valueBySequence = perCapture
            ? campWindows.ToDictionary(
                w => w.SequenceNumber,
                w => HnmCampPricing.WindowValueFor(
                    ev, linkshell, w.SequenceNumber, closeWindow, w.DkpAmount, w.IsKillWindow) ?? 0d)
            : new Dictionary<int, double>();
        windowEvent.PerCaptureDkp = perCapture;

        // What each member has been priced for so far, so the Tags capture below can carry the rest
        // of what the finalizer owes them.
        //
        // Keyed on the ACCOUNT wherever there is one, because the capture rows are keyed on the
        // SCANNED character and the roster is keyed on the member's MAIN. Someone caught on an alt
        // is scanned as "Athmilk" and rostered as "Edicius", and matching those two by name would
        // read as a member who earned no window at all — handing them a tag row for the whole total
        // on top of the windows they were actually paid for.
        static string PriceKey(string? accountId, string characterName)
            => string.IsNullOrWhiteSpace(accountId) ? $"name:{characterName}" : accountId!;
        var pricedByKey = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        var scannedAccounts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // The kill bonus is earned by being in the Post Kill roster AT ALL. A camp can hold more
        // than one kill capture — the officer posted, spotted a miss, posted again — and the
        // finalizer pays for that once, so this pays it on the first one and zero on the rest.
        var killPaidKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Everyone a window actually caught. What is left over is added below, because a roster
        // member with no scan is a REAL case -- a Claim Shield tagger who never appeared in one --
        // and dropping them would delete a payout the finalizer already decided on.
        var scannedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var window in campWindows)
        {
            var scans = window.Attendees
                .Where(a => !string.IsNullOrWhiteSpace(a.CharacterName))
                .ToList();
            if (scans.Count == 0)
            {
                continue;   // a window nobody was scanned in has nothing to show
            }

            var windowSnapshot = new AttendanceSnapshot
            {
                LinkshellId = ev.LinkshellId,
                WindowEvent = windowEvent,
                LinkedEventId = ev.Id,
                // The window's OWN timestamps and identity, so the card reads as the camp
                // happened rather than as one lump filed at End Camp.
                CapturedAtUtc = window.PostedAt,
                CreatedAtUtc = nowUtc,
                SnapshotStatus = AttendanceSnapshotStatuses.Active,
                // Named for the ROLE the window is paid as, which is the one thing an officer
                // reviewing the money needs from it: "Open", "Close", "Kill", or "Window N" for the
                // ordinary ones in between. Derived from the same three facts WindowValue prices on
                // — sequence 1, the resolved close, the kill flag — so the name and the amount can
                // never disagree.
                //
                // window.Label only names the two ends on a 2-POST camp (HnmConfig
                // .GetDefaultWindowLabel returns null for any other count), so a 7-window camp used
                // to hand off three captures called "Window 1", "Window 2", "Window 3" with nothing
                // saying which was the open and which the close. It is kept as the fallback for the
                // middle windows, where it is the addon's own wording.
                Name = isWd
                    ? $"Check In roster · window {window.SequenceNumber}"
                    : WindowRoleLabel(window, closeWindow),
                // What made every capture render as "Unassigned".
                WindowNumber = window.SequenceNumber,
                SlotKind = AttendanceSnapshotSlotKinds.Window,
                // First alliance seen in the window. A camp fielding two alliances posts two
                // scans per window, and the combined roster unions them anyway -- this only has
                // to stop the column being blank.
                AllianceNumber = scans
                    .Where(a => a.AllianceNumber.HasValue)
                    .Select(a => a.AllianceNumber)
                    .FirstOrDefault(),
                AllianceKey = scans.Select(a => a.AllianceKey).FirstOrDefault(k => k != null),
                // Who filed it. PostedBySource is the addon's own marker ("lsm-addon (lsm)");
                // VerifiedBy on the scan is the fallback for rows written before it existed.
                CapturedByCharacterName = !string.IsNullOrWhiteSpace(window.PostedBySource)
                    ? window.PostedBySource
                    : scans.Select(a => a.VerifiedBy).FirstOrDefault(v => !string.IsNullOrWhiteSpace(v)),
                EntryCount = scans.Count,
            };
            _db.AttendanceSnapshots.Add(windowSnapshot);

            // One window pays a member ONCE, however many scan rows they have in it — the finalizer
            // sums over the set of SEQUENCES a member was seen in, not over the rows. An account
            // holding two participations can produce two rows for one window, and pricing both
            // would pay that window twice. The duplicate row still renders (it is what was
            // captured); it just carries nothing.
            var pricedThisWindow = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var scan in scans)
            {
                var name = scan.CharacterName!.Trim();
                scannedNames.Add(name);
                rosterByName.TryGetValue(name, out var rosterRow);
                var accountId = scan.AppUserId ?? rosterRow?.AppUserId;
                if (!string.IsNullOrWhiteSpace(accountId)) scannedAccounts.Add(accountId);

                double? entryAmount = null;
                if (perCapture && pricedThisWindow.Add(name))
                {
                    var priceKey = PriceKey(accountId, name);
                    entryAmount = window.IsKillWindow
                        ? (killPaidKeys.Add(priceKey) ? killBonus : 0d)
                        : valueBySequence.GetValueOrDefault(window.SequenceNumber);
                    pricedByKey[priceKey] = pricedByKey.GetValueOrDefault(priceKey) + entryAmount.Value;
                }

                windowSnapshot.Entries.Add(new AttendanceSnapshotEntry
                {
                    Snapshot = windowSnapshot,
                    CharacterName = name,
                    // Carries the account through to post time so credit doesn't depend on the
                    // character name matching one of the four names the name-resolver indexes.
                    AppUserId = scan.AppUserId ?? rosterRow?.AppUserId,
                    MainJob = TruncateJob(rosterRow?.JobName),
                    SubJob = TruncateJob(rosterRow?.SubJobName),
                    // The other blank column. Recorded per scan, so it is where they actually
                    // were for THAT window rather than wherever they ended up.
                    Zone = scan.Zone,
                    // What THIS window pays them. Null on a Manual Check In camp, which prices
                    // nothing per capture.
                    DkpAmount = entryAmount,
                });
            }
        }

        // Anyone the finalizer put on the roster that no window caught. Overwhelmingly a Claim
        // Shield tagger: HnmStandardCampFinalizer appends them precisely because tagging is
        // evidence of presence in its own right, and they can have no scan at all. Without this
        // they would carry a WindowEventMemberDkp override and appear in NO snapshot, which is a
        // row the combined roster never builds -- an officer would post the camp and silently not
        // pay them.
        // ...and, on a priced camp, THE TAGS: what a member earned for landing an action on the mob.
        //
        // Its own capture because its evidence is its own — the addon's tag list, not a roster
        // scan — so a tagger can be owed for it having appeared in no window at all, and equally a
        // member scanned in every window can be owed nothing here. That is the whole reason it
        // cannot ride on a window, and why it is separate from the kill post, which pays the people
        // who were standing there when the mob died. Two different questions, two captures.
        //
        // Carries the REMAINDER rather than re-reading the tag list: the windows above and the kill
        // capture have taken their share of what the finalizer computed, and whatever is left is the
        // tag bonus plus the rounding it snapped the total to. Defined that way it cannot disagree
        // with the finalizer, and the sum of a member's captures is exactly their payout.
        var extras = new List<(HnmCampMember Member, double? Amount)>();
        foreach (var member in byCharacterName.Values)
        {
            var scanned = !string.IsNullOrWhiteSpace(member.AppUserId)
                ? scannedAccounts.Contains(member.AppUserId!)
                : scannedNames.Contains(member.CharacterName);

            if (!perCapture)
            {
                // Manual Check In: the row exists only so an unscanned member still appears on the
                // roster. It prices nothing — the per-member amounts below are the payout.
                if (!scanned) extras.Add((member, null));
                continue;
            }

            var priced = pricedByKey.GetValueOrDefault(PriceKey(member.AppUserId, member.CharacterName));
            var remainder = member.Dkp - priced;

            // An unscanned member has to be listed even when they are owed nothing, or the combined
            // roster — which is built from capture entries — loses them entirely.
            if (!scanned || Math.Abs(remainder) > 0.0001)
            {
                extras.Add((member, remainder));
            }
        }

        if (extras.Count > 0)
        {
            var extraSnapshot = new AttendanceSnapshot
            {
                LinkshellId = ev.LinkshellId,
                WindowEvent = windowEvent,
                LinkedEventId = ev.Id,
                CapturedAtUtc = nowUtc,
                CreatedAtUtc = nowUtc,
                SnapshotStatus = AttendanceSnapshotStatuses.Active,
                Name = perCapture ? "Tag" : "Credited without a window",
                SlotKind = AttendanceSnapshotSlotKinds.Window,
                EntryCount = extras.Count,
            };
            _db.AttendanceSnapshots.Add(extraSnapshot);

            foreach (var (member, amount) in extras)
            {
                extraSnapshot.Entries.Add(new AttendanceSnapshotEntry
                {
                    Snapshot = extraSnapshot,
                    CharacterName = member.CharacterName,
                    AppUserId = member.AppUserId,
                    MainJob = TruncateJob(member.JobName),
                    SubJob = TruncateJob(member.SubJobName),
                    DkpAmount = amount,
                });
            }
        }

        // The amounts, one row per roster member — on a camp whose captures are NOT priced. There
        // the finalizer's answer is all there is, and it is deliberately built from the ROSTER
        // rather than from the snapshots: what a member is owed is not a function of how many
        // windows they turned up in.
        //
        // A priced camp writes none of these on purpose. Its payout is the sum of the capture
        // amounts above, so a per-member row could only be a second, stale copy of the same money —
        // and the one an officer never edits is exactly the one that would go stale.
        if (!perCapture)
        {
            foreach (var member in byCharacterName.Values)
            {
                windowEvent.MemberDkpOverrides.Add(new WindowEventMemberDkp
                {
                    WindowEvent = windowEvent,
                    CharacterName = member.CharacterName,
                    DkpAmount = member.Dkp,
                });
            }
        }

        // THE PAST EVENT, written HERE — at End Camp — rather than at Post.
        //
        // Ending a camp is what makes it past, and for these camps nothing else records that it
        // happened: the board is RECYCLED for the next pop rather than deleted, so a camp that
        // ended vanished from the live list, never appeared under Past Events, and existed only
        // as a pending review row until somebody got round to it. On a recurring board that gap
        // could run for days, and a camp nobody ever reviewed left no trace at all.
        //
        // Post still owns the money: WindowEventDkpLedgerService reconciles this history's roster
        // and amounts to whatever the review settled on, and writes the ledger. The amounts staged
        // below are the camp's own proposal — the same numbers the review row opens with — so the
        // archive is never blank and never disagrees with what the officer is looking at.
        var archive = await BuildCampArchiveAsync(
            ev, byCharacterName.Values, nowUtc, cancellationToken);
        windowEvent.CampEventHistory = archive;

        // The camp's lotteries move onto the archive with it. See ClaimShieldCapture.EventHistoryId
        // for why leaving them on the recycled board was paying the claim bonus over and over.
        foreach (var capture in captures)
        {
            capture.EventHistory = archive;
            capture.EventId = null;
        }

        // The camp's posted attendance windows move onto the archive for the same reason. These
        // rows are the ONLY record of which windows a camp posted and who was scanned in each --
        // nothing re-derives them. All three End Camp callers used to DELETE them once the board
        // recycled, so any camp that posted a ToD lost its entire window history and showed no
        // windows under Past Events. Clearing EventId also frees the unique
        // (EventId, SequenceNumber) index the recycled board needs for the next camp's window-1
        // scan, which is what those deletes were really for -- so archiving replaces them rather
        // than being additive. Matches both close paths (EventController.Lifecycle EndEventCore
        // and ActivityDataController EndEventAsync), which archive exactly this way.
        // Reuses campWindows from above rather than re-querying: same rows, same tracked
        // instances, one round trip.
        foreach (var window in campWindows)
        {
            window.EventHistory = archive;
            window.Event = null;
            window.EventId = null;
        }

        _logger.LogInformation(
            "HNM camp handed off for review: event {EventId} ({Monster}, mode {Mode}) staged "
            + "{Count} member(s) totalling {Total} DKP — archived as a past event, pending an "
            + "officer's Post.",
            ev.Id, monster, isWd ? "Manual Check In" : "Standard",
            byCharacterName.Count, byCharacterName.Values.Sum(m => m.Dkp));

        return windowEvent;
    }

    // The camp's Past Event row, staged (not saved) with one participant per member.
    //
    // Dated off the CAMP, not off the Event: the caller is about to recycle that row for the next
    // pop, which re-points StartTime at a repop that has not happened and clears
    // CommencementStartTime. Reading it later would date the archive to the future.
    private async Task<EventHistory> BuildCampArchiveAsync(
        Event ev,
        IEnumerable<HnmCampMember> members,
        DateTime endedAtUtc,
        CancellationToken cancellationToken)
    {
        var startedAtUtc = ev.CommencementStartTime ?? ev.StartTime;

        var history = new EventHistory
        {
            LinkshellId = ev.LinkshellId,
            EventName = ev.EventName,
            EventType = ev.EventType,
            EventLocation = ev.EventLocation,
            // Resolved to a TEMPLATE, never the per-event snapshot: a snapshot is cascade-deleted
            // with its event, so storing one here would dangle. Same call EndEventCoreAsync makes,
            // and it is what lets the next pop of this camp inherit the board.
            PartySetupId = await PartySetupInheritance.ResolveTemplateIdAsync(_db, ev, cancellationToken),
            StartDate = startedAtUtc?.Date,
            StartTime = startedAtUtc,
            EndTime = endedAtUtc,
            CommencementStartTime = ev.CommencementStartTime,
            Duration = startedAtUtc is { } startedAt
                ? (endedAtUtc - startedAt).TotalHours
                : ev.Duration,
            DkpPerHour = ev.DkpPerHour,
            EventDkp = ev.EventDkp,
            Details = ev.Details,
            CountsTowardActive = ev.CountsTowardActive,
            TimeStamp = endedAtUtc,
            AppUserEventHistories = new List<AppUserEventHistory>(),
        };

        // ONE row per ACCOUNT, not per character. The roster above is deduped by character name,
        // which is the right key for the review card — but AppUserEventHistory is uniquely indexed
        // on (EventHistoryId, AppUserId), so a member scanned on both their main and an alt would
        // make this save throw and take the whole End Camp down with it. Keep the larger amount on
        // a collision, matching the character-name fold above, so a clash can never underpay.
        var seenAppUserIds = new Dictionary<string, AppUserEventHistory>(StringComparer.OrdinalIgnoreCase);
        foreach (var member in members)
        {
            var row = new AppUserEventHistory
            {
                AppUserId = member.AppUserId,
                CharacterName = member.CharacterName,
                JobName = member.JobName,
                SubJobName = member.SubJobName,
                StartTime = startedAtUtc,
                Duration = null,
                EventDkp = member.Dkp,
                IsQuickJoin = true,
                IsVerified = true,
                ActiveCredit = true,
            };

            // A member with no account is not covered by the unique index (it is filtered to
            // non-null AppUserId), so those rows are all kept.
            if (string.IsNullOrWhiteSpace(member.AppUserId))
            {
                history.AppUserEventHistories.Add(row);
                continue;
            }
            if (seenAppUserIds.TryGetValue(member.AppUserId, out var existing))
            {
                if (member.Dkp > (existing.EventDkp ?? 0d))
                {
                    existing.EventDkp = member.Dkp;
                    existing.CharacterName = member.CharacterName;
                    existing.JobName = member.JobName;
                    existing.SubJobName = member.SubJobName;
                }
                continue;
            }
            seenAppUserIds[member.AppUserId] = row;
            history.AppUserEventHistories.Add(row);
        }

        _db.EventHistories.Add(history);
        return history;
    }

    // Does this event end through the CAMP path instead of the generic one?
    //
    // Named here because all three generic End Event actions — web, Activity and the in-game
    // addon — have to ask it, and when each of them wrote the condition out by hand, two got a
    // NARROWER one than intended (Manual Check In only) and the third got none at all. The result
    // was that every Standard camp ended through the generic path and was archived paying 0: that
    // path multiplies windowsAttended by Event.DkpPerHour, which is forced to 0 on HNM camps
    // exactly because a camp is priced by HnmCampPricing's shape bonuses instead.
    //
    // Deliberately not "is it windowed". A Claim/Kill-style windowed event that is NOT an HNM camp
    // really is paid windowsAttended × DkpPerHour by EndEventCoreAsync — HnmCampPricing
    // .WindowValueFor says so outright — so IsHnm is the line between the two payout models, and
    // AttendanceMode is a distinction WITHIN the HNM side. Both modes answer true here.
    //
    // WdFinalizedAt is the same idempotence latch HandOffAndRecycleAsync gates on: a camp already
    // handed off answers false, so a second End Event falls through to the generic path — which is
    // what actually removes the recycled board.
    public static bool EndsThroughCampPath(Event ev)
        => DiscordEventMessageBuilder.IsHnm(ev) && ev.WdFinalizedAt is null;

    // "End this camp" for callers OUTSIDE the board's End Camp form — the generic End Event
    // actions, which would otherwise archive an HNM camp as a normal event and pay 0, discarding
    // the check-in credit. Replaces the old WdCampFinalizer.FinalizeAsync at those call sites:
    // same shape (load, hand off, recycle the board, save), except it stages a review row instead
    // of writing DKP. Returns true if it handed the camp off this call.
    //
    // Idempotent on WdFinalizedAt, which is what the two callers already gate on.
    public async Task<bool> HandOffAndRecycleAsync(int eventId, CancellationToken cancellationToken)
    {
        var ev = await _db.Events
            .Include(e => e.Linkshell)
            .FirstOrDefaultAsync(e => e.Id == eventId, cancellationToken);
        if (ev is null || ev.WdFinalizedAt is not null) return false;

        var nowUtc = DateTime.UtcNow;
        var effectiveCount = DiscordEventMessageBuilder.EffectiveWindowCount(ev);
        var popWindow = Math.Clamp(ev.WdPopWindow ?? ev.HnmWindowNumber, 1, effectiveCount);

        // THE CAMP'S OUTCOME, from evidence rather than from the two Manual Check In columns.
        //
        // This used to pass ev.WdClaimed / ev.WdKilled straight through, and those are only ever
        // WRITTEN on a Manual Check In camp -- EventController.HnmBoard sets them inside an
        // `if (isWd)`. On a STANDARD camp they sat at their `false` defaults, so every camp ended
        // from this path (the addon's End Event, and the generic End Event actions) handed the
        // finalizer claimed:false, killed:false. HnmCampPricing.StandardBonuses then zeroed BOTH
        // outcome bonuses, and a camp that was claimed and killed paid its open and close and
        // nothing else. The board's own End Camp form was unaffected, which is why this only
        // showed up in game.
        //
        // Both facts are already recorded, so neither needs asking for:
        //   claimed -- a Claim Shield capture exists for this camp. The addon only files one when
        //              an action LANDED on the mob, which is the definition of having claimed it.
        //              (Which MEMBERS get paid is still the capture list's call -- the finalizer
        //              reads it separately. This decides only whether the bonus applies at all.)
        //   killed  -- a kill roster was posted, or a ToD was recorded. Defaults to TRUE when
        //              neither is present, matching `killed ?? true` in the two board End Camp
        //              paths: a camp is ended because the mob died unless someone says otherwise.
        var claimed = ev.WdClaimed;
        var killed = ev.WdKilled;
        if (!DiscordEventMessageBuilder.IsWd(ev))
        {
            claimed = await _db.ClaimShieldCaptures
                .AnyAsync(capture => capture.EventId == ev.Id, cancellationToken);
            killed = ev.HnmDefeatedAt is not null
                || await _db.EventAttendanceWindows
                    .AnyAsync(w => w.EventId == ev.Id && w.IsKillWindow, cancellationToken);
            if (!killed && ev.HnmDefeatedAt is null)
            {
                // Nothing says it did NOT die, and ending a camp is what you do when it has.
                killed = true;
            }
        }

        await StageHandoffAsync(ev, popWindow, claimed, killed, cancellationToken);

        ev.WdFinalizedAt = nowUtc;
        ev.HnmDefeatedAt ??= nowUtc;
        ev.CommencementStartTime = null;
        ev.HnmWindowNumber = 1;

        var participations = await _db.AppUserEvents
            .Where(p => p.EventId == eventId)
            .ToListAsync(cancellationToken);
        _db.AppUserEvents.RemoveRange(participations);
        var slotSignups = await _db.EventPartySlotSignups
            .Where(s => s.EventId == eventId)
            .ToListAsync(cancellationToken);
        _db.EventPartySlotSignups.RemoveRange(slotSignups);
        // The camp's attendance windows are NOT deleted here: StageHandoffAsync above moved them
        // onto the camp archive (EventId cleared), which both frees the unique
        // (EventId, SequenceNumber) index for the recycled board and keeps the window history
        // visible under Past Events. Re-querying and removing them here would undo that -- the
        // query reads the pre-save database rows and EF hands back the same tracked instances.

        await _db.SaveChangesAsync(cancellationToken);

        // The board is parked now, so hand it the ToD that was settled for this kill. Must run
        // AFTER the save above: it reads the parked row back out of the database.
        //
        // Failures here must not bubble up. The camp is handed off and the roster is torn down by
        // the line above — that is this method's contract, and it is already committed. Re-pointing
        // the board at the new pop is a downstream convenience the poller and the ToD tracker's own
        // sync can still put right, so reporting the end as failed would be a lie about the part
        // that mattered.
        try
        {
            await AdoptSettledTodAsync(ev, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Re-pointing camp {EventId} at its settled ToD failed.", ev.Id);
        }
        return true;
    }

    // Point the recycled board at the kill's Time of Death.
    //
    // The addon posts that ToD in its OWN call, before this one — from the End Event dialog, or
    // from the ToD Capture panel earlier in the night — so by the time the board parks, a ToD newer
    // than the one it was created from can already exist. The board's own End Camp form has always
    // re-pointed StartTime / SourceTodId / HnmRepostAt at the ToD it logs (HnmCampPopService); until
    // this ran, a camp ended from the addon skipped all of it and sat there advertising the PREVIOUS
    // pop's repop time with no re-post scheduled at all.
    //
    // Two outcomes, because two different things can own the next pop:
    //
    //   * A standing Repeat-on-ToD board owns it. The poller re-posts LeadHours before the pop,
    //     which is what the officer asked for by enabling it, so the board STAYS parked and only its
    //     advertised times move onto the new cycle — via the same shared sync the ToD tracker uses
    //     when a ToD is corrected. Stamping SourceTodId is also what lets the poller recognise this
    //     row as the new cycle's board instead of posting a second one beside it.
    //
    //   * Nothing does, so re-queue it right now: the streamlined addon workflow's "the next pop is
    //     already on the board" behaviour, which is what the ToD post itself used to provide.
    //     Handed to HnmAutoEventService because the camp is parked (queued) by this point, so its
    //     own recycle finds THIS row and revives it with the composed name, next day and next
    //     monster it would otherwise have given a brand new event.
    //
    // Either way the officer is left with ONE row. No newer ToD — a camp ended without one, and the
    // web / Activity End Event actions, which post none — leaves the board exactly as it was.
    private async Task AdoptSettledTodAsync(Event ev, CancellationToken cancellationToken)
    {
        var monster = ev.AssignedMonsterName?.Trim();
        if (string.IsNullOrWhiteSpace(monster))
        {
            return;
        }

        var latestTodId = await HnmRecurringBoardService.LatestTodIdAsync(
            _db, ev.LinkshellId, monster, cancellationToken);
        if (latestTodId is not { } todId || todId <= (ev.SourceTodId ?? 0))
        {
            return;
        }

        var board = await HnmRecurringBoardService.FindAsync(_db, ev.LinkshellId, monster, cancellationToken);
        if (board?.Enabled == true)
        {
            ev.SourceTodId = todId;
            await _db.SaveChangesAsync(cancellationToken);
            await HnmRecurringBoardService.SyncParkedBoardsForTodAsync(
                _db, ev.LinkshellId, monster, cancellationToken);
            _logger.LogInformation(
                "HNM camp {EventId} ('{Monster}') parked on tod {TodId}; its Repeat-on-ToD board re-posts before the pop.",
                ev.Id, monster, todId);
            return;
        }

        await _autoEvent.CreateAutoEventForTodAsync(todId, cancellationToken);
    }

    // What this window is PAID as, said in one word. The three roles are exactly the branches of
    // HnmStandardCampFinalizer.WindowValue, tested in its order: the kill roster first (it is not a
    // roster read of the camp at all), then sequence 1, which is the open by definition, then the
    // resolved close.
    //
    // Anything else is an ordinary window and keeps whatever the addon called it, falling back to
    // its number. NormalizeWindowLabel maps the two pre-rename labels ("On Time", "Claim/Kill") so
    // a camp that spans the rename doesn't name half its captures the old way.
    private static string WindowRoleLabel(EventAttendanceWindow window, int closeWindow)
    {
        if (window.IsKillWindow) return HnmConfig.KillWindowLabel;
        if (window.SequenceNumber == 1) return HnmConfig.OpenWindowLabel;
        if (closeWindow > 0 && window.SequenceNumber == closeWindow) return HnmConfig.CloseWindowLabel;

        var label = HnmConfig.NormalizeWindowLabel(window.Label);
        return string.IsNullOrWhiteSpace(label) ? $"Window {window.SequenceNumber}" : label;
    }

    // AttendanceSnapshotEntry.MainJob/SubJob are the addon's 3-letter codes (MaxLength 8), but
    // AppUserEvent.JobName is unbounded and can hold a full "White Mage". Trim rather than let
    // the insert throw.
    private static string? TruncateJob(string? job)
    {
        var trimmed = job?.Trim();
        if (string.IsNullOrEmpty(trimmed)) return null;
        return trimmed.Length <= 8 ? trimmed : trimmed[..8];
    }
}
