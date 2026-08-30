using LinkshellManagerDiscordApp.Models;

namespace LinkshellManagerDiscordApp.Services;

// Posts/edits an event's Discord board, in one of two modes.
//
// WIDE TEXT (the linkshell's "full-width event boards" setting): the roster is a character-
// aligned column grid in the message's `content`, which is the widest surface Discord gives a
// bot — measured at ~112 monospace characters against ~70 for a Components V2 text component,
// and V2 cannot carry `content` at all. NO image is rendered at all in this mode: the grid
// already shows every slot, job, name and icon, so the PNG was the same roster twice, and
// skipping it keeps headless Chromium off the signup-refresh path entirely.
//
// PICTURE (the setting off): the rendered PNG IS the board, inside an embed, falling back to a
// text embed when the renderer is unavailable so events always post.
//
// Ad-hoc (no party setup) events are the plain embed. Everything here is a CLASSIC message —
// no Components V2 flag anywhere — so an edit can move between these shapes freely.
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
    // useComponentsV2 is misnamed and kept only because the column and the setting are: it
    // selects the WIDE TEXT board — a CLASSIC message whose `content` carries the roster and
    // which renders no image at all. Components V2 was tried and abandoned; its text components
    // are capped at about half the width `content` gets, and V2 forbids `content` outright.
    // Off, the rendered picture is the board, inside an embed Discord caps narrower still.
    //
    // The mode is fixed for a board's whole life — the caller passes the same value on every
    // EditAsync for that message — because the two modes carry different payload shapes.
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

    // Posts or edits the WIDE board, which is one message per alliance. Returns the message ids
    // in display order, or null if nothing could be posted at all.
    //
    // `existing` is what the event already has, first id first. Messages are matched to
    // alliances BY POSITION, so alliance 2 keeps its message when alliance 1's roster changes —
    // editing in place rather than reposting is what stops the board jumping to the bottom of
    // the channel every time somebody signs up.
    //
    // When a setup gains or loses an alliance the count changes: extras are posted at the end,
    // and messages no longer needed are deleted. Deleting the surplus matters — an orphaned
    // board message keeps its buttons, and clicking them would edit a message nothing owns.
    public async Task<IReadOnlyList<string>?> SendWideAsync(
        string channelId, Event ev, IReadOnlyList<EventSignupLine> signups,
        IReadOnlyDictionary<int, EventPartySlotSignup> slotSignups,
        IReadOnlyList<string> existing, CancellationToken cancellationToken)
    {
        var payloads = DiscordEventMessageBuilder.BuildWideBoardMessages(
            ev, signups, ev.PartySetup!, slotSignups);

        var ids = new List<string>(payloads.Count);
        for (var i = 0; i < payloads.Count; i++)
        {
            if (i < existing.Count)
            {
                var edited = await _bot.EditMessageAsync(channelId, existing[i], payloads[i], cancellationToken);
                if (edited == DiscordEditResult.Edited)
                {
                    ids.Add(existing[i]);
                    continue;
                }
                // Gone (deleted by hand, or the channel was purged) — fall through and repost so
                // the board heals itself instead of silently stopping at the missing message.
            }

            var posted = await _bot.PostMessageAsync(channelId, payloads[i], cancellationToken);
            if (string.IsNullOrEmpty(posted))
            {
                // Keep whatever DID post: a partial board is recoverable on the next refresh,
                // whereas dropping the ids would orphan the messages already in the channel.
                return ids.Count > 0 ? ids : null;
            }
            ids.Add(posted!);
        }

        // Surplus messages from a setup that shrank.
        for (var i = payloads.Count; i < existing.Count; i++)
        {
            await _bot.DeleteMessageAsync(channelId, existing[i], cancellationToken);
        }

        return ids;
    }

    // messageId null → post; non-null → edit. Returns the message id on success.
    private async Task<string?> SendAsync(
        string channelId, string? messageId, Event ev, IReadOnlyList<EventSignupLine> signups,
        IReadOnlyDictionary<int, EventPartySlotSignup>? slotSignups, string? boardTheme,
        bool useComponentsV2, CancellationToken cancellationToken)
    {
        var imageUnavailable = false;
        if (ev.PartySetup is not null && slotSignups is not null)
        {
            // WIDE-TEXT MODE is handled by SendWideAsync, which owns a SET of messages (one per
            // alliance) rather than one. The publisher calls it directly, so reaching here in
            // that mode would mean a caller bypassed it.
            if (useComponentsV2)
            {
                throw new InvalidOperationException(
                    "Wide-text boards span several messages — use SendWideAsync, not PostAsync/EditAsync.");
            }

            // CLASSIC MODE: the picture IS the board. Render it; on any failure fall through to
            // the text embed below so the post/edit still happens.
            //
            // The canvas is sized from the board's own content, never from the mode flag: a
            // wider canvas only makes Discord squeeze the image harder. See CardWidthFor.
            var cardWidth = EventBoardHtmlBuilder.CardWidthFor(ev.PartySetup);
            var html = EventBoardHtmlBuilder.Build(ev, ev.PartySetup, slotSignups, signups, boardTheme);
            var png = await _renderer.RenderAsync(html, cancellationToken, cardWidth);
            var fileName = $"event-{ev.Id}-board.png";

            if (png is not null)
            {
                var payload = DiscordEventMessageBuilder.BuildBoardImageEmbedMessage(
                    ev, signups, ev.PartySetup, slotSignups, fileName);
                if (messageId is null)
                {
                    return await _bot.PostMessageWithImageAsync(channelId, payload, png, fileName, cancellationToken);
                }
                return await _bot.EditMessageWithImageAsync(channelId, messageId, payload, png, fileName, cancellationToken)
                    ? messageId : null;
            }

            // Render failed — this board is about to degrade to the narrow embed, so say so.
            imageUnavailable = true;
        }

        var json = DiscordEventMessageBuilder.Build(ev, signups, ev.PartySetup, slotSignups, imageUnavailable);
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
