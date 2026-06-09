using LinkshellManagerDiscordApp.Data;
using LinkshellManagerDiscordApp.Models;
using Microsoft.EntityFrameworkCore;

namespace LinkshellManagerDiscordApp.Services;

// Resolves which Discord channel a given kind of post goes to for a linkshell,
// from its user-defined LinkshellChannelRoutes. The single place every publisher
// asks "where does this post go?" — replaces the old per-purpose channel lookups
// and the webhook flag scans.
public sealed class ChannelRouteResolver
{
    private readonly ApplicationDbContext _db;

    public ChannelRouteResolver(ApplicationDbContext db)
    {
        _db = db;
    }

    // Channel id for a NON-event post type (Loot/Auctions/Attendance/TodBoard).
    // At most one route owns each (enforced on save), so this returns that route's
    // channel, or null when nothing is configured.
    public async Task<string?> ResolveChannelIdAsync(
        int linkshellId, string postType, CancellationToken cancellationToken)
    {
        var route = await ResolveRouteAsync(linkshellId, postType, cancellationToken);
        return string.IsNullOrEmpty(route?.ChannelId) ? null : route!.ChannelId;
    }

    // The route for a non-event post type, tracked so callers (the ToD board) can
    // read/write TodBoardMessageId on it. Null when nothing is configured.
    public async Task<LinkshellChannelRoute?> ResolveRouteAsync(
        int linkshellId, string postType, CancellationToken cancellationToken)
    {
        var query = _db.LinkshellChannelRoutes
            .Where(route => route.LinkshellId == linkshellId && route.ChannelId != "");
        query = postType switch
        {
            ChannelPostTypes.Loot => query.Where(route => route.PostLoot),
            ChannelPostTypes.Auctions => query.Where(route => route.PostAuctions),
            ChannelPostTypes.Attendance => query.Where(route => route.PostAttendance),
            ChannelPostTypes.TodBoard => query.Where(route => route.PostTodBoard),
            _ => query.Where(_ => false),
        };
        return await query.OrderBy(route => route.Id).FirstOrDefaultAsync(cancellationToken);
    }

    // The channel an event of the given type posts to: the PostEvents route whose
    // event-type filter contains the type, else the unfiltered (catch-all)
    // PostEvents route. Null when no event route is configured.
    public async Task<string?> ResolveEventChannelIdAsync(
        int linkshellId, string? eventType, CancellationToken cancellationToken)
    {
        var rows = await _db.LinkshellChannelRoutes
            .AsNoTracking()
            .Where(route => route.LinkshellId == linkshellId && route.PostEvents && route.ChannelId != "")
            .OrderBy(route => route.Id)
            .ToListAsync(cancellationToken);
        if (rows.Count == 0)
        {
            return null;
        }

        var type = (eventType ?? string.Empty).Trim();
        var match = rows.FirstOrDefault(route => FilterContains(route.EventTypeFilter, type))
            ?? rows.FirstOrDefault(route => string.IsNullOrWhiteSpace(route.EventTypeFilter));
        return string.IsNullOrEmpty(match?.ChannelId) ? null : match!.ChannelId;
    }

    // True when the pipe-delimited filter contains the given event type. An empty
    // filter is the catch-all and is handled separately (it does NOT match here,
    // so a specific-type route is always preferred over the catch-all).
    private static bool FilterContains(string? filter, string eventType)
    {
        if (string.IsNullOrWhiteSpace(filter) || string.IsNullOrWhiteSpace(eventType))
        {
            return false;
        }
        return filter
            .Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Any(token => string.Equals(token, eventType, StringComparison.OrdinalIgnoreCase));
    }
}
