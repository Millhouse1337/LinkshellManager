using System.Threading.Channels;

namespace LinkshellManagerDiscordApp.Services;

// Fire-and-forget queue of linkshell ids whose "LSM DKP" template tab should be
// re-exported because a member's DKP changed (live sync, push-only). Enqueued
// from ApplicationDbContext whenever a DkpLedgerEntry changes or an
// AppUserLinkshell's LinkshellDkp is modified — so every DKP path (event close,
// auction, loot, audits, window events) triggers it without per-controller
// wiring. Drained + debounced by SheetTemplateSyncBackgroundService, which only
// pushes for linkshells with live sync enabled + a connected sheet. Singleton,
// single-reader, mirrors DiscordTodBoardQueue.
public sealed class SheetTemplateSyncQueue
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
        // SaveChanges is never delayed by sheet delivery.
        _channel.Writer.TryWrite(linkshellId);
    }

    public ChannelReader<int> Reader => _channel.Reader;
}
