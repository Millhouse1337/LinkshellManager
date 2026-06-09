using System.Text;
using LinkshellManagerDiscordApp.Data;
using LinkshellManagerDiscordApp.Models;
using Microsoft.EntityFrameworkCore;

namespace LinkshellManagerDiscordApp.Services;

// Posts an "event ended" summary to the linkshell's Discord channel for the
// event's type when an event closes: total duration, attendee count, and each
// attendee's time + DKP earned. A new message (not an edit of the original
// announcement). Bot-only and best-effort, like the announce path; each skip is
// logged so an officer can see why a summary didn't appear.
public sealed class DiscordEventEndedPublisher
{
    private const int EndedColor = 0x2ECC71; // Green.

    private readonly ApplicationDbContext _db;
    private readonly DiscordBotClient _bot;
    private readonly ChannelRouteResolver _routes;
    private readonly ILogger<DiscordEventEndedPublisher> _logger;

    public DiscordEventEndedPublisher(
        ApplicationDbContext db,
        DiscordBotClient bot,
        ChannelRouteResolver routes,
        ILogger<DiscordEventEndedPublisher> logger)
    {
        _db = db;
        _bot = bot;
        _routes = routes;
        _logger = logger;
    }

    public async Task HandleAsync(int eventHistoryId, CancellationToken cancellationToken)
    {
        if (!_bot.IsConfigured)
        {
            return; // No bot → nothing to post; the announce path already logs this.
        }

        try
        {
            var history = await _db.EventHistories
                .AsNoTracking()
                .Include(h => h.AppUserEventHistories)
                .FirstOrDefaultAsync(h => h.Id == eventHistoryId, cancellationToken);
            if (history is null)
            {
                return;
            }

            var channelId = await _routes.ResolveEventChannelIdAsync(history.LinkshellId, history.EventType, cancellationToken);
            if (string.IsNullOrEmpty(channelId))
            {
                _logger.LogInformation(
                    "Event-ended summary for history {HistoryId} not posted: no Discord channel configured for " +
                    "linkshell {LinkshellId} / event type \"{EventType}\".",
                    eventHistoryId, history.LinkshellId, history.EventType ?? "(none)");
                return;
            }

            var payload = new
            {
                embeds = new[] { BuildEmbed(history) },
                allowed_mentions = new { parse = Array.Empty<string>() }
            };
            var messageId = await _bot.PostMessageAsync(channelId, payload, cancellationToken);
            if (string.IsNullOrEmpty(messageId))
            {
                _logger.LogWarning(
                    "Event-ended summary for history {HistoryId}: the bot failed to post to channel {ChannelId} " +
                    "(check the bot is in the server with Send Messages).",
                    eventHistoryId, channelId);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Event-ended summary for history {HistoryId} failed.", eventHistoryId);
        }
    }

    private static object BuildEmbed(EventHistory history)
    {
        var attendees = history.AppUserEventHistories
            .OrderByDescending(a => a.EventDkp ?? 0)
            .ThenBy(a => a.CharacterName)
            .ToList();

        var sb = new StringBuilder();
        if (attendees.Count == 0)
        {
            sb.Append("_No attendees recorded._");
        }
        else
        {
            foreach (var a in attendees)
            {
                var job = string.IsNullOrWhiteSpace(a.JobName) ? string.Empty : $" · {Escape(a.JobName!)}";
                var dur = a.Duration.HasValue ? $" · {FormatDuration(a.Duration.Value)}" : string.Empty;
                var line = $"• {Escape(a.CharacterName ?? "Unknown")}{job}{dur} · **{FormatDkp(a.EventDkp ?? 0)} DKP**";
                if (sb.Length + line.Length + 1 > 3500)
                {
                    sb.Append("\n…");
                    break;
                }
                if (sb.Length > 0)
                {
                    sb.Append('\n');
                }
                sb.Append(line);
            }
        }

        var totalHours = history.Duration
            ?? (history.CommencementStartTime.HasValue && history.EndTime.HasValue
                ? (history.EndTime.Value - history.CommencementStartTime.Value).TotalHours
                : (double?)null);

        var fields = new List<object>();
        if (totalHours.HasValue)
        {
            fields.Add(new { name = "Duration", value = FormatDuration(totalHours.Value), inline = true });
        }
        fields.Add(new { name = "Attendees", value = attendees.Count.ToString(), inline = true });
        fields.Add(new { name = "DKP awarded", value = FormatDkp(attendees.Sum(a => a.EventDkp ?? 0)), inline = true });

        return new
        {
            title = Truncate($"🏁 Event ended — {history.EventName ?? "Event"}", 250),
            description = sb.ToString(),
            color = EndedColor,
            fields = fields.ToArray(),
            footer = new { text = "Event summary" },
            timestamp = (history.EndTime ?? DateTime.UtcNow).ToString("o"),
        };
    }

    private static string FormatDuration(double hours)
    {
        var totalMinutes = (int)Math.Round(Math.Max(0, hours) * 60);
        var h = totalMinutes / 60;
        var m = totalMinutes % 60;
        if (h > 0 && m > 0) return $"{h}h {m}m";
        if (h > 0) return $"{h}h";
        return $"{m}m";
    }

    // DKP is intentionally fractional (per-linkshell increments), so show whole
    // numbers cleanly and fractions to 2 dp.
    private static string FormatDkp(double dkp)
        => dkp == Math.Floor(dkp) ? ((long)dkp).ToString() : dkp.ToString("0.##");

    private static string Escape(string value) => value
        .Replace("\\", "\\\\").Replace("`", "\\`").Replace("*", "\\*")
        .Replace("_", "\\_").Replace("~", "\\~").Replace("|", "\\|");

    private static string Truncate(string value, int max)
        => string.IsNullOrEmpty(value) || value.Length <= max ? value : value[..Math.Max(0, max - 1)] + "…";
}
