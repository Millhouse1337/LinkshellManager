using System.Threading.Channels;

namespace LinkshellManagerDiscordApp.Services;

public enum AuctionChannelJobKind
{
    // A new auction was created → create its Discord channel + post details.
    Create,

    // An auction was closed → post final results then delete the channel.
    Close,

    // A bid was placed → edit the auction's posted message in place to show the
    // current high bid per item (no new message, no channel spam).
    Update,
}

// Create / Update: EntityId is the Auction id. Close: EntityId is the
// AuctionHistory id (the live Auction row is gone by then).
public readonly record struct AuctionChannelJob(AuctionChannelJobKind Kind, int EntityId);

// Fire-and-forget queue of per-auction Discord channel jobs. Enqueued from
// ApplicationDbContext on a successful commit (a new Auction → Create, a new
// AuctionHistory carrying a channel id → Close) so every create/close path
// triggers it without per-controller wiring. Drained by
// DiscordAuctionChannelBackgroundService. Singleton, single-reader, mirrors
// DiscordTodBoardQueue / DiscordDkpSpendQueue.
public sealed class DiscordAuctionChannelQueue
{
    private readonly Channel<AuctionChannelJob> _channel =
        Channel.CreateUnbounded<AuctionChannelJob>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
        });

    public void Enqueue(AuctionChannelJob job)
    {
        if (job.EntityId <= 0)
        {
            return;
        }
        // Unbounded channel: TryWrite always succeeds. Non-blocking so a
        // SaveChanges is never delayed by Discord delivery.
        _channel.Writer.TryWrite(job);
    }

    public ChannelReader<AuctionChannelJob> Reader => _channel.Reader;
}
