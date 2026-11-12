using System.Threading.Channels;

namespace LinkshellManagerDiscordApp.Services;

// One awarded event-loot row to announce. Only EventLootDetail rows are posted:
// a ToD carries no loot, so nothing creates TodLootDetail rows any more.
public readonly record struct DiscordLootJob(int EventLootDetailId);

// Fire-and-forget queue of awarded-loot rows to announce to a linkshell's Loot
// Discord channel. Enqueued from ApplicationDbContext on a successful commit
// whenever a new EventLootDetail with a winner is added (any path — web,
// Activity, addon, event loot). Drained by DiscordLootChannelBackgroundService.
// Singleton, single-reader; mirrors DiscordAuctionChannelQueue.
public sealed class DiscordLootChannelQueue
{
    private readonly Channel<DiscordLootJob> _channel =
        Channel.CreateUnbounded<DiscordLootJob>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
        });

    public void Enqueue(DiscordLootJob job)
    {
        if (job.EventLootDetailId <= 0)
        {
            return;
        }
        _channel.Writer.TryWrite(job);
    }

    public ChannelReader<DiscordLootJob> Reader => _channel.Reader;
}
