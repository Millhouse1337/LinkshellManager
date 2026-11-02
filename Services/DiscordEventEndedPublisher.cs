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

            // An HNM camp whose board is now its "defeated" note shows this summary ON that note
            // (HnmBoardNoticeService), so ending a camp posts one message, not two.
            if (await CarriedByCampBoardAsync(_db, eventHistoryId, cancellationToken))
            {
                _logger.LogInformation(
                    "Event-ended summary for history {HistoryId} is shown on the camp's defeated board note; not posted separately.",
                    eventHistoryId);
                return;
            }

            // EventHistory carries no monster, so an HNM camp's is looked up: without it a summary
            // could only reach a catch-all HNM route, never the monster's own channel.
            var monsterName = await CampMonsterNameAsync(history, cancellationToken);
            var channelId = await _routes.ResolveEventChannelIdAsync(history.LinkshellId, history.EventType, monsterName, cancellationToken);
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
                embeds = new[] { await BuildEmbedAsync(_db, history, cancellationToken) },
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

    // The monster an HNM camp's archive belongs to, or null. From the camp's review card, whose
    // SourceEvent is the camp row (End Camp recycles it for the next pop, so it is still there);
    // else from a Claim Shield capture filed on the archive. Not HNM -> null, no lookups.
    private async Task<string?> CampMonsterNameAsync(EventHistory history, CancellationToken cancellationToken)
    {
        if (!string.Equals(history.EventType?.Trim(), "HNM", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }
        var fromCamp = await _db.WindowEvents
            .AsNoTracking()
            .Where(card => card.CampEventHistoryId == history.Id && card.SourceEvent != null)
            .Select(card => card.SourceEvent!.AssignedMonsterName)
            .FirstOrDefaultAsync(cancellationToken);
        if (!string.IsNullOrWhiteSpace(fromCamp))
        {
            return fromCamp;
        }
        return await _db.ClaimShieldCaptures
            .AsNoTracking()
            .Where(capture => capture.EventHistoryId == history.Id && capture.MonsterName != "")
            .Select(capture => capture.MonsterName)
            .FirstOrDefaultAsync(cancellationToken);
    }

    // The summary embed for a Past Event, or null when it is gone. Shared with the HNM defeated
    // note, which carries it at the top in place of a second message.
    public static async Task<object?> BuildSummaryEmbedAsync(
        ApplicationDbContext db, int eventHistoryId, CancellationToken cancellationToken)
    {
        var history = await db.EventHistories
            .AsNoTracking()
            .Include(h => h.AppUserEventHistories)
            .FirstOrDefaultAsync(h => h.Id == eventHistoryId, cancellationToken);
        return history is null ? null : await BuildEmbedAsync(db, history, cancellationToken);
    }

    // True when this Past Event is an HNM camp whose board is parked as its "defeated" note -- the
    // note shows the summary, so it is not posted on its own. The same link the note uses to find
    // its archive (DiscordEventChannelPublisher.ResolveCampArchiveIdAsync): the review card's
    // SourceEvent. A camp whose board was removed at End Camp (repost off) has no SourceEvent left,
    // so its summary still posts.
    public static Task<bool> CarriedByCampBoardAsync(
        ApplicationDbContext db, int eventHistoryId, CancellationToken cancellationToken)
        => db.WindowEvents
            .AsNoTracking()
            .AnyAsync(card => card.CampEventHistoryId == eventHistoryId
                && card.SourceEvent != null
                && card.SourceEvent.HnmDefeatedAt != null
                && card.SourceEvent.DiscordMessageId != null
                && card.SourceEvent.DiscordMessageId != "",
                cancellationToken);

    private static async Task<object> BuildEmbedAsync(
        ApplicationDbContext db, EventHistory history, CancellationToken cancellationToken)
    {
        // Who tagged the mob on this camp's Claim Shield. Read off the ARCHIVE
        // (Capture.EventHistoryId), because End Camp clears Capture.EventId onto it -- the
        // live-event id is gone by the time this summary is built.
        //
        // Every capture on the camp contributes names, won or lost: a tag is a tag whether or
        // not the lottery went our way, which is the same rule HnmStandardCampFinalizer
        // applies when it decides who the claim bonus reaches.
        var taggers = await db.ClaimShieldCaptureMembers
            .AsNoTracking()
            .Where(m => m.Capture!.EventHistoryId == history.Id)
            .Select(m => new TaggerRow(m.CharacterName, m.Matched))
            .ToListAsync(cancellationToken);
        return BuildEmbed(history, taggers);
    }

    // One Claim Shield name as stored. Matched says whether it resolved to a current linkshell
    // membership -- the gate the claim bonus itself uses, so an unmatched name is listed but must
    // not be presented as having been paid.
    private sealed record TaggerRow(string CharacterName, bool Matched);

    private static object BuildEmbed(EventHistory history, IReadOnlyList<TaggerRow> taggers)
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
        // Claim Shield, when the camp had one. Omitted entirely otherwise, so an ordinary timed
        // event's summary is unchanged rather than carrying an empty field.
        //
        // Folded to one row per NAME: a member who tagged in three lotteries is one tagger and is
        // paid once, so listing the capture rows verbatim would overstate both.
        var claimNames = taggers
            .Where(t => !string.IsNullOrWhiteSpace(t.CharacterName))
            .GroupBy(t => t.CharacterName.Trim(), StringComparer.OrdinalIgnoreCase)
            // Matched wins on a collision: the same name can appear matched on one capture and
            // unmatched on another (the roster changed mid-camp), and being paid is the fact.
            .Select(g => new TaggerRow(g.Key, g.Any(t => t.Matched)))
            .OrderByDescending(t => t.Matched)
            .ThenBy(t => t.CharacterName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (claimNames.Count > 0)
        {
            // Unmatched names are shown, not hidden -- they are usually a roster typo an officer
            // can fix -- but marked, because they earn nothing. Same transparency the in-app Claim
            // Shield panel gives them with its "not on roster" tag.
            var claimText = string.Join(", ", claimNames.Select(t => t.Matched
                ? Escape(t.CharacterName)
                : $"{Escape(t.CharacterName)} (not on roster)"));

            fields.Add(new
            {
                name = $"Claim Shield ({claimNames.Count(t => t.Matched)})",
                // Discord caps a field value at 1024.
                value = Truncate(claimText, 1024),
                inline = false,
            });
        }

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
