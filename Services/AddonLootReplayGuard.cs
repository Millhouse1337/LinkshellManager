namespace LinkshellManagerDiscordApp.Services;

// Stops a retried addon loot post from charging the winner twice.
//
// The addon's Post Loot gives up waiting after a few seconds. If the server had already saved the
// row by then, the officer saw "No response from server", pressed Post again, and the winner paid
// twice. The addon now sends one clientRequestId per drop and keeps it for every retry of that
// drop. The first post that SUCCEEDS is remembered here under (token, id); a later post with the
// same id gets that first answer back and writes nothing.
//
// Only successes are remembered: a refused post ("not enough DKP") can be fixed and sent again
// with the same id. Posts with no id (older addons) are never deduplicated, exactly as before.
//
// Kept in memory on purpose, like TodImageUploadQuota: the app runs as a single process, and a
// retry comes seconds or minutes after the first try, not across a restart. 24 hours is far
// longer than any retry.
public sealed class AddonLootReplayGuard
{
    public const int MaxKeyLength = 100;
    public static readonly TimeSpan Remember = TimeSpan.FromHours(24);

    // Two posts with the same id can arrive together: the first still saving when the retry
    // lands. A lock per id makes the second wait and then find the first's answer. A fixed set of
    // locks picked by hash keeps memory bounded; two different ids that share a lock only wait
    // for each other, which costs nothing at loot-post rates.
    private readonly SemaphoreSlim[] _locks = Enumerable.Range(0, 64).Select(_ => new SemaphoreSlim(1, 1)).ToArray();
    private readonly object _gate = new();
    private readonly Dictionary<string, (object? Response, DateTime At)> _done = new(StringComparer.Ordinal);
    private readonly Func<DateTime> _utcNow;

    public AddonLootReplayGuard()
        : this(() => DateTime.UtcNow)
    {
    }

    internal AddonLootReplayGuard(Func<DateTime> utcNow)
    {
        _utcNow = utcNow;
    }

    // The id as it will be used, or null when there is none to use (missing, blank, too long).
    public static string? NormalizeKey(string? clientRequestId)
    {
        var key = clientRequestId?.Trim();
        if (string.IsNullOrEmpty(key) || key.Length > MaxKeyLength)
        {
            return null;
        }
        return key;
    }

    // Runs `post` once per (tokenId, clientRequestId). `post` returns its answer and whether it
    // succeeded; only a success is remembered. `Replayed` is true when the answer is an earlier
    // post's, given back without running `post` (nothing was written this time).
    //
    // Keyed by the addon TOKEN, not just the linkshell, so two officers can never collide on an id.
    public async Task<(T Response, bool Succeeded, bool Replayed)> RunOnceAsync<T>(
        int tokenId,
        string? clientRequestId,
        Func<Task<(T Response, bool Succeeded)>> post,
        CancellationToken cancellationToken)
    {
        var key = NormalizeKey(clientRequestId);
        if (key is null)
        {
            var (response, succeeded) = await post();
            return (response, succeeded, false);
        }

        var slot = $"{tokenId}:{key}";
        var gate = _locks[(slot.GetHashCode() & int.MaxValue) % _locks.Length];
        await gate.WaitAsync(cancellationToken);
        try
        {
            if (TryGet(slot, out var earlier) && earlier is T same)
            {
                return (same, true, true);
            }

            var (response, succeeded) = await post();
            if (succeeded)
            {
                Store(slot, response);
            }
            return (response, succeeded, false);
        }
        finally
        {
            gate.Release();
        }
    }

    private bool TryGet(string slot, out object? response)
    {
        lock (_gate)
        {
            if (_done.TryGetValue(slot, out var hit) && _utcNow() - hit.At < Remember)
            {
                response = hit.Response;
                return true;
            }
            response = null;
            return false;
        }
    }

    private void Store(string slot, object? response)
    {
        lock (_gate)
        {
            // Old entries are swept on write, so the table holds about a day of loot posts.
            var now = _utcNow();
            foreach (var stale in _done.Where(pair => now - pair.Value.At >= Remember).Select(pair => pair.Key).ToList())
            {
                _done.Remove(stale);
            }
            _done[slot] = (response, now);
        }
    }
}
