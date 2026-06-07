using System.Threading.Channels;

namespace LinkshellManagerDiscordApp.Services;

// Which loot table a queued post refers to.
public enum LootJobSource
{
    Tod,
    Event,
}

// EntityId is the TodLootDetail id (Tod) or EventLootDetail id (Event).
public readonly record struct DiscordLootJob(LootJobSource Source, int EntityId);

// Fire-and-forget queue of awarded-loot rows to announce to a linkshell's Loot
// Discord channel. Enqueued from ApplicationDbContext on a successful commit
// whenever a new TodLootDetail / EventLootDetail with a winner is added (any
// path — web, Activity, addon, ToD, event loot). Drained by
// DiscordLootChannelBackgroundService. Singleton, single-reader; mirrors
// DiscordAuctionChannelQueue.
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
        if (job.EntityId <= 0)
        {
            return;
        }
        _channel.Writer.TryWrite(job);
    }

    public ChannelReader<DiscordLootJob> Reader => _channel.Reader;
}
