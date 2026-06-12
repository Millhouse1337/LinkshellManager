using LinkshellManagerDiscordApp.Models;

namespace LinkshellManagerDiscordApp.Services;

// Posts/edits an event's Discord board. A party board is a wide, readable embed
// (parties as fields) with the rendered PNG (EventBoardHtmlBuilder →
// EventBoardImageRenderer) shown inside it — the embed fills the message column
// while a bare image attachment is capped narrow by Discord. If the renderer is
// unavailable it falls back to the same embed without the image so events always
// post. Ad-hoc (no party setup) events are the plain embed. All are classic
// messages, so an edit can switch between the image-embed and the plain embed
// without Discord rejecting a Components-V2 flag toggle.
//
// Centralised here so the publisher (initial post + detail-edit refresh) and the
// queue-driven signup refreshes go through identical logic.
public sealed class EventBoardPoster
{
    private readonly DiscordBotClient _bot;
    private readonly EventBoardImageRenderer _renderer;

    public EventBoardPoster(DiscordBotClient bot, EventBoardImageRenderer renderer)
    {
        _bot = bot;
        _renderer = renderer;
    }

    // Posts a new board, returning the new message id (or null on failure).
    // boardTheme is the linkshell's chosen palette key (null → default).
    public Task<string?> PostAsync(
        string channelId, Event ev, IReadOnlyList<EventSignupLine> signups,
        IReadOnlyDictionary<int, EventPartySlotSignup>? slotSignups, CancellationToken cancellationToken,
        string? boardTheme = null)
        => SendAsync(channelId, null, ev, signups, slotSignups, boardTheme, cancellationToken);

    // Edits the existing board message in place. Returns false on failure.
    public async Task<bool> EditAsync(
        string channelId, string messageId, Event ev, IReadOnlyList<EventSignupLine> signups,
        IReadOnlyDictionary<int, EventPartySlotSignup>? slotSignups, CancellationToken cancellationToken,
        string? boardTheme = null)
        => await SendAsync(channelId, messageId, ev, signups, slotSignups, boardTheme, cancellationToken) is not null;

    // messageId null → post; non-null → edit. Returns the message id on success.
    private async Task<string?> SendAsync(
        string channelId, string? messageId, Event ev, IReadOnlyList<EventSignupLine> signups,
        IReadOnlyDictionary<int, EventPartySlotSignup>? slotSignups, string? boardTheme,
        CancellationToken cancellationToken)
    {
        // Party board → render the image; on any render failure, fall through to the
        // text embed so the post/edit still happens.
        if (ev.PartySetup is not null && slotSignups is not null)
        {
            var html = EventBoardHtmlBuilder.Build(ev, ev.PartySetup, slotSignups, signups, boardTheme);
            var png = await _renderer.RenderAsync(html, cancellationToken);
            if (png is not null)
            {
                var fileName = $"event-{ev.Id}-board.png";
                var payload = DiscordEventMessageBuilder.BuildBoardImageEmbedMessage(
                    ev, signups, ev.PartySetup, slotSignups, fileName);
                if (messageId is null)
                {
                    return await _bot.PostMessageWithImageAsync(channelId, payload, png, fileName, cancellationToken);
                }
                return await _bot.EditMessageWithImageAsync(channelId, messageId, payload, png, fileName, cancellationToken)
                    ? messageId : null;
            }
        }

        var json = DiscordEventMessageBuilder.Build(ev, signups, ev.PartySetup, slotSignups);
        if (messageId is null)
        {
            return await _bot.PostMessageAsync(channelId, json, cancellationToken);
        }
        return await _bot.EditMessageAsync(channelId, messageId, json, cancellationToken) ? messageId : null;
    }
}
