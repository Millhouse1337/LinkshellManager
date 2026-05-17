using System.Threading.Channels;

namespace LinkshellManagerDiscordApp.Services;

// Fire-and-forget queue for AttInput-append jobs. Triggers (snapshot capture,
// per-window attendance post, event close) enqueue a typed job; the
// SheetSyncBackgroundService drains them and dispatches to AttInputAppendService.
//
// Previously this queued bare linkshell IDs for the now-removed Main!C
// per-row overwrite. The queue is unchanged in spirit -- still
// fire-and-forget, still single-reader -- but each job now carries enough
// information to know which kind of append to run.
public sealed class SheetSyncQueue
{
    private readonly Channel<SheetSyncJob> _channel = Channel.CreateUnbounded<SheetSyncJob>(new UnboundedChannelOptions
    {
        SingleReader = true,
        SingleWriter = false,
    });

    public ValueTask EnqueueSnapshotAsync(int snapshotId, CancellationToken cancellationToken = default)
    {
        return _channel.Writer.WriteAsync(new SheetSyncJob(SheetSyncJobKind.Snapshot, snapshotId), cancellationToken);
    }

    public ValueTask EnqueueEventWindowAsync(int eventAttendanceWindowId, CancellationToken cancellationToken = default)
    {
        return _channel.Writer.WriteAsync(new SheetSyncJob(SheetSyncJobKind.EventWindow, eventAttendanceWindowId), cancellationToken);
    }

    public ValueTask EnqueueEventCloseAsync(int eventId, CancellationToken cancellationToken = default)
    {
        return _channel.Writer.WriteAsync(new SheetSyncJob(SheetSyncJobKind.EventClose, eventId), cancellationToken);
    }

    public ValueTask EnqueueDkpAuditAsync(int dkpLedgerEntryId, CancellationToken cancellationToken = default)
    {
        return _channel.Writer.WriteAsync(new SheetSyncJob(SheetSyncJobKind.DkpAudit, dkpLedgerEntryId), cancellationToken);
    }

    // Writes every AuctionSpent ledger entry tied to an AuctionHistory into a
    // single ManualPoints column on the linkshell's Google Sheet. Fired by
    // AuctionController.CloseAuction immediately after the close commits.
    public ValueTask EnqueueAuctionDeductionsAsync(int auctionHistoryId, CancellationToken cancellationToken = default)
    {
        return _channel.Writer.WriteAsync(new SheetSyncJob(SheetSyncJobKind.AuctionDeductions, auctionHistoryId), cancellationToken);
    }

    // Same as AuctionDeductions but for LootSpent ledger entries on an
    // EventHistory. Fired by EventController.Lifecycle after EndEventCoreAsync.
    public ValueTask EnqueueEventLootDeductionsAsync(int eventHistoryId, CancellationToken cancellationToken = default)
    {
        return _channel.Writer.WriteAsync(new SheetSyncJob(SheetSyncJobKind.EventLootDeductions, eventHistoryId), cancellationToken);
    }

    // Recomputes the ManualPoints "per-day" column for the ToD's linkshell+date
    // from the current TodLootDetail rows. Keyed by TodId (not a loot-detail or
    // ledger id) so it survives loot deletes and so post / edit / delete all
    // converge on the same idempotent recompute. Fired by the addon ToD loot
    // post, the web/Activity ToD loot create/delete, and the ToD loot edit.
    public ValueTask EnqueueTodLootDeductionsAsync(int todId, CancellationToken cancellationToken = default)
    {
        return _channel.Writer.WriteAsync(new SheetSyncJob(SheetSyncJobKind.TodLootDeductions, todId), cancellationToken);
    }

    // Posts the entire Window Event roster (header row + one row per unique
    // active character) to the linkshell's Google Sheet AttInput tab. Fired
    // by the officer's Post to DKP Sheet button -- snapshot capture itself
    // no longer auto-enqueues, so this is the one user-initiated push.
    public ValueTask EnqueueWindowEventPostAsync(int windowEventId, CancellationToken cancellationToken = default)
    {
        return _channel.Writer.WriteAsync(new SheetSyncJob(SheetSyncJobKind.WindowEventPost, windowEventId), cancellationToken);
    }

    // Re-syncs a Window Event whose officer-facing fields (DKP, Entry Type)
    // were edited after the initial post. Rewrites the J:K cells across the
    // tracked row range and adjusts the matching ledger entries + member
    // totals so the local store stays consistent with the sheet.
    public ValueTask EnqueueWindowEventEditAsync(int windowEventId, CancellationToken cancellationToken = default)
    {
        return _channel.Writer.WriteAsync(new SheetSyncJob(SheetSyncJobKind.WindowEventEdit, windowEventId), cancellationToken);
    }

    // Legacy entry point. Kept temporarily to avoid breaking older call sites
    // during the migration to typed jobs; treats the linkshellId as a no-op
    // since the column-C overwrite has been removed. Callers should migrate
    // to the typed enqueue methods above.
    public ValueTask EnqueueAsync(int linkshellId, CancellationToken cancellationToken = default)
    {
        // Intentionally a no-op now -- legacy DKP-write hooks that called this
        // were doing the Main!C overwrite that we no longer want.
        return ValueTask.CompletedTask;
    }

    public ChannelReader<SheetSyncJob> Reader => _channel.Reader;
}

public readonly record struct SheetSyncJob(SheetSyncJobKind Kind, int TargetId);

public enum SheetSyncJobKind
{
    Snapshot,
    EventWindow,
    EventClose,
    DkpAudit,
    WindowEventPost,
    WindowEventEdit,
    AuctionDeductions,
    EventLootDeductions,
    TodLootDeductions,
}
