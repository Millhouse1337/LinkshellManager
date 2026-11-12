using System.Linq.Expressions;
using LinkshellManagerDiscordApp.Data;
using LinkshellManagerDiscordApp.Models;
using Microsoft.EntityFrameworkCore;

namespace LinkshellManagerDiscordApp.Services;

// "Is this event running right now?" -- one answer for every loot path.
//
// An Events row is NOT the same thing as a running event. End Camp keeps the HNM board's row and
// recycles it for the next pop (HnmCampReviewHandoffService / HnmCampPopService stamp WdFinalizedAt
// and clear CommencementStartTime), and a queued board has never started at all. Loot filed on
// such a row as if it were live was recorded uncharged and waited for the NEXT camp's End Camp --
// days on a recurring board, and forever if the board was cancelled or deleted.
//
// Running = started (CommencementStartTime) and not yet ended by End Camp (WdFinalizedAt).
// EndTime is deliberately NOT part of the test: on a timed event it is the PLANNED end the officer
// typed in the event form (EventsLifecycle create/edit), set from the moment the event is made,
// while the real close deletes the row (EventEndService). Testing it would call every timed event
// with an end time "not running". HNM boards keep it null, so it says nothing for them either.
public static class EventRunState
{
    // The same rule as an expression, for queries the database runs.
    public static readonly Expression<Func<Event, bool>> Running =
        ev => ev.CommencementStartTime != null && ev.WdFinalizedAt == null;

    private static readonly Func<Event, bool> RunningCompiled = Running.Compile();

    public static bool IsRunning(Event ev) => RunningCompiled(ev);

    // The Past Event the board's most recent camp was archived as, or null when it has none (it
    // never ran, or every camp ended with nobody on it).
    //
    // Found through the camp's review row: End Camp stages both together and links them
    // (WindowEvent.CampEventHistoryId), so this is the camp's own answer rather than a match on
    // names and dates. Newest first, because a recycled board has ended many times.
    public static Task<int?> NewestCampArchiveIdAsync(
        ApplicationDbContext db, int eventId, CancellationToken cancellationToken)
        => db.WindowEvents
            .AsNoTracking()
            .Where(w => w.SourceEventId == eventId && w.CampEventHistoryId != null)
            .OrderByDescending(w => w.CampEndedAtUtc)
            .ThenByDescending(w => w.Id)
            .Select(w => w.CampEventHistoryId)
            .FirstOrDefaultAsync(cancellationToken);
}
