using System.Threading.Channels;

namespace LinkshellManagerDiscordApp.Services;

// Fire-and-forget queue of linkshell ids whose live DKP sheet Discord post needs
// refreshing. Enqueued from ApplicationDbContext whenever member DKP changes (any
// DkpLedgerEntry add/modify/delete or a LinkshellDkp modification) and from the
// channel-picker save. Drained + debounced by DkpSheetPostBackgroundService, which
// edits the existing post in place. Singleton, single-reader; mirrors
// DiscordTodBoardQueue.
public sealed class DkpSheetPostQueue
{
    // Bounded with DropOldest: the 5s read-side debounce keeps this near-empty in normal
    // operation, but a pathological write-faster-than-drain DKP loop must not grow it without
    // bound. Enqueued ids are idempotent (the publisher reads current state), so dropping an
    // older queued id is harmless — the latest enqueue re-adds the linkshell.
    private readonly Channel<int> _channel = Channel.CreateBounded<int>(new BoundedChannelOptions(1024)
    {
        FullMode = BoundedChannelFullMode.DropOldest,
        SingleReader = true,
        SingleWriter = false,
    });

    public void Enqueue(int linkshellId)
    {
        if (linkshellId <= 0)
        {
            return;
        }
        _channel.Writer.TryWrite(linkshellId);
    }

    public ChannelReader<int> Reader => _channel.Reader;
}
