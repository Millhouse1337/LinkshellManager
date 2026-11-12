using LinkshellManagerDiscordApp.Data;
using Microsoft.EntityFrameworkCore;

namespace LinkshellManagerDiscordApp.Services;

// One pass of a background job over many rows (standing boards, due events, live camps), where each
// row is handled on its own: its own scope (so its own DbContext), its own save, its own try/catch.
//
// Why: these jobs used to load every row into ONE context and either save them all together or keep
// going after a failure on the same context. One bad row then hurt everyone else:
//   * a row that threw before the shared save stopped the save, so no camp in ANY linkshell went live
//     (or advanced its window) until someone read the logs and fixed that one row;
//   * a row whose save failed left its half-made changes in the shared tracker, and the NEXT row's
//     save wrote them anyway -- a change that had just failed got saved by somebody else's save.
// With a fresh context per row, a failure throws away only that row's work, is logged with its id,
// and the loop carries on with the next one.
//
// A lost race on the row (an officer saved it between our read and our save, so the xmin check on
// Event.Version turned the save away) re-runs just that row with fresh reads, up to
// ConcurrencyRetry.MaxRetries times; still losing, it is left for the next tick.
//
// `work` must start from scratch (load its row by id, check it still applies, change it, save at most
// once), because a re-run calls it again on a brand new context.
internal static class BackgroundItemLoop
{
    internal static async Task RunEachAsync(
        IServiceScopeFactory scopeFactory,
        IReadOnlyList<int> ids,
        Func<ApplicationDbContext, int, Task> work,
        ILogger logger,
        string itemKind,
        CancellationToken cancellationToken)
    {
        foreach (var id in ids)
        {
            try
            {
                await ConcurrencyRetry.RunFreshTickAsync(async () =>
                {
                    using var scope = scopeFactory.CreateScope();
                    var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                    await work(db, id);
                });
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw; // the app is shutting down: stop the whole pass, not just this row
            }
            catch (DbUpdateConcurrencyException ex)
            {
                logger.LogInformation(ex,
                    "{ItemKind} {ItemId} kept losing to concurrent edits; retrying it next tick.",
                    itemKind, id);
            }
            catch (Exception ex)
            {
                logger.LogError(ex,
                    "{ItemKind} {ItemId} failed; the rest of this tick carried on without it.",
                    itemKind, id);
            }
        }
    }

    // The ids a pass will walk, read on a short-lived context of their own so nothing it loads is
    // still tracked when the per-row work starts.
    internal static async Task<List<int>> ReadIdsAsync(
        IServiceScopeFactory scopeFactory,
        Func<ApplicationDbContext, Task<List<int>>> query)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        return await query(db);
    }
}
