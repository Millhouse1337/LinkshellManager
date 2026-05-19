using System.Threading.Channels;

namespace LinkshellManagerDiscordApp.Services;

// Fire-and-forget queue of linkshell ids whose Discord ToD board needs a
// rebuild. Enqueued from ApplicationDbContext whenever a Tod row is
// added / modified / deleted (so every mutation path — web, addon, Activity,
// approval — triggers it without per-controller wiring) and from the
// Customize save. Drained + debounced by DiscordTodBoardBackgroundService,
// which edits the existing board message in place. Singleton, single-reader,
// mirrors DiscordWebhookQueue.
public sealed class DiscordTodBoardQueue
{
    private readonly Channel<int> _channel = Channel.CreateUnbounded<int>(new UnboundedChannelOptions
    {
        SingleReader = true,
        SingleWriter = false,
    });

    public void Enqueue(int linkshellId)
    {
        if (linkshellId <= 0)
        {
            return;
        }
        // Unbounded channel: TryWrite always succeeds. Non-blocking so a
        // SaveChanges (or web request) is never delayed by board delivery.
        _channel.Writer.TryWrite(linkshellId);
    }

    public ChannelReader<int> Reader => _channel.Reader;
}
