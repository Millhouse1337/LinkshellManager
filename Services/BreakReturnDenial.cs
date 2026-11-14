using LinkshellManagerDiscordApp.Data;
using LinkshellManagerDiscordApp.Models;
using Microsoft.EntityFrameworkCore;

namespace LinkshellManagerDiscordApp.Services;

// What "Deny" on a member's own "I'm back" does. Used by the Activity deny endpoint.
//
// A self-return restarts the member's clock straight away and only ASKS an officer to confirm it.
// Deny used to just stamp the request as denied, so the clock kept running and the member was paid
// for the time anyway unless the officer also pressed Send to Break Room. Deny now takes that time
// back: the member counts as never having come back.
//
//   - Still in the segment the return started (no break since): back on break as of the return
//     time. Duration already holds everything banked before the break, so it stays as it is.
//   - Took another break since: the time between the return and that break was banked into
//     Duration when the break started, so it comes back out. Their current state is left alone.
//   - Windowed (HNM) events have no clock to undo, and re-parking a member there hides them from
//     the addon roster with no way back (see EventBreakPolicy), so Deny only marks the request.
public static class BreakReturnDenial
{
    public enum Outcome { Denied, ReturnNotFound, AlreadyResolved, ParticipantNotFound }

    // The whole Deny (the caller has checked permissions): find the request,
    // check it is still pending, take the time back, save.
    //
    // The request's row is locked first. Two Denies of one return (a double click, or one officer in
    // the app and one in the addon) used to both pass the "still pending" check; when the member had
    // taken another break since, each took the time off Duration, so it came off twice. With the
    // lock the second waits for the first to commit, then reads the request as already resolved.
    public static async Task<Outcome> DenyAsync(
        ApplicationDbContext db,
        Event eventEntity,
        int ledgerEntryId,
        CancellationToken cancellationToken)
    {
        // The in-memory test provider has no transactions or row locks.
        await using var transaction = db.Database.IsRelational()
            ? await db.Database.BeginTransactionAsync(cancellationToken)
            : null;
        if (transaction is not null)
        {
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"""SELECT 1 FROM "AppUserEventStatusLedgers" WHERE "Id" = {ledgerEntryId} FOR UPDATE""",
                cancellationToken);
        }

        var deniedReturn = await db.AppUserEventStatusLedgers
            .FirstOrDefaultAsync(
                item => item.Id == ledgerEntryId &&
                        item.EventId == eventEntity.Id &&
                        item.ActionType == "BreakReturn",
                cancellationToken);
        if (deniedReturn is null)
        {
            return Outcome.ReturnNotFound;
        }
        if (!deniedReturn.RequiresVerification || deniedReturn.VerifiedAt.HasValue || deniedReturn.DeniedAt.HasValue)
        {
            return Outcome.AlreadyResolved;
        }

        var participation = await db.AppUserEvents
            .FirstOrDefaultAsync(item => item.Id == deniedReturn.AppUserEventId, cancellationToken);
        if (participation is null)
        {
            return Outcome.ParticipantNotFound;
        }

        var participantLedger = await db.AppUserEventStatusLedgers
            .Where(item => item.AppUserEventId == participation.Id)
            .ToListAsync(cancellationToken);
        Apply(eventEntity, participation, deniedReturn, participantLedger, DateTime.UtcNow);

        await db.SaveChangesAsync(cancellationToken);
        if (transaction is not null)
        {
            await transaction.CommitAsync(cancellationToken);
        }
        return Outcome.Denied;
    }

    // `participantLedger` is every status ledger row of this participation (any order).
    // The caller has already checked the request is still pending, and saves afterwards.
    public static void Apply(
        Event eventEntity,
        AppUserEvent participation,
        AppUserEventStatusLedger deniedReturn,
        IEnumerable<AppUserEventStatusLedger> participantLedger,
        DateTime nowUtc)
    {
        deniedReturn.DeniedAt = nowUtc;
        deniedReturn.RequiresVerification = false;

        if (EventBreakPolicy.IsWindowedAttendance(eventEntity))
        {
            return;
        }

        var returnedAt = deniedReturn.OccurredAt;
        var nextBreak = participantLedger
            .Where(entry =>
                entry.Id != deniedReturn.Id &&
                entry.ActionType == "BreakStart" &&
                entry.OccurredAt >= returnedAt)
            .OrderBy(entry => entry.OccurredAt)
            .ThenBy(entry => entry.Id)
            .FirstOrDefault();

        if (nextBreak is not null)
        {
            var bankedHours = (nextBreak.OccurredAt - returnedAt).TotalHours;
            participation.Duration = Math.Max(0, (participation.Duration ?? 0) - bankedHours);
            return;
        }

        // On break with no BreakStart row after the return: an old or hand-edited state we can't
        // read the time from, so don't guess — the request is still marked denied.
        if (participation.IsOnBreak == true)
        {
            return;
        }

        participation.IsOnBreak = true;
        participation.ResumeTime = null;
    }
}
