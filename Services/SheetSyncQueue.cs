using System.Threading.Channels;

namespace LinkshellManagerDiscordApp.Services;

public sealed class SheetSyncQueue
{
    private readonly Channel<int> _channel = Channel.CreateUnbounded<int>(new UnboundedChannelOptions
    {
        SingleReader = true,
        SingleWriter = false,
    });

    public ValueTask EnqueueAsync(int linkshellId, CancellationToken cancellationToken = default)
    {
        return _channel.Writer.WriteAsync(linkshellId, cancellationToken);
    }

    public ChannelReader<int> Reader => _channel.Reader;
}
