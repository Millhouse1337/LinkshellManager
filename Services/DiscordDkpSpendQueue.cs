using System.Threading.Channels;

namespace LinkshellManagerDiscordApp.Services;

// Fire-and-forget queue of DkpLedgerEntry ids that represent a DKP spend
// (negative Amount). Enqueued from ApplicationDbContext whenever such a row
// is added (so every spend path — auction close, loot-edit, manual
// adjustment/audit — triggers it without per-controller wiring). Drained +
// debounced by DiscordDkpSpendBackgroundService, which groups a save-burst
// into one rich embed per channel. Append-only: unlike the ToD board there
// is no message to edit, so we carry entry ids (not linkshell ids) and post
// a fresh message each burst. Singleton, single-reader, mirrors
// DiscordTodBoardQueue.
public sealed class DiscordDkpSpendQueue
{
    private readonly Channel<int> _channel = Channel.CreateUnbounded<int>(new UnboundedChannelOptions
    {
        SingleReader = true,
        SingleWriter = false,
    });

    public void Enqueue(int ledgerEntryId)
    {
        if (ledgerEntryId <= 0)
        {
            return;
        }
        // Unbounded channel: TryWrite always succeeds. Non-blocking so a
        // SaveChanges is never delayed by Discord delivery.
        _channel.Writer.TryWrite(ledgerEntryId);
    }

    public ChannelReader<int> Reader => _channel.Reader;
}
