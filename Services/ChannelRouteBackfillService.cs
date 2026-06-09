using LinkshellManagerDiscordApp.Data;
using LinkshellManagerDiscordApp.Models;
using Microsoft.EntityFrameworkCore;

namespace LinkshellManagerDiscordApp.Services;

// One-time, idempotent startup backfill: converts each linkshell's legacy
// LinkshellDiscordChannel rows (fixed HENM/KSNM/Other/Auctions/Loot purposes)
// into the new LinkshellChannelRoutes, merging purposes that share a channel id
// into a single route. Skips any linkshell that already has routes, so it's safe
// to run on every boot until the legacy table is dropped.
//
// Webhook configs (ToD board / Attendance) can't be auto-converted — a webhook
// URL is not a channel snowflake — so officers reconfigure those under the new
// "Discord channel routes" card.
public sealed class ChannelRouteBackfillService : IHostedService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<ChannelRouteBackfillService> _logger;

    public ChannelRouteBackfillService(
        IServiceScopeFactory scopeFactory, ILogger<ChannelRouteBackfillService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

            // Only linkshells that have legacy channel rows but no routes yet.
            var linkshellIdsWithRoutes = await db.LinkshellChannelRoutes
                .Select(route => route.LinkshellId)
                .Distinct()
                .ToListAsync(cancellationToken);
            var pending = await db.LinkshellDiscordChannels
                .AsNoTracking()
                .Where(channel => channel.ChannelId != "" && !linkshellIdsWithRoutes.Contains(channel.LinkshellId))
                .ToListAsync(cancellationToken);
            if (pending.Count == 0)
            {
                return;
            }

            var created = 0;
            foreach (var byLinkshell in pending.GroupBy(channel => channel.LinkshellId))
            {
                foreach (var byChannel in byLinkshell.GroupBy(channel => channel.ChannelId))
                {
                    var purposes = byChannel.Select(channel => channel.Purpose).ToHashSet(StringComparer.OrdinalIgnoreCase);
                    var route = new LinkshellChannelRoute
                    {
                        LinkshellId = byLinkshell.Key,
                        ChannelId = byChannel.Key,
                        ChannelName = byChannel.Select(channel => channel.ChannelName).FirstOrDefault(name => !string.IsNullOrEmpty(name)),
                        PostLoot = purposes.Contains(DiscordChannelPurposes.Loot),
                        PostAuctions = purposes.Contains(DiscordChannelPurposes.Auctions),
                        CreatedAtUtc = DateTime.UtcNow,
                    };

                    // Event purposes → one PostEvents route. A catch-all (Events)
                    // sharing the channel wins over the type filter so no event is
                    // ever dropped; otherwise the filter lists the specific types.
                    var hasEvents = purposes.Contains(DiscordChannelPurposes.Events);
                    var types = new List<string>();
                    if (purposes.Contains(DiscordChannelPurposes.HenmEvents)) types.Add("HENM");
                    if (purposes.Contains(DiscordChannelPurposes.KsnmEvents)) types.Add("KSNM");
                    if (hasEvents || types.Count > 0)
                    {
                        route.PostEvents = true;
                        route.EventTypeFilter = hasEvents ? null : string.Join('|', types);
                    }

                    db.LinkshellChannelRoutes.Add(route);
                    created++;
                }
            }

            await db.SaveChangesAsync(cancellationToken);
            _logger.LogInformation(
                "Channel-route backfill created {Count} route(s) from legacy channel configs.", created);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Best-effort: a backfill failure must never block startup.
            _logger.LogError(ex, "Channel-route backfill failed; legacy channel configs were not migrated.");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
