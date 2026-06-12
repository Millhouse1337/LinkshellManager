using LinkshellManagerDiscordApp.Data;
using LinkshellManagerDiscordApp.Models;
using Microsoft.EntityFrameworkCore;

namespace LinkshellManagerDiscordApp.Services;

// Announces a newly-created event to the linkshell's Discord channel for that
// event's type (HENM -> HENM channel, KSNM -> KSNM channel, else the general
// "Other events" channel), as a bot message carrying an inline job-signup select
// + Withdraw button. Stores the channel + message id on the Event so the
// interactions endpoint can edit it in place later. Best-effort: any failure is
// logged and dropped; the event itself is unaffected. No-op when the bot token
// isn't configured or no matching channel exists. Mirrors
// DiscordAuctionChannelPublisher's shape.
public sealed class DiscordEventChannelPublisher
{
    private readonly ApplicationDbContext _db;
    private readonly DiscordBotClient _bot;
    private readonly EventBoardPoster _poster;
    private readonly ChannelRouteResolver _routes;
    private readonly ILogger<DiscordEventChannelPublisher> _logger;

    public DiscordEventChannelPublisher(
        ApplicationDbContext db,
        DiscordBotClient bot,
        EventBoardPoster poster,
        ChannelRouteResolver routes,
        ILogger<DiscordEventChannelPublisher> logger)
    {
        _db = db;
        _bot = bot;
        _poster = poster;
        _routes = routes;
        _logger = logger;
    }

    public async Task HandleAsync(int eventId, CancellationToken cancellationToken)
    {
        // Events post via the bot ONLY (no webhook fallback) because the inline
        // job-signup select + Withdraw button are components, which Discord
        // strips from webhook messages. Each skip below is logged so an officer
        // whose event "didn't post to any channel" can see exactly why.
        if (!_bot.IsConfigured)
        {
            _logger.LogInformation(
                "Event {EventId} not announced: the Discord bot token is not configured (DiscordOAuth:BotToken). " +
                "Event channel posting requires the bot — webhooks can't carry the signup buttons.",
                eventId);
            return;
        }

        try
        {
            // Load the linked party setup tree (when any) so the announcement can
            // render the interactive board instead of the ad-hoc job roster.
            var ev = await _db.Events
                .Include(item => item.Linkshell)
                .Include(item => item.PartySetup!)
                    .ThenInclude(ps => ps.Alliances).ThenInclude(a => a.Parties).ThenInclude(p => p.Slots)
                .FirstOrDefaultAsync(item => item.Id == eventId, cancellationToken);
            if (ev is null)
            {
                return;
            }

            var signups = await LoadSignupsAsync(ev.Id, cancellationToken);
            var slotSignups = ev.PartySetup is null
                ? null
                : await EventPartySignupService.GetSignupsForEventAsync(_db, ev.Id, cancellationToken);

            // Already announced → edit the posted message in place so detail edits
            // (name, time, DKP/hour, location, party setup) AND signup changes show,
            // without waiting for a signup interaction to refresh it.
            if (!string.IsNullOrEmpty(ev.DiscordMessageId))
            {
                if (string.IsNullOrEmpty(ev.DiscordChannelId))
                {
                    return;
                }
                await _poster.EditAsync(ev.DiscordChannelId, ev.DiscordMessageId, ev, signups, slotSignups, cancellationToken, ev.Linkshell?.EventBoardTheme);
                _logger.LogInformation(
                    "Event {EventId} board updated in place (message {MessageId}).",
                    eventId, ev.DiscordMessageId);
                return;
            }

            var channelId = await _routes.ResolveEventChannelIdAsync(ev.LinkshellId, ev.EventType, cancellationToken);
            if (string.IsNullOrEmpty(channelId))
            {
                _logger.LogInformation(
                    "Event {EventId} not announced: linkshell {LinkshellId} has no Discord channel route that posts " +
                    "events for type \"{EventType}\" (and no unfiltered event route). Set one under " +
                    "Linkshell → Configurations → Discord channel routes.",
                    eventId, ev.LinkshellId, ev.EventType ?? "(none)");
                return;
            }

            var messageId = await _poster.PostAsync(channelId, ev, signups, slotSignups, cancellationToken, ev.Linkshell?.EventBoardTheme);
            if (string.IsNullOrEmpty(messageId))
            {
                _logger.LogWarning(
                    "Event {EventId} announce: the bot failed to post to channel {ChannelId} (linkshell {LinkshellId}). " +
                    "Check the bot is a member of the server and has the \"Send Messages\" permission in that channel.",
                    eventId, channelId, ev.LinkshellId);
                return;
            }

            ev.DiscordChannelId = channelId;
            ev.DiscordMessageId = messageId;
            await _db.SaveChangesAsync(cancellationToken);
            _logger.LogInformation(
                "Event {EventId} announced to channel {ChannelId} (message {MessageId}).",
                eventId, channelId, messageId);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Event announce for event {EventId} failed.", eventId);
        }
    }

    private async Task<List<EventSignupLine>> LoadSignupsAsync(int eventId, CancellationToken cancellationToken)
    {
        var rows = await _db.AppUserEvents
            .AsNoTracking()
            .Where(signup => signup.EventId == eventId)
            .OrderBy(signup => signup.CharacterName)
            .Select(signup => new { signup.CharacterName, signup.JobName, signup.SubJobName, signup.JobType })
            .ToListAsync(cancellationToken);

        return rows
            .Select(row => new EventSignupLine(row.CharacterName ?? "Unknown", row.JobName, row.SubJobName, row.JobType))
            .ToList();
    }
}
