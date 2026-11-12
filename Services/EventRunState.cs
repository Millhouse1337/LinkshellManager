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

    // Call inside the loot post's transaction, BEFORE reading whether the event is running, and
    // keep the transaction open until the loot is saved.
    //
    // Loot on a running event is saved uncharged; the event's own end charges it (End Camp's
    // SettleCampLootAsync, or the close). The two could cross: End Camp read the board's loot, a
    // loot post read "running" and saved, then End Camp parked the board -- leaving the new row on
    // the parked board, uncharged, for the NEXT camp (or for ever).
    //
    // A no-op UPDATE of the event row rather than a SELECT ... FOR UPDATE, because End Camp holds
    // no transaction (HnmCampPopService.PopAsync explains why), so it would never wait on a lock.
    // What it does have is the Event's xmin check on its save (Event.Version). The UPDATE takes the
    // same row lock FOR UPDATE would AND gives the row a new xmin, so:
    //   * End Camp saves first: this waits for it, then reads the board as ended and the loot goes
    //     to the camp's archive, charged (ManualLootService).
    //   * this commits first: End Camp's save (or the close's delete) finds the row changed since
    //     it read it, saves nothing, and is retried -- reading the new loot this time.
    // In-memory tests have no transactions or row versions and run one request at a time.
    // Scoped to the caller's linkshell: the id arrives from a client, and another shell's row is
    // left alone.
    public static Task HoldForLootAsync(
        ApplicationDbContext db, int linkshellId, int eventId, CancellationToken cancellationToken)
        => db.Database.IsRelational()
            ? db.Database.ExecuteSqlInterpolatedAsync(
                $"""
                 UPDATE "Events" SET "LinkshellId" = "LinkshellId"
                 WHERE "Id" = {eventId} AND "LinkshellId" = {linkshellId}
                 """,
                cancellationToken)
            : Task.CompletedTask;

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
