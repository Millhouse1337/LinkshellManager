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
    private readonly ILogger<DiscordEventChannelPublisher> _logger;

    public DiscordEventChannelPublisher(
        ApplicationDbContext db,
        DiscordBotClient bot,
        ILogger<DiscordEventChannelPublisher> logger)
    {
        _db = db;
        _bot = bot;
        _logger = logger;
    }

    public async Task HandleAsync(int eventId, CancellationToken cancellationToken)
    {
        if (!_bot.IsConfigured)
        {
            return;
        }

        try
        {
            var ev = await _db.Events
                .FirstOrDefaultAsync(item => item.Id == eventId, cancellationToken);
            if (ev is null || !string.IsNullOrEmpty(ev.DiscordMessageId))
            {
                // Missing, or already announced (idempotent).
                return;
            }

            var channel = await ResolveChannelAsync(ev.LinkshellId, ev.EventType, cancellationToken);
            if (channel is null)
            {
                return; // No channel configured for this event's type.
            }

            var signups = await LoadSignupsAsync(eventId, cancellationToken);
            var payload = DiscordEventMessageBuilder.Build(ev, signups);

            var messageId = await _bot.PostMessageAsync(channel.ChannelId, payload, cancellationToken);
            if (string.IsNullOrEmpty(messageId))
            {
                return;
            }

            ev.DiscordChannelId = channel.ChannelId;
            ev.DiscordMessageId = messageId;
            await _db.SaveChangesAsync(cancellationToken);
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

    // The channel for the event's type, falling back to the general "Other
    // events" channel when no type-specific one is set.
    private async Task<LinkshellDiscordChannel?> ResolveChannelAsync(
        int linkshellId, string? eventType, CancellationToken cancellationToken)
    {
        var rows = await _db.LinkshellDiscordChannels
            .AsNoTracking()
            .Where(channel => channel.LinkshellId == linkshellId && channel.ChannelId != "")
            .ToListAsync(cancellationToken);
        if (rows.Count == 0)
        {
            return null;
        }

        var purpose = DiscordChannelPurposes.ForEventType(eventType);
        return rows.FirstOrDefault(channel => channel.Purpose == purpose)
            ?? rows.FirstOrDefault(channel => channel.Purpose == DiscordChannelPurposes.Events);
    }

    private async Task<List<EventSignupLine>> LoadSignupsAsync(int eventId, CancellationToken cancellationToken)
    {
        var rows = await _db.AppUserEvents
            .AsNoTracking()
            .Where(signup => signup.EventId == eventId)
            .OrderBy(signup => signup.CharacterName)
            .Select(signup => new { signup.CharacterName, signup.JobName })
            .ToListAsync(cancellationToken);

        return rows
            .Select(row => new EventSignupLine(row.CharacterName ?? "Unknown", row.JobName))
            .ToList();
    }
}
