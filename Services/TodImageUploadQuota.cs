namespace LinkshellManagerDiscordApp.Services;

// A per-user daily cap on ToD screenshot uploads.
//
// An upload is saved to disk before any ToD row points at it, so without a cap one account could
// keep uploading 5 MB images until the droplet's disk filled up. Real use is a handful a day, so
// 30 per user per UTC day never gets in a member's way.
//
// Kept in memory on purpose: the app runs as a single process, and a restart resetting the counts
// is harmless - this is a brake on runaway uploads, not exact accounting. Counting rows in the
// database would miss the abuse case, because an upload that is never attached to a ToD leaves
// no row behind.
public sealed class TodImageUploadQuota
{
    public const int DailyLimitPerUser = 30;

    private readonly Func<DateTime> _utcNow;
    private readonly object _gate = new();
    private readonly Dictionary<string, int> _countsToday = new(StringComparer.Ordinal);
    private DateTime _day;

    public TodImageUploadQuota()
        : this(() => DateTime.UtcNow)
    {
    }

    internal TodImageUploadQuota(Func<DateTime> utcNow)
    {
        _utcNow = utcNow;
        _day = utcNow().Date;
    }

    // Takes one upload from today's allowance. False = the user has hit the cap.
    public bool TryTake(string userId)
    {
        lock (_gate)
        {
            ResetIfNewDay();
            _countsToday.TryGetValue(userId, out var used);
            if (used >= DailyLimitPerUser)
            {
                return false;
            }

            _countsToday[userId] = used + 1;
            return true;
        }
    }

    // Gives an upload back when the file was refused (wrong type, too big), so a few
    // mistakes don't use up the day.
    public void Release(string userId)
    {
        lock (_gate)
        {
            ResetIfNewDay();
            if (_countsToday.TryGetValue(userId, out var used) && used > 0)
            {
                _countsToday[userId] = used - 1;
            }
        }
    }

    // A new UTC day starts everyone at zero, and drops yesterday's entries so the map never grows.
    private void ResetIfNewDay()
    {
        var today = _utcNow().Date;
        if (today != _day)
        {
            _day = today;
            _countsToday.Clear();
        }
    }
}
