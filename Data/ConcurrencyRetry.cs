using Microsoft.EntityFrameworkCore;

namespace LinkshellManagerDiscordApp.Data;

// Re-runs an action that lost a race on a member's balance or an event row.
//
// AppUserLinkshell and Event carry Postgres's xmin as a concurrency token (their Version
// property). Every save of one of those rows checks that nobody else saved it since we read it.
// Before, two DKP changes to one member at the same moment both read Bob at 100; one saved 105, the
// other 97, and one change was lost while both ledger rows stayed (balance and history disagreed
// forever). Now the later save fails with DbUpdateConcurrencyException and NOTHING from it is
// saved. Re-running the action reads Bob again (now 105), applies its own change on top (102), and
// saves -- exactly as if it had arrived a moment later.
//
// The action must start from scratch: load what it needs by id, change it, save. Before each re-run
// the tracker is cleared (so the stale copies are gone) and the per-request memos in
// DkpLedgerWriter / TreasuryJournalWriter are reset (ApplicationDbContext.ResetForRetry).
//
// It only re-runs when that is safe, and otherwise just runs the action once and lets the failure
// through (ApiExceptionHandler turns it into a 409 "please retry"):
//   * not inside another ConcurrencyRetry -- the outer one owns the re-run, and clearing the
//     tracker here would silently drop entities the outer action is still holding;
//   * not inside a transaction someone else opened -- their earlier saves would not be undone;
//   * nothing was staged before the action started -- clearing would throw that work away;
//   * no save of this attempt has already committed -- re-running would repeat it.
internal static class ConcurrencyRetry
{
    internal const int MaxRetries = 3;

    // What an officer sees when every retry lost (or a re-run was not safe).
    internal const string ConflictMessage = "Someone changed this at the same time - please retry.";

    internal static async Task<T> RunAsync<T>(ApplicationDbContext dbContext, Func<Task<T>> action)
    {
        if (dbContext.ConcurrencyRetryDepth > 0
            || dbContext.Database.CurrentTransaction is not null
            || dbContext.ChangeTracker.HasChanges())
        {
            return await action();
        }

        dbContext.ConcurrencyRetryDepth++;
        try
        {
            for (var retries = 0; ; retries++)
            {
                var committedBefore = dbContext.CommittedSaveCount;
                try
                {
                    return await action();
                }
                catch (DbUpdateConcurrencyException) when (
                    retries < MaxRetries && dbContext.CommittedSaveCount == committedBefore)
                {
                    dbContext.ResetForRetry();
                }
            }
        }
        finally
        {
            dbContext.ConcurrencyRetryDepth--;
        }
    }

    // For a background tick that opens its own scope (so its own DbContext) on every call: a lost
    // race just runs the tick again, which reads everything afresh. Still losing after the
    // retries rethrows, and the service's loop leaves the work for its next tick.
    internal static async Task RunFreshTickAsync(Func<Task> tick)
    {
        for (var retries = 0; ; retries++)
        {
            try
            {
                await tick();
                return;
            }
            catch (DbUpdateConcurrencyException) when (retries < MaxRetries)
            {
            }
        }
    }

    // A save of an Event that writes only columns nobody else writes -- the Discord message ids the
    // board publisher keeps -- after a slow Discord call between its read and its save. Someone
    // editing the event meanwhile is ordinary, and re-running the publisher would post the board
    // again. So on a conflict each event is read back as it is now and only the columns this save
    // changed are put on top: what an UPDATE did before the xmin check existed, column by column.
    // Anything other than a modified Event in the failed save is rethrown.
    internal static async Task SaveOwnEventColumnsAsync(
        ApplicationDbContext dbContext, CancellationToken cancellationToken)
    {
        for (var retries = 0; ; retries++)
        {
            try
            {
                await dbContext.SaveChangesAsync(cancellationToken);
                return;
            }
            catch (DbUpdateConcurrencyException ex) when (
                retries < MaxRetries
                && ex.Entries.All(entry => entry.Entity is Models.Event && entry.State == EntityState.Modified))
            {
                foreach (var entry in ex.Entries)
                {
                    var ours = entry.Properties
                        .Where(property => property.IsModified)
                        .Select(property => (property.Metadata.Name, property.CurrentValue))
                        .ToList();
                    await entry.ReloadAsync(cancellationToken);
                    if (entry.State == EntityState.Detached)
                    {
                        continue; // deleted meanwhile: nothing left to write to
                    }
                    foreach (var (name, value) in ours)
                    {
                        entry.Property(name).CurrentValue = value;
                    }
                }
            }
        }
    }

    // RunAsync for an action that saves more than once: its saves run in one transaction, so a
    // conflict on the last one also undoes the first ones and the re-run starts clean. In-memory
    // tests have no transactions; there the action just runs.
    internal static Task<T> RunInTransactionAsync<T>(
        ApplicationDbContext dbContext, Func<Task<T>> action, CancellationToken cancellationToken) =>
        RunAsync(dbContext, async () =>
        {
            if (!dbContext.Database.IsRelational() || dbContext.Database.CurrentTransaction is not null)
            {
                return await action();
            }

            await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
            var result = await action();
            await transaction.CommitAsync(cancellationToken);
            return result;
        });
}
