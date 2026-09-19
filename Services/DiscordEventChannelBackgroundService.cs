using System.Threading.Channels;

namespace LinkshellManagerDiscordApp.Services;

// Drains DiscordEventChannelQueue and runs each event id through the publisher, which renders
// the event's board to its Discord channel from whatever state the event is in right now.
public sealed class DiscordEventChannelBackgroundService : BackgroundService
{
    // How long a burst is left to settle before anything renders.
    //
    // One action often saves the same event more than once in quick succession, and every save
    // queues a render. The addon's End Event is the case that broke: it parks the camp as
    // defeated, then a few hundred milliseconds later revives it for the next pop. Rendered as
    // they arrived, the first render read the half-finished state -- the "defeated" note, which on
    // a wide board DELETES every alliance message after the first -- and the second then had to
    // re-post those alliances from scratch, at the bottom of the channel, into a rate limit.
    //
    // Waiting a beat and rendering each event ONCE, from wherever it settled, edits the board in
    // place instead: nothing deleted, nothing re-posted, a fraction of the Discord calls. It also
    // folds a run of signup clicks into one refresh. The click itself is answered immediately; only
    // the board's redraw waits.
    internal static readonly TimeSpan SettleDelay = TimeSpan.FromSeconds(1);

    private readonly DiscordEventChannelQueue _queue;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<DiscordEventChannelBackgroundService> _logger;

    public DiscordEventChannelBackgroundService(
        DiscordEventChannelQueue queue,
        IServiceScopeFactory scopeFactory,
        ILogger<DiscordEventChannelBackgroundService> logger)
    {
        _queue = queue;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var first = await _queue.Reader.ReadAsync(stoppingToken);
                await Task.Delay(SettleDelay, stoppingToken);

                foreach (var eventId in DrainDistinct(first, _queue.Reader))
                {
                    // Per event, so one board that throws cannot drop the others in the same batch
                    // -- each of them used to get its own pass through the outer catch.
                    try
                    {
                        using var scope = _scopeFactory.CreateScope();
                        await scope.ServiceProvider
                            .GetRequiredService<DiscordEventChannelPublisher>()
                            .HandleAsync(eventId, stoppingToken);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        _logger.LogError(ex, "Rendering the Discord board for event {EventId} failed.", eventId);
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unhandled error in DiscordEventChannelBackgroundService loop.");
                try { await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken); }
                catch (OperationCanceledException) { }
            }
        }
    }

    // `first`, then everything else already waiting, each event ONCE, in the order first seen.
    // Order is kept so a burst across several boards still renders them in the order they changed.
    internal static List<int> DrainDistinct(int first, ChannelReader<int> reader)
    {
        var seen = new HashSet<int> { first };
        var order = new List<int> { first };
        while (reader.TryRead(out var next))
        {
            if (seen.Add(next))
            {
                order.Add(next);
            }
        }
        return order;
    }
}
