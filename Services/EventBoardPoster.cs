using LinkshellManagerDiscordApp.Models;

namespace LinkshellManagerDiscordApp.Services;

// Posts/edits an event's Discord board, choosing the format: a party board is
// rendered to a PNG (EventBoardHtmlBuilder → EventBoardImageRenderer) and posted
// as an image; if the renderer is unavailable it falls back to the classic text
// embed so events always post. Ad-hoc (no party setup) events are always the text
// embed. Both formats are classic messages, so an edit can switch between image
// and embed without Discord rejecting a Components-V2 flag toggle.
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
    public Task<string?> PostAsync(
        string channelId, Event ev, IReadOnlyList<EventSignupLine> signups,
        IReadOnlyDictionary<int, EventPartySlotSignup>? slotSignups, CancellationToken cancellationToken)
        => SendAsync(channelId, null, ev, signups, slotSignups, cancellationToken);

    // Edits the existing board message in place. Returns false on failure.
    public async Task<bool> EditAsync(
        string channelId, string messageId, Event ev, IReadOnlyList<EventSignupLine> signups,
        IReadOnlyDictionary<int, EventPartySlotSignup>? slotSignups, CancellationToken cancellationToken)
        => await SendAsync(channelId, messageId, ev, signups, slotSignups, cancellationToken) is not null;

    // messageId null → post; non-null → edit. Returns the message id on success.
    private async Task<string?> SendAsync(
        string channelId, string? messageId, Event ev, IReadOnlyList<EventSignupLine> signups,
        IReadOnlyDictionary<int, EventPartySlotSignup>? slotSignups, CancellationToken cancellationToken)
    {
        // Party board → render the image; on any render failure, fall through to the
        // text embed so the post/edit still happens.
        if (ev.PartySetup is not null && slotSignups is not null)
        {
            var html = EventBoardHtmlBuilder.Build(ev, ev.PartySetup, slotSignups, signups);
            var png = await _renderer.RenderAsync(html, cancellationToken);
            if (png is not null)
            {
                var fileName = $"event-{ev.Id}-board.png";
                var payload = DiscordEventMessageBuilder.BuildBoardImageMessage(ev.Id, fileName);
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
