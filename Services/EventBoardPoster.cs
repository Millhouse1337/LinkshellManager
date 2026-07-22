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
    // useComponentsV2 posts a Components V2 board (wide media-gallery card) instead of the
    // classic image-in-embed; the whole board lifecycle must keep the mode it posted with
    // (Discord forbids toggling the V2 flag on edit), so the caller passes the same value on
    // every EditAsync for that message.
    public Task<string?> PostAsync(
        string channelId, Event ev, IReadOnlyList<EventSignupLine> signups,
        IReadOnlyDictionary<int, EventPartySlotSignup>? slotSignups, CancellationToken cancellationToken,
        string? boardTheme = null, bool useComponentsV2 = false)
        => SendAsync(channelId, null, ev, signups, slotSignups, boardTheme, useComponentsV2, cancellationToken);

    // Edits the existing board message in place. Returns false on failure.
    public async Task<bool> EditAsync(
        string channelId, string messageId, Event ev, IReadOnlyList<EventSignupLine> signups,
        IReadOnlyDictionary<int, EventPartySlotSignup>? slotSignups, CancellationToken cancellationToken,
        string? boardTheme = null, bool useComponentsV2 = false)
        => await SendAsync(channelId, messageId, ev, signups, slotSignups, boardTheme, useComponentsV2, cancellationToken) is not null;

    // messageId null → post; non-null → edit. Returns the message id on success.
    private async Task<string?> SendAsync(
        string channelId, string? messageId, Event ev, IReadOnlyList<EventSignupLine> signups,
        IReadOnlyDictionary<int, EventPartySlotSignup>? slotSignups, string? boardTheme,
        bool useComponentsV2, CancellationToken cancellationToken)
    {
        // Party board → render the image; on any render failure, fall through to the
        // text board so the post/edit still happens. In V2 mode BOTH the image board and
        // the text fallback are V2-flagged (Discord won't let an edit change the flag), so
        // the fallback must NOT drop to the classic Build()/EditMessageAsync path below.
        if (ev.PartySetup is not null && slotSignups is not null)
        {
            // Wider internal canvas only in V2 mode, so classic boards render byte-identically.
            var html = EventBoardHtmlBuilder.Build(ev, ev.PartySetup, slotSignups, signups, boardTheme, wide: useComponentsV2);
            var png = await _renderer.RenderAsync(html, cancellationToken, wide: useComponentsV2);
            if (png is not null)
            {
                var fileName = $"event-{ev.Id}-board.png";
                var payload = useComponentsV2
                    ? DiscordEventMessageBuilder.BuildBoardImageV2Message(ev, signups, ev.PartySetup, slotSignups, fileName)
                    : DiscordEventMessageBuilder.BuildBoardImageEmbedMessage(ev, signups, ev.PartySetup, slotSignups, fileName);
                if (messageId is null)
                {
                    return await _bot.PostMessageWithImageAsync(channelId, payload, png, fileName, cancellationToken);
                }
                return await _bot.EditMessageWithImageAsync(channelId, messageId, payload, png, fileName, cancellationToken)
                    ? messageId : null;
            }

            // Render failed. In V2 mode send the V2 text fallback (still V2-flagged) rather
            // than the classic embed, which would fail the edit by dropping the V2 flag.
            if (useComponentsV2)
            {
                var v2Fallback = DiscordEventMessageBuilder.BuildBoardV2FallbackMessage(ev, signups, ev.PartySetup, slotSignups);
                if (messageId is null)
                {
                    return await _bot.PostMessageAsync(channelId, v2Fallback, cancellationToken);
                }
                return await _bot.EditMessageAsync(channelId, messageId, v2Fallback, cancellationToken) == DiscordEditResult.Edited
                    ? messageId : null;
            }
        }

        var json = DiscordEventMessageBuilder.Build(ev, signups, ev.PartySetup, slotSignups);
        if (messageId is null)
        {
            return await _bot.PostMessageAsync(channelId, json, cancellationToken);
        }
        // Intentionally collapses the 3-way result to success/not — unlike DKP/ToD this
        // path doesn't keep a long-lived message id to protect, so a transient failure and a
        // gone message are handled the same (return null → caller reposts).
        return await _bot.EditMessageAsync(channelId, messageId, json, cancellationToken) == DiscordEditResult.Edited
            ? messageId : null;
    }
}
