using LinkshellManagerDiscordApp.Models;
using Microsoft.EntityFrameworkCore;

namespace LinkshellManagerDiscordApp.Services;

// Where an attendance event or capture lives, as shared query filters. The Activity's window-events
// endpoint and the addon's Misc Posts panel both list these rows, and a card that drops out of one
// list has to drop into the other everywhere or it becomes invisible.
//
// (This was AttendanceSectionsBuilder, which also built the old website's Event System sections.
// Those pages are gone; the app's own mappers live in ActivityDataController.WindowEvents.)
public static class AttendanceQueryFilters
{
    // The split is ENDED vs STILL LIVE -- deliberately not Open vs Closed. An HNM camp handed off
    // by HnmCampReviewHandoffService is Status=Open with CampEndedAtUtc set: a finished camp
    // waiting on review, not a live one. Keying on status alone filed every ended camp under
    // Current Field Activity, next to boards that were still being scanned.
    //
    // Live work: still being captured, so it belongs in Current Field Activity.
    public static IQueryable<WindowEvent> ApplyLiveFilter(IQueryable<WindowEvent> source)
        => source.Where(e => e.Status == WindowEventStatuses.Open && e.CampEndedAtUtc == null);

    // THE CAPTURES THE ADDON'S MISC POSTS PANEL IS STILL RESPONSIBLE FOR.
    //
    // That panel is a live to-do list, not an archive: what you have captured at the camp you are
    // standing at, and whether an officer has filed it yet. Once the camp ends, every capture on it
    // moves onto a review card and the app owns it from then on -- there is nothing the addon can
    // do with the row, and nothing it can say about it that the review card does not say better.
    //
    // Leaving them in was worse than useless. The list is capped at the most recent 50, so last
    // week's finished camps sat on top of tonight's captures and pushed the unfiled ones -- the
    // only rows that ARE somebody's work -- off the bottom.
    //
    // The addon had its own answer: End Camp moved a local cutoff and the rows hid. That cutoff
    // lives in addon memory only, so a /addon reload or a game restart brought every one of them
    // back. This is the same rule, decided where it survives.
    //
    // Live means the same thing it does in ApplyLiveFilter directly above, and it is spelled out
    // rather than reused because it is being asked of the snapshot's event, not of the event
    // itself -- the two must not drift.
    //
    // Three ways to still be live work:
    //   * on no event at all -- unfiled, which is the officer's to-do by definition;
    //   * on a Window Event still being captured;
    //   * on a live camp (the Event row), which End Event deletes -- and the FK sets LinkedEventId
    //     null when it does, so a null here is a camp that is gone rather than one still running.
    public static IQueryable<AttendanceSnapshot> ApplyLiveCaptureFilter(
        IQueryable<AttendanceSnapshot> source)
        => source.Where(s =>
            (s.WindowEvent == null
             || (s.WindowEvent.Status == WindowEventStatuses.Open && s.WindowEvent.CampEndedAtUtc == null))
            && (s.LinkedEvent == null || s.LinkedEvent.EndTime == null));

    // Ended, and nobody has posted the DKP yet -- the "Events Pending DKP Post" section.
    //
    // PostedToSheetAt is the gate, because it is the money: WindowEventDkpLedgerService sets it
    // when the ledger is written, and that is the moment the event stops being something an
    // officer owes work on. Until then it is deliberately kept OUT of Past Events
    // (ActivityDataController.EventHistory), so the past-event list means "settled" rather than
    // "over".
    //
    // Two ways in, because there are two ways to finish: a camp End Camp stamps CampEndedAtUtc,
    // and an officer closes a "/lsm now" event by hand.
    public static IQueryable<WindowEvent> ApplyPendingDkpPostFilter(IQueryable<WindowEvent> source)
        => source.Where(e => e.PostedToSheetAt == null
                             && (e.CampEndedAtUtc != null || e.Status == WindowEventStatuses.Closed));

    // The archive's search, as a composable filter over closed Window Events.
    //
    // Blank/null means "no filter" and the source comes back untouched.
    public static IQueryable<WindowEvent> ApplyClosedSearch(IQueryable<WindowEvent> source, string? query)
    {
        if (string.IsNullOrWhiteSpace(query)) return source;

        var pattern = $"%{query.Trim()}%";
        return source.Where(e =>
            (e.Name != null && EF.Functions.ILike(e.Name, pattern))
            || (e.CreatedByCharacterName != null && EF.Functions.ILike(e.CreatedByCharacterName, pattern))
            // Poster match spans EVERY snapshot, matching the in-memory filter this replaced.
            || e.Snapshots.Any(s =>
                s.CapturedByCharacterName != null && EF.Functions.ILike(s.CapturedByCharacterName, pattern))
            // Member match runs over the COMBINED roster, and BuildActivityCombinedMembers builds
            // that from ACTIVE snapshots only — hence the status filter, so a search here can't
            // match a name the card doesn't list.
            || e.Snapshots.Any(s =>
                s.SnapshotStatus == AttendanceSnapshotStatuses.Active
                && s.Entries.Any(entry => EF.Functions.ILike(entry.CharacterName, pattern))));
    }
}
