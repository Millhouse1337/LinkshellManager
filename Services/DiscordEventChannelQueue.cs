using System.Threading.Channels;

namespace LinkshellManagerDiscordApp.Services;

// Fire-and-forget queue of event ids to announce to a linkshell's Discord
// channel. Enqueued from ApplicationDbContext on a successful commit whenever a
// new Event row is added (any path — web, Activity, addon, HNM auto), so posting
// needs no per-controller wiring. Drained by DiscordEventChannelBackgroundService.
// Singleton, single-reader; mirrors DiscordAuctionChannelQueue.
public sealed class DiscordEventChannelQueue
{
    private readonly Channel<int> _channel =
        Channel.CreateUnbounded<int>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
        });

    public void Enqueue(int eventId)
    {
        if (eventId <= 0)
        {
            return;
        }
        // Unbounded: TryWrite always succeeds. Non-blocking so SaveChanges is
        // never delayed by Discord delivery.
        _channel.Writer.TryWrite(eventId);
    }

    public ChannelReader<int> Reader => _channel.Reader;
}
