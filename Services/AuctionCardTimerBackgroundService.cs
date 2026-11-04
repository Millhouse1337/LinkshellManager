using LinkshellManagerDiscordApp.Data;
using Microsoft.EntityFrameworkCore;

namespace LinkshellManagerDiscordApp.Services;

// Redraws an auction's Discord card when its status changes only because time passed: a scheduled
// auction reaching its start (Queued -> Live) or any auction reaching its end (Live -> Ended, which
// also takes the bid picker off the card).
//
// Every other change already redraws the card as it is saved -- a bid, Start, End early, a re-timed
// auction (ApplicationDbContext.CollectAuctionChannelWork). Time passing saves nothing, so a card used
// to keep saying "Live · ends 18 hours ago" with its picker still on it until somebody touched it.
//
// The last check lives in memory only. The first pass after a restart redraws every posted card whose
// start or end is already behind it, which also catches anything that crossed a boundary while the
// app was down. Open auctions are few, so that is a handful of message edits per deploy.
public sealed class AuctionCardTimerBackgroundService : BackgroundService
{
    // How late a card can be in flipping to Live / Ended.
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(30);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly DiscordAuctionChannelQueue _queue;
    private readonly ILogger<AuctionCardTimerBackgroundService> _logger;

    public AuctionCardTimerBackgroundService(
        IServiceScopeFactory scopeFactory,
        DiscordAuctionChannelQueue queue,
        ILogger<AuctionCardTimerBackgroundService> logger)
    {
        _scopeFactory = scopeFactory;
        _queue = queue;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        DateTime? lastCheckUtc = null;
        using var timer = new PeriodicTimer(PollInterval);
        while (!stoppingToken.IsCancellationRequested)
        {
            var nowUtc = DateTime.UtcNow;
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                foreach (var auctionId in await DueForRedrawAsync(db, lastCheckUtc, nowUtc, stoppingToken))
                {
                    _queue.Enqueue(new AuctionChannelJob(AuctionChannelJobKind.Update, auctionId));
                }
                // Only after a pass that worked, so a failed one is retried over the same stretch.
                lastCheckUtc = nowUtc;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unhandled error in AuctionCardTimerBackgroundService loop.");
            }

            try
            {
                if (!await timer.WaitForNextTickAsync(stoppingToken))
                {
                    break;
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    // The posted auctions whose card status changed in (sinceUtc, nowUtc]: their end arrived, or their
    // scheduled start arrived without anyone pressing Start (Start redraws the card itself). sinceUtc
    // null = the first pass: every posted auction whose start or end is already behind it.
    internal static Task<List<int>> DueForRedrawAsync(
        ApplicationDbContext db, DateTime? sinceUtc, DateTime nowUtc, CancellationToken cancellationToken)
    {
        var posted = db.Auctions
            .AsNoTracking()
            .Where(a => a.DiscordMessageId != null && a.DiscordMessageId != "");

        var due = sinceUtc is { } since
            ? posted.Where(a => (a.EndTime > since && a.EndTime <= nowUtc)
                                || (a.StartedAt == null && a.StartTime > since && a.StartTime <= nowUtc))
            : posted.Where(a => a.EndTime <= nowUtc
                                || (a.StartedAt == null && a.StartTime <= nowUtc));

        return due.Select(a => a.Id).ToListAsync(cancellationToken);
    }
}
