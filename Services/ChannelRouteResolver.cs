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
            ChannelPostTypes.DkpSheet => query.Where(route => route.PostDkpSheet),
            _ => query.Where(_ => false),
        };
        return await query.OrderBy(route => route.Id).FirstOrDefaultAsync(cancellationToken);
    }

    // The channel an event of the given type posts to: the PostEvents route whose
    // event-type filter explicitly contains the type. There is NO unfiltered
    // catch-all — a route only receives the event types it checks; any type that
    // isn't one of the named ones maps to "Other" (so a route can catch those by
    // checking "Other"). Null when no matching event route is configured.
    public async Task<string?> ResolveEventChannelIdAsync(
        int linkshellId, string? eventType, string? monsterName, CancellationToken cancellationToken)
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
        var isNamed = EventTypeVocabulary.All.Any(t =>
            !string.Equals(t, "Other", StringComparison.OrdinalIgnoreCase)
            && string.Equals(t, type, StringComparison.OrdinalIgnoreCase));
        var matchType = isNamed ? type : "Other";

        // HNM can be routed per-monster: an HNM route whose HnmMonsterFilter lists THIS
        // monster wins; else an HNM route with no monster filter (catch-all HNM); else the
        // legacy "Other" route (HNM used to ride the "Other" bucket).
        if (string.Equals(matchType, "HNM", StringComparison.OrdinalIgnoreCase))
        {
            var monster = (monsterName ?? string.Empty).Trim();
            // A combined "Base/Stronger" name (e.g. "Adamantoise/Aspidochelone") matches a
            // route filtering EITHER half; single names split to one segment.
            var monsterSegments = HnmConfig.MonsterSegments(monster);
            var hnmRoutes = rows.Where(route => FilterContains(route.EventTypeFilter, "HNM")).ToList();
            var hnmMatch = monsterSegments.Length > 0
                ? hnmRoutes.FirstOrDefault(route => monsterSegments.Any(seg => FilterContains(route.HnmMonsterFilter, seg)))
                : null;
            hnmMatch ??= hnmRoutes.FirstOrDefault(route => string.IsNullOrWhiteSpace(route.HnmMonsterFilter));
            hnmMatch ??= rows.FirstOrDefault(route => FilterContains(route.EventTypeFilter, "Other"));
            return string.IsNullOrEmpty(hnmMatch?.ChannelId) ? null : hnmMatch!.ChannelId;
        }

        var match = rows.FirstOrDefault(route => FilterContains(route.EventTypeFilter, matchType));
        return string.IsNullOrEmpty(match?.ChannelId) ? null : match!.ChannelId;
    }

    // True when the pipe-delimited filter explicitly contains the given event
    // type. An empty filter matches nothing (no catch-all): a route must check the
    // type to receive it.
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
