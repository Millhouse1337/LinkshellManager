using System.Threading.Channels;

namespace LinkshellManagerDiscordApp.Services;

// Fire-and-forget queue of EventHistory ids to post an "event ended" summary for.
// Enqueued from ApplicationDbContext on a successful commit whenever an
// EventHistory row is added (which both the web and Activity end-event paths do),
// so the summary needs no per-controller wiring. Drained by
// DiscordEventEndedBackgroundService. Singleton, single-reader; mirrors
// DiscordEventChannelQueue.
public sealed class DiscordEventEndedQueue
{
    private readonly Channel<int> _channel =
        Channel.CreateUnbounded<int>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
        });

    public void Enqueue(int eventHistoryId)
    {
        if (eventHistoryId <= 0)
        {
            return;
        }
        _channel.Writer.TryWrite(eventHistoryId);
    }

    public ChannelReader<int> Reader => _channel.Reader;
}
