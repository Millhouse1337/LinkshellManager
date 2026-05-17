using System.Threading.Channels;

namespace LinkshellManagerDiscordApp.Services;

// Fire-and-forget queue for posting an attendance snapshot to a linkshell's
// Discord channel webhook. Mirrors SheetSyncQueue in spirit: single-reader,
// unbounded, drained by DiscordWebhookBackgroundService. Kept separate from
// SheetSyncQueue so Discord delivery and Google Sheet sync fail independently.
public sealed class DiscordWebhookQueue
{
    private readonly Channel<int> _channel = Channel.CreateUnbounded<int>(new UnboundedChannelOptions
    {
        SingleReader = true,
        SingleWriter = false,
    });

    // Enqueues an AttendanceSnapshot id to publish. The snapshot is loaded
    // (with its linkshell webhook URL) when the job runs, so this never blocks
    // the addon request even if Discord is slow or unreachable.
    public ValueTask EnqueueSnapshotAsync(int snapshotId, CancellationToken cancellationToken = default)
    {
        return _channel.Writer.WriteAsync(snapshotId, cancellationToken);
    }

    public ChannelReader<int> Reader => _channel.Reader;
}
