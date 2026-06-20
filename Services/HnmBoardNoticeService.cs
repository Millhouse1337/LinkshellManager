using System.Text;
using LinkshellManagerDiscordApp.Models;

namespace LinkshellManagerDiscordApp.Services;

// Replaces an HNM signup board's already-posted Discord message with a "defeated" note
// (monster down, predicted repop, and when the board auto-re-posts), stripping the signup
// buttons and the board image. Called when an officer logs the monster's Time of Death
// from the board's "Post ToD" button. The recurring-board poller later edits this same
// message back into a fresh signup board for the next pop (one message that cycles).
public sealed class HnmBoardNoticeService
{
    private readonly DiscordBotClient _bot;
    private readonly ILogger<HnmBoardNoticeService> _logger;

    public HnmBoardNoticeService(DiscordBotClient bot, ILogger<HnmBoardNoticeService> logger)
    {
        _bot = bot;
        _logger = logger;
    }

    // Edits the event's posted board message to the defeated note. No-op (returns false) if
    // the event was never posted to Discord (no channel configured). Reads the repop time
    // from ev.StartTime and the re-post time from ev.HnmRepostAt, so callers set those first.
    public async Task<bool> PostDefeatedNoticeAsync(Event ev, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(ev.DiscordChannelId) || string.IsNullOrWhiteSpace(ev.DiscordMessageId))
        {
            return false;
        }

        var monster = !string.IsNullOrWhiteSpace(ev.AssignedMonsterName)
            ? ev.AssignedMonsterName!
            : (!string.IsNullOrWhiteSpace(ev.EventName) ? ev.EventName! : "The monster");

        // Discord <t:unix:F/R> renders each viewer's local time + a live "in N hours".
        var description = new StringBuilder();
        if (ev.StartTime.HasValue)
        {
            var repop = ToUnix(ev.StartTime.Value);
            description.Append($"Will repop <t:{repop}:F> (<t:{repop}:R>).");
        }
        else
        {
            description.Append("Next pop time is being calculated.");
        }
        if (ev.HnmRepostAt.HasValue)
        {
            var repost = ToUnix(ev.HnmRepostAt.Value);
            description.Append($"\n\nThe sign-up board will automatically re-post in this channel <t:{repost}:R>.");
        }

        var payload = new
        {
            content = string.Empty,
            embeds = new[]
            {
                new
                {
                    title = $"💀 {monster} defeated",
                    description = description.ToString(),
                    color = 0x6B7280
                }
            },
            // Empty arrays REPLACE the existing buttons + board image (Discord keeps fields
            // that are omitted, so they must be sent explicitly to clear them).
            components = Array.Empty<object>(),
            attachments = Array.Empty<object>()
        };

        var ok = await _bot.EditMessageAsync(ev.DiscordChannelId!, ev.DiscordMessageId!, payload, cancellationToken);
        if (!ok)
        {
            _logger.LogWarning(
                "Failed to edit HNM board message {MessageId} to a defeated note for event {EventId}.",
                ev.DiscordMessageId, ev.Id);
        }
        return ok;
    }

    private static long ToUnix(DateTime utc) =>
        new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc)).ToUnixTimeSeconds();
}
