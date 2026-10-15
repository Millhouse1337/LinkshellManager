using LinkshellManagerDiscordApp.Models;

namespace LinkshellManagerDiscordApp.Services;

// Posts/edits an event's Discord board: the text embed from DiscordEventMessageBuilder.Build
// (the party board when the event has a party setup, else the ad-hoc roster). A classic
// message — no Components V2 flag — with the board buttons below it.
//
// Centralised here so the publisher (initial post + detail-edit refresh) and the
// queue-driven signup refreshes go through identical logic.
public sealed class EventBoardPoster
{
    private readonly DiscordBotClient _bot;

    public EventBoardPoster(DiscordBotClient bot)
    {
        _bot = bot;
    }

    // Posts a new board, returning the new message id (or null on failure).
    public Task<string?> PostAsync(
        string channelId, Event ev, IReadOnlyList<EventSignupLine> signups,
        IReadOnlyDictionary<int, EventPartySlotSignup>? slotSignups, CancellationToken cancellationToken)
        => _bot.PostMessageAsync(
            channelId, DiscordEventMessageBuilder.Build(ev, signups, ev.PartySetup, slotSignups), cancellationToken);

    // Edits the existing board message in place. The three-way result lets the caller tell a
    // board that is gone (or can't take this payload) from a passing Discord hiccup.
    public Task<DiscordEditResult> EditAsync(
        string channelId, string messageId, Event ev, IReadOnlyList<EventSignupLine> signups,
        IReadOnlyDictionary<int, EventPartySlotSignup>? slotSignups, CancellationToken cancellationToken)
        => _bot.EditMessageAsync(
            channelId, messageId, DiscordEventMessageBuilder.Build(ev, signups, ev.PartySetup, slotSignups),
            cancellationToken);
}
