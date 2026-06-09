using LinkshellManagerDiscordApp.Data;
using LinkshellManagerDiscordApp.Models;
using Microsoft.EntityFrameworkCore;

namespace LinkshellManagerDiscordApp.Services;

// A desired channel route from a save request (Activity or web). Id is null for
// a new route; an empty/invalid ChannelId means "drop this route".
public sealed record ChannelRouteEdit(
    int? Id,
    string? Name,
    string? ChannelId,
    bool PostEvents,
    bool PostLoot,
    bool PostAuctions,
    bool PostAttendance,
    bool PostTodBoard,
    IReadOnlyList<string>? EventTypeFilter);

// Shared save logic for a linkshell's Discord channel routes: full replace with
// validation (one owner per non-event post type) and a ToD board re-enqueue so
// toggles take effect immediately. Used by both the Activity API and the web
// Customize page.
public sealed class ChannelRouteEditor
{
    private readonly ApplicationDbContext _db;
    private readonly DiscordTodBoardQueue _todBoardQueue;

    public ChannelRouteEditor(ApplicationDbContext db, DiscordTodBoardQueue todBoardQueue)
    {
        _db = db;
        _todBoardQueue = todBoardQueue;
    }

    // Persists the desired routes for a linkshell. Returns null on success, or a
    // human-readable error (e.g. two routes claiming the same non-event type).
    public async Task<string?> SaveAsync(
        int linkshellId,
        IReadOnlyList<ChannelRouteEdit> edits,
        IReadOnlyList<DiscordChannelInfo>? availableChannels,
        CancellationToken cancellationToken)
    {
        // Only edits with a real channel snowflake become routes; the rest are
        // treated as "removed".
        var valid = edits
            .Where(e => IsSnowflake(e.ChannelId))
            .ToList();

        // One route per non-event post type (events may span several routes via
        // their type filter, so they're exempt).
        var error = ValidateSingleOwner(valid);
        if (error is not null)
        {
            return error;
        }

        var existing = await _db.LinkshellChannelRoutes
            .Where(route => route.LinkshellId == linkshellId)
            .ToListAsync(cancellationToken);

        var keptIds = new HashSet<int>();
        foreach (var edit in valid)
        {
            var channelId = edit.ChannelId!.Trim();
            var name = string.IsNullOrWhiteSpace(edit.Name) ? null : edit.Name.Trim();
            var channelName = availableChannels?.FirstOrDefault(c => c.Id == channelId)?.Name;
            var filter = BuildEventTypeFilter(edit);

            var route = edit.Id is int id ? existing.FirstOrDefault(r => r.Id == id) : null;
            if (route is null)
            {
                route = new LinkshellChannelRoute
                {
                    LinkshellId = linkshellId,
                    CreatedAtUtc = DateTime.UtcNow,
                };
                _db.LinkshellChannelRoutes.Add(route);
            }
            else
            {
                keptIds.Add(route.Id);
                // Preserve the live ToD board message id only when this route keeps
                // posting the board to the SAME channel; otherwise it must repost.
                if (!(edit.PostTodBoard && string.Equals(route.ChannelId, channelId, StringComparison.Ordinal)))
                {
                    route.TodBoardMessageId = null;
                }
            }

            route.Name = name;
            route.ChannelId = channelId;
            route.ChannelName = channelName ?? (route.Id != 0 ? route.ChannelName : null);
            route.PostEvents = edit.PostEvents;
            route.PostLoot = edit.PostLoot;
            route.PostAuctions = edit.PostAuctions;
            route.PostAttendance = edit.PostAttendance;
            route.PostTodBoard = edit.PostTodBoard;
            route.EventTypeFilter = filter;
        }

        // Anything not in the desired set is removed.
        foreach (var route in existing.Where(r => !keptIds.Contains(r.Id)))
        {
            _db.LinkshellChannelRoutes.Remove(route);
        }

        await _db.SaveChangesAsync(cancellationToken);
        // Re-render the ToD board so a (de)selected ToD route takes effect now.
        _todBoardQueue.Enqueue(linkshellId);
        return null;
    }

    private static string? ValidateSingleOwner(IReadOnlyList<ChannelRouteEdit> routes)
    {
        if (routes.Count(r => r.PostLoot) > 1) return "Only one channel route can post Loot.";
        if (routes.Count(r => r.PostAuctions) > 1) return "Only one channel route can post Auctions.";
        if (routes.Count(r => r.PostAttendance) > 1) return "Only one channel route can post Attendance.";
        if (routes.Count(r => r.PostTodBoard) > 1) return "Only one channel route can post the ToD board.";
        return null;
    }

    // Pipe-delimited, validated against the known vocabulary, de-duped. Null when
    // there's no usable filter (the route then catches all event types).
    private static string? BuildEventTypeFilter(ChannelRouteEdit edit)
    {
        if (!edit.PostEvents || edit.EventTypeFilter is null)
        {
            return null;
        }
        var known = new HashSet<string>(EventTypeVocabulary.All, StringComparer.OrdinalIgnoreCase);
        var picked = edit.EventTypeFilter
            .Select(t => t?.Trim())
            .Where(t => !string.IsNullOrEmpty(t) && known.Contains(t!))
            // Normalise to the canonical casing from the vocabulary.
            .Select(t => EventTypeVocabulary.All.First(v => string.Equals(v, t, StringComparison.OrdinalIgnoreCase)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        return picked.Count == 0 ? null : string.Join('|', picked);
    }

    private static bool IsSnowflake(string? value)
    {
        var v = value?.Trim();
        return !string.IsNullOrEmpty(v) && v.Length <= 20 && v.All(char.IsDigit);
    }
}
