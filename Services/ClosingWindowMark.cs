using LinkshellManagerDiscordApp.Data;
using LinkshellManagerDiscordApp.Models;
using Microsoft.EntityFrameworkCore;

namespace LinkshellManagerDiscordApp.Services;

// Moves a live camp's closing-window tick. Shared by the Closing window checkbox
// (AddonApiController.SetClosingWindowAsync) and the tick a 2-post camp's Close post sets
// (AddonApiController.FindOrCreateAttendanceWindowAsync), so both follow one rule: at most one
// closing window per camp.
//
// Two saves, in one transaction. A later migration adds that rule as a unique index, and Postgres
// checks a unique index row by row: setting the new window and clearing the old one in a single
// save would, for a moment, have two closing windows and be refused. So the old tick is cleared
// and saved first, then the new one is set and saved. In-memory tests have no transactions.
internal static class ClosingWindowMark
{
    // Makes `closingWindowId` the camp's only closing window, or clears the tick from every window
    // when it is null. Returns all the camp's windows (tracked), already saved.
    internal static async Task<List<EventAttendanceWindow>> MoveAsync(
        ApplicationDbContext db, int eventId, int? closingWindowId, CancellationToken cancellationToken)
    {
        await using var transaction = db.Database.IsRelational() && db.Database.CurrentTransaction is null
            ? await db.Database.BeginTransactionAsync(cancellationToken)
            : null;

        var windows = await db.EventAttendanceWindows
            .Where(w => w.EventId == eventId)
            .ToListAsync(cancellationToken);

        // Save 1: let go of the old tick.
        var released = false;
        foreach (var window in windows.Where(w => w.IsClosingWindow && w.Id != closingWindowId))
        {
            window.IsClosingWindow = false;
            released = true;
        }
        if (released)
        {
            await db.SaveChangesAsync(cancellationToken);
        }

        // Save 2: set the new one. Always saved, so anything else the caller left pending goes out
        // here, as it did when this was one save.
        var closing = windows.FirstOrDefault(w => w.Id == closingWindowId);
        if (closing is not null)
        {
            closing.IsClosingWindow = true;
        }
        await db.SaveChangesAsync(cancellationToken);

        if (transaction is not null)
        {
            await transaction.CommitAsync(cancellationToken);
        }
        return windows;
    }
}
