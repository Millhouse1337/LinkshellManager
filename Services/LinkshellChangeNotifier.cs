using System.Collections.Concurrent;

namespace LinkshellManagerDiscordApp.Services;

/// <summary>
/// In-memory, per-linkshell change bus that lets the Discord Activity long-poll
/// for "something changed in my linkshell" instead of blind-polling every few
/// seconds. Registered as a singleton.
///
/// The backend already knows precisely what changed on each save (the
/// <c>ApplicationDbContext</c> change hooks). After a successful commit we call
/// <see cref="Notify"/> for each affected linkshell; that bumps a monotonic
/// per-linkshell version and wakes any long-poll request parked on that
/// linkshell. A client holds <c>GET /api/activity/changes</c> with its
/// last-seen version; the call returns as soon as the version advances (with the
/// coarse areas that changed) or after a short hold (empty areas → the client
/// immediately re-polls).
///
/// Scope is a single server instance (state lives in memory). That matches the
/// single-droplet deployment; if we ever scale out this becomes Redis pub/sub
/// or similar. The change-feed is an optimization layered on top of the existing
/// polling timer — if it drops, the timer still guarantees eventual freshness.
/// </summary>
public sealed class LinkshellChangeNotifier
{
    /// <summary>
    /// Coarse change areas. Plain string constants (not an enum) so the JSON
    /// contract with the Angular client is a simple string array. The client
    /// currently treats any non-empty area list as "refresh the active tab", so
    /// these are mostly informational / future-proofing.
    /// </summary>
    public static class Areas
    {
        public const string Events = "events";
        public const string Auctions = "auctions";
        public const string Loot = "loot";
        public const string Tods = "tods";
        public const string Dkp = "dkp";
        public const string Roster = "roster";
        public const string Windows = "windows";
        public const string Parties = "parties";
    }

    private static readonly string[] AllAreas =
    {
        Areas.Events, Areas.Auctions, Areas.Loot, Areas.Tods,
        Areas.Dkp, Areas.Roster, Areas.Windows, Areas.Parties
    };

    // Keep enough recent (version, area) entries that a client polling every
    // ~25s never falls behind the ring under normal load; if it ever does we
    // fall back to "all areas" (full refresh) rather than missing a change.
    private const int MaxRecentPerLinkshell = 128;

    private sealed class LinkshellState
    {
        public long Version;
        public readonly LinkedList<(long Version, string Area)> Recent = new();
        public readonly List<TaskCompletionSource<bool>> Waiters = new();
    }

    private readonly ConcurrentDictionary<int, LinkshellState> _states = new();

    /// <summary>
    /// Records a change for a linkshell and wakes every request parked on it.
    /// Safe to call from anywhere (it's invoked from the DbContext save hooks).
    /// </summary>
    public void Notify(int linkshellId, string area)
    {
        if (linkshellId <= 0 || string.IsNullOrWhiteSpace(area))
        {
            return;
        }

        var state = _states.GetOrAdd(linkshellId, _ => new LinkshellState());

        List<TaskCompletionSource<bool>> toWake;
        lock (state)
        {
            state.Version++;
            state.Recent.AddLast((state.Version, area));
            while (state.Recent.Count > MaxRecentPerLinkshell)
            {
                state.Recent.RemoveFirst();
            }

            if (state.Waiters.Count == 0)
            {
                return;
            }

            toWake = new List<TaskCompletionSource<bool>>(state.Waiters);
            state.Waiters.Clear();
        }

        foreach (var waiter in toWake)
        {
            waiter.TrySetResult(true);
        }
    }

    /// <summary>
    /// The current version for a linkshell (0 if nothing has changed yet). The
    /// client uses this as its starting <c>since</c> on first connect so it only
    /// receives <em>future</em> changes without an immediate refetch.
    /// </summary>
    public long CurrentVersion(int linkshellId)
    {
        if (!_states.TryGetValue(linkshellId, out var state))
        {
            return 0;
        }
        lock (state)
        {
            return state.Version;
        }
    }

    /// <summary>
    /// Returns immediately if the linkshell's version differs from
    /// <paramref name="sinceVersion"/> (changed, or reset to a lower value after
    /// a server restart); otherwise parks until the next <see cref="Notify"/>,
    /// the <paramref name="maxHold"/> timeout, or request cancellation.
    /// </summary>
    public async Task<ChangeResult> WaitForChangeAsync(
        int linkshellId, long sinceVersion, TimeSpan maxHold, CancellationToken cancellationToken)
    {
        var state = _states.GetOrAdd(linkshellId, _ => new LinkshellState());

        TaskCompletionSource<bool> tcs;
        lock (state)
        {
            if (state.Version != sinceVersion)
            {
                return BuildResultLocked(state, sinceVersion);
            }
            // Park BEFORE releasing the lock so a Notify racing in after we
            // unlock still finds (and completes) this waiter — no lost wakeups.
            tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            state.Waiters.Add(tcs);
        }

        using var timeoutCts = new CancellationTokenSource(maxHold);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);
        await using var registration = linked.Token.Register(
            static s => ((TaskCompletionSource<bool>)s!).TrySetResult(false), tcs);

        try
        {
            await tcs.Task.ConfigureAwait(false);
        }
        finally
        {
            lock (state)
            {
                state.Waiters.Remove(tcs);
            }
        }

        // Client disconnected mid-hold → bail without trying to build a response.
        cancellationToken.ThrowIfCancellationRequested();

        lock (state)
        {
            return BuildResultLocked(state, sinceVersion);
        }
    }

    private static ChangeResult BuildResultLocked(LinkshellState state, long sinceVersion)
    {
        var areas = state.Recent
            .Where(entry => entry.Version > sinceVersion)
            .Select(entry => entry.Area)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        // Version advanced but the specific entries were evicted (client far
        // behind) → can't say precisely what changed, so signal everything and
        // let the client do a full refresh rather than silently miss updates.
        if (areas.Length == 0 && state.Version > sinceVersion)
        {
            areas = AllAreas;
        }

        return new ChangeResult(state.Version, areas);
    }
}

public sealed record ChangeResult(long Version, IReadOnlyList<string> Areas);
