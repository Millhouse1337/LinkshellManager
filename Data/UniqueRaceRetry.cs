using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace LinkshellManagerDiscordApp.Data;

// Re-runs a post that lost a race to another post landing at the same moment.
//
// Some posts ADD TO the one record a camp keeps -- a window's credits, the camp's Claim Shield
// capture. Each reads what is there, then inserts what is missing. Two posts landing together both
// read before either has saved, so both insert the same thing, and a unique index rejects the later
// one. That used to fail the whole post, including whatever only that officer had seen. Re-running
// it reads again, finds what the other post saved, and adds only what is left -- exactly as if it
// had arrived a second later.
//
// The tracker is cleared before each re-run so nothing from the failed save is sent again. Anything
// the post saved before failing must therefore be safe to repeat.
//
// More than one retry: every officer's addon reads the same chat line, so three or four posts
// within a few milliseconds is ordinary for Claim Shield. Each round lets at least one through.
internal static class UniqueRaceRetry
{
    internal const int MaxRetries = 3;

    // `tables`: the tables whose unique rejections mean "someone else got there first". A rejection
    // anywhere else is a real error and is rethrown untouched.
    internal static async Task<T> RunAsync<T>(
        ApplicationDbContext dbContext, Func<Task<T>> post, params string[] tables)
    {
        for (var retries = 0; ; retries++)
        {
            try
            {
                return await post();
            }
            catch (DbUpdateException ex) when (retries < MaxRetries && IsUniqueRejectionOn(ex, tables))
            {
                dbContext.ChangeTracker.Clear();
            }
        }
    }

    internal static bool IsUniqueRejectionOn(DbUpdateException ex, IReadOnlyCollection<string> tables) =>
        ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation } rejection
        && rejection.TableName is { } table
        && tables.Contains(table);
}
