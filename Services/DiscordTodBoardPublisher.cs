using System.Text;
using LinkshellManagerDiscordApp.Data;
using LinkshellManagerDiscordApp.Models;
using Microsoft.EntityFrameworkCore;

namespace LinkshellManagerDiscordApp.Services;

// Builds and maintains the live "ToD board": one Discord message in the
// linkshell's ToD channel route that lists every ToD. The message is POSTed
// once, its id stored on the route, and every subsequent change edits that same
// message in place (never re-posts). Repop times use Discord's native
// relative-timestamp markdown (<t:unix:R>) so the "in 4 hours" countdown ticks
// on the client without us having to re-edit.
public sealed class DiscordTodBoardPublisher
{
    private const int EmbedColor = 0x5865F2; // Discord blurple.
    private const int MaxDescription = 4000;  // Discord hard limit is 4096.

    private readonly ApplicationDbContext _db;
    private readonly ChannelRouteResolver _routes;
    private readonly DiscordBotClient _bot;
    private readonly ILogger<DiscordTodBoardPublisher> _logger;

    public DiscordTodBoardPublisher(
        ApplicationDbContext db,
        ChannelRouteResolver routes,
        DiscordBotClient bot,
        ILogger<DiscordTodBoardPublisher> logger)
    {
        _db = db;
        _routes = routes;
        _bot = bot;
        _logger = logger;
    }

    public async Task PublishAsync(int linkshellId, CancellationToken cancellationToken)
    {
        if (!_bot.IsConfigured)
        {
            return;
        }

        // Tracked: we persist TodBoardMessageId back onto this route.
        var route = await _routes.ResolveRouteAsync(linkshellId, ChannelPostTypes.TodBoard, cancellationToken);
        if (route is null || string.IsNullOrEmpty(route.ChannelId))
        {
            return;
        }

        var linkshell = await _db.Linkshells
            .AsNoTracking()
            .Where(l => l.Id == linkshellId)
            .Select(l => new { l.LinkshellName, l.HiddenTodMonsters })
            .FirstOrDefaultAsync(cancellationToken);

        var hidden = new HashSet<string>(
            (linkshell?.HiddenTodMonsters ?? string.Empty)
                .Split('|', StringSplitOptions.RemoveEmptyEntries)
                .Select(s => s.Trim())
                .Where(s => s.Length > 0),
            StringComparer.OrdinalIgnoreCase);

        // All ToDs for the linkshell (HNMs included — the board is the full
        // pop tracker, unlike the web /Tod page which hides true HNMs).
        var tods = await _db.Tods
            .AsNoTracking()
            .Where(t => t.LinkshellId == linkshellId)
            .Select(t => new { t.Id, t.MonsterName, t.DayNumber, t.Time, t.RepopTime, t.Claim, t.Cooldown })
            .ToListAsync(cancellationToken);

        // Latest row per monster (Time desc, Id desc — same as the web list),
        // hidden monsters removed, ordered by soonest repop (nulls last).
        var rows = tods
            .Where(t => !string.IsNullOrWhiteSpace(t.MonsterName)
                        && !hidden.Contains(t.MonsterName!.Trim()))
            .GroupBy(t => t.MonsterName!.Trim(), StringComparer.OrdinalIgnoreCase)
            .Select(g => g
                .OrderByDescending(t => t.Time ?? DateTime.MinValue)
                .ThenByDescending(t => t.Id)
                .First())
            .OrderBy(t => t.RepopTime ?? DateTime.MaxValue)
            .ThenBy(t => t.MonsterName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var description = new StringBuilder();
        if (rows.Count == 0)
        {
            description.Append("_No ToDs recorded yet._");
        }
        else
        {
            var omitted = 0;
            foreach (var row in rows)
            {
                // Name · Day N · <cooldown> CD — <repop absolute> (<live countdown>).
                // <t:unix:R> is Discord's native relative timestamp: it ticks
                // down on every client on its own, no re-edit from us needed.
                var meta = $"**{EscapeMarkdown(row.MonsterName!.Trim())}**";
                if (row.DayNumber is > 0)
                {
                    meta += $" · Day {row.DayNumber}";
                }
                if (!string.IsNullOrWhiteSpace(row.Cooldown))
                {
                    meta += $" · {EscapeMarkdown(row.Cooldown!.Trim())} CD";
                }

                string line;
                if (row.RepopTime.HasValue)
                {
                    var unix = ((DateTimeOffset)DateTime.SpecifyKind(row.RepopTime.Value, DateTimeKind.Utc))
                        .ToUnixTimeSeconds();
                    line = $"{meta} — <t:{unix}:f> (<t:{unix}:R>)";
                }
                else
                {
                    line = $"{meta} — _no repop time_";
                }

                if (description.Length + line.Length + 1 > MaxDescription)
                {
                    omitted++;
                    continue;
                }
                if (description.Length > 0)
                {
                    description.Append('\n');
                }
                description.Append(line);
            }
            if (omitted > 0)
            {
                description.Append($"\n_…and {omitted} more_");
            }
        }

        var payload = new
        {
            embeds = new[]
            {
                new
                {
                    title = string.IsNullOrWhiteSpace(linkshell?.LinkshellName)
                        ? "ToD Tracker"
                        : $"ToD Tracker — {Truncate(linkshell!.LinkshellName!, 230)}",
                    description = description.ToString(),
                    color = EmbedColor,
                    footer = new { text = "Updated" },
                    timestamp = DateTime.UtcNow.ToString("o"),
                },
            },
            allowed_mentions = new { parse = Array.Empty<string>() },
        };

        // Edit the existing board in place; if the edit fails (message deleted,
        // channel changed, etc.) drop the stale id and post a fresh one. The new
        // id is persisted on the route so the next change edits it.
        if (!string.IsNullOrEmpty(route.TodBoardMessageId))
        {
            if (await _bot.EditMessageAsync(route.ChannelId, route.TodBoardMessageId, payload, cancellationToken))
            {
                return;
            }
            route.TodBoardMessageId = null;
        }

        var messageId = await _bot.PostMessageAsync(route.ChannelId, payload, cancellationToken);
        if (string.IsNullOrEmpty(messageId))
        {
            _logger.LogWarning(
                "ToD board for linkshell {LinkshellId}: the bot failed to post to channel {ChannelId} " +
                "(check the bot is in the server with Send Messages).",
                linkshellId, route.ChannelId);
            // Persist the cleared id (if we nulled it above) so the next pass reposts.
            if (_db.ChangeTracker.HasChanges())
            {
                await _db.SaveChangesAsync(cancellationToken);
            }
            return;
        }

        route.TodBoardMessageId = messageId.Length > 32 ? messageId[..32] : messageId;
        await _db.SaveChangesAsync(cancellationToken);
    }

    private static string EscapeMarkdown(string value) => value
        .Replace("\\", "\\\\")
        .Replace("`", "\\`")
        .Replace("*", "\\*")
        .Replace("_", "\\_")
        .Replace("~", "\\~")
        .Replace("|", "\\|");

    private static string Truncate(string value, int max)
    {
        if (string.IsNullOrEmpty(value) || value.Length <= max)
        {
            return value;
        }
        return value.Substring(0, Math.Max(0, max - 1)) + "…";
    }
}
