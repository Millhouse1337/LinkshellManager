using System.Threading.Channels;

namespace LinkshellManagerDiscordApp.Services;

// A Discord (channelId, messageId) pair whose message should be deleted — the
// signup/party board of an event that has just ended or been cancelled.
public readonly record struct DiscordMessageRef(string ChannelId, string MessageId);

// Fire-and-forget queue of event signup-board messages to delete. Enqueued from
// ApplicationDbContext on a successful commit whenever an Event row that carried
// a posted board is deleted (every end + cancel path deletes the Event), so the
// board is cleaned up with no per-controller wiring. Drained by
// DiscordEventCleanupBackgroundService. Mirrors DiscordEventEndedQueue.
public sealed class DiscordEventCleanupQueue
{
    private readonly Channel<DiscordMessageRef> _channel =
        Channel.CreateUnbounded<DiscordMessageRef>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
        });

    public void Enqueue(DiscordMessageRef message)
    {
        if (string.IsNullOrWhiteSpace(message.ChannelId) || string.IsNullOrWhiteSpace(message.MessageId))
        {
            return;
        }
        _channel.Writer.TryWrite(message);
    }

    public ChannelReader<DiscordMessageRef> Reader => _channel.Reader;
}
