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
    // useComponentsV2 mirrors the board's posted mode: a V2 board can't be edited with a
    // classic payload (Discord forbids dropping the V2 flag on edit), so the note is sent as
    // a V2 message too when the board was posted that way.
    //
    // eventHistoryId is the camp's PAST EVENT — the archive written at End Camp. It puts a
    // "View Camp Details" button on this note, which is the only way back to a camp once its
    // board is gone: the roster, the DKP, the loot and the ToD all live on a page nobody in
    // Discord has a link to, and the board that used to carry them has just been replaced by
    // these three lines. Null leaves the note exactly as it was.
    public async Task<bool> PostDefeatedNoticeAsync(
        Event ev, bool useComponentsV2, CancellationToken cancellationToken,
        int? eventHistoryId = null)
    {
        if (string.IsNullOrWhiteSpace(ev.DiscordChannelId) || string.IsNullOrWhiteSpace(ev.DiscordMessageId))
        {
            return false;
        }

        var monster = !string.IsNullOrWhiteSpace(ev.AssignedMonsterName)
            ? ev.AssignedMonsterName!
            : (!string.IsNullOrWhiteSpace(ev.EventName) ? ev.EventName! : "The monster");

        // Discord <t:unix:D/T/R> renders each viewer's local time + a live "in N hours".
        // D + T ("August 26, 2026" + "6:07:35 PM") rather than the single F style: F is the only
        // style that carries the weekday but it stops at minutes, and a repop time is ToD math —
        // the seconds matter more here than the weekday does.
        var description = new StringBuilder();
        if (ev.StartTime.HasValue)
        {
            var repop = ToUnix(ev.StartTime.Value);
            description.Append($"Will repop <t:{repop}:D> <t:{repop}:T> (<t:{repop}:R>).");
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

        var title = $"💀 {monster} defeated";
        object payload = useComponentsV2
            ? DiscordEventMessageBuilder.BuildV2DefeatedNoticeMessage(
                title, description.ToString(), eventHistoryId)
            : new
            {
                content = string.Empty,
                embeds = new[]
                {
                    new
                    {
                        title,
                        description = description.ToString(),
                        color = 0x6B7280
                    }
                },
                // This REPLACES the board's sign-up buttons rather than adding to them (Discord
                // keeps fields that are omitted, so the array must be sent explicitly). It is
                // empty unless the camp has an archive to open, which is the pre-existing
                // behaviour for a camp nobody attended.
                components = DiscordEventMessageBuilder.BuildDefeatedNoticeComponents(eventHistoryId),
                attachments = Array.Empty<object>()
            };

        // Intentionally collapses the 3-way result to success/not — this is a one-shot
        // "defeated" edit, not a kept-alive board, so transient vs gone are treated the same.
        var ok = await _bot.EditMessageAsync(ev.DiscordChannelId!, ev.DiscordMessageId!, payload, cancellationToken)
            == DiscordEditResult.Edited;
        if (!ok)
        {
            _logger.LogWarning(
                "Failed to edit HNM board message {MessageId} to a defeated note for event {EventId}.",
                ev.DiscordMessageId, ev.Id);
        }

        // A wide board is one message per alliance. The note replaces the FIRST; the rest have to
        // go, or the channel keeps showing a live-looking roster for a monster that is already
        // down — buttons and all.
        if (!string.IsNullOrWhiteSpace(ev.DiscordExtraMessageIds))
        {
            foreach (var extra in ev.DiscordExtraMessageIds!
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                await _bot.DeleteMessageAsync(ev.DiscordChannelId!, extra, cancellationToken);
            }
            ev.DiscordExtraMessageIds = null;
        }

        return ok;
    }

    private static long ToUnix(DateTime utc) =>
        new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc)).ToUnixTimeSeconds();
}
