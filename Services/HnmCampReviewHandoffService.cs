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
    private readonly ILogger<HnmCampReviewHandoffService> _logger;

    public HnmCampReviewHandoffService(
        ApplicationDbContext db,
        WdCampFinalizer wdFinalizer,
        HnmStandardCampFinalizer standardFinalizer,
        ILogger<HnmCampReviewHandoffService> logger)
    {
        _db = db;
        _wdFinalizer = wdFinalizer;
        _standardFinalizer = standardFinalizer;
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
        var members = isWd
            ? await _wdFinalizer.BuildRosterAsync(ev, linkshell, popWindow, claimed, killed, cancellationToken)
            : await _standardFinalizer.BuildRosterAsync(ev, linkshell, popWindow, claimed, killed, cancellationToken);

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
            // Event.StartTime to the next predicted repop and clears CommencementStartTime, and
            // EventHistory isn't written until Post.
            CampStartedAtUtc = ev.CommencementStartTime ?? ev.StartTime,
            CampEndedAtUtc = nowUtc,
            CampEventType = ev.EventType,
            CampEventLocation = ev.EventLocation,
        };
        _db.WindowEvents.Add(windowEvent);

        var snapshot = new AttendanceSnapshot
        {
            LinkshellId = ev.LinkshellId,
            WindowEvent = windowEvent,
            LinkedEventId = ev.Id,
            CapturedAtUtc = nowUtc,
            CreatedAtUtc = nowUtc,
            SnapshotStatus = AttendanceSnapshotStatuses.Active,
            Name = isWd
                ? $"Check In roster · window {popWindow}"
                : $"Camp roster · window {popWindow}",
            EntryCount = byCharacterName.Count,
        };
        _db.AttendanceSnapshots.Add(snapshot);

        foreach (var member in byCharacterName.Values)
        {
            snapshot.Entries.Add(new AttendanceSnapshotEntry
            {
                Snapshot = snapshot,
                CharacterName = member.CharacterName,
                // Carries the account through to post time so credit doesn't depend on the
                // character name matching one of the four names the name-resolver indexes.
                AppUserId = member.AppUserId,
                MainJob = TruncateJob(member.JobName),
                SubJob = TruncateJob(member.SubJobName),
            });

            windowEvent.MemberDkpOverrides.Add(new WindowEventMemberDkp
            {
                WindowEvent = windowEvent,
                CharacterName = member.CharacterName,
                DkpAmount = member.Dkp,
            });
        }

        _logger.LogInformation(
            "HNM camp handed off for review: event {EventId} ({Monster}, mode {Mode}) staged "
            + "{Count} member(s) totalling {Total} DKP — pending an officer's Post.",
            ev.Id, monster, isWd ? "Manual Check In" : "Standard",
            byCharacterName.Count, byCharacterName.Values.Sum(m => m.Dkp));

        return windowEvent;
    }

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

        await StageHandoffAsync(ev, popWindow, ev.WdClaimed, ev.WdKilled, cancellationToken);

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
        // The board is RECYCLED, not deleted, so the unique (EventId, SequenceNumber) index would
        // otherwise hand the next camp's window-1 scan this camp's stale row.
        var scannedWindows = await _db.EventAttendanceWindows
            .Where(w => w.EventId == eventId)
            .ToListAsync(cancellationToken);
        _db.EventAttendanceWindows.RemoveRange(scannedWindows);

        await _db.SaveChangesAsync(cancellationToken);
        return true;
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
