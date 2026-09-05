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
    private readonly HnmBoardNoticeService _hnmBoardNotice;
    private readonly ILogger<DiscordEventChannelPublisher> _logger;

    public DiscordEventChannelPublisher(
        ApplicationDbContext db,
        DiscordBotClient bot,
        EventBoardPoster poster,
        ChannelRouteResolver routes,
        HnmBoardNoticeService hnmBoardNotice,
        ILogger<DiscordEventChannelPublisher> logger)
    {
        _db = db;
        _bot = bot;
        _poster = poster;
        _routes = routes;
        _hnmBoardNotice = hnmBoardNotice;
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

            // Components V2 board mode for this linkshell (experimental). Read once and
            // threaded through every post/edit for this board so the V2 flag stays
            // consistent across the board's whole lifecycle (Discord rejects toggling it
            // on edit). Flipping the linkshell toggle mid-lifecycle of a live board is
            // unsupported — it only takes effect for boards posted afterwards.
            var useV2 = ev.Linkshell?.UseComponentsV2Boards ?? false;

            // HNM board marked "defeated / awaiting re-post" (its ToD was just logged):
            // replace the board with the "monster down" note instead of rendering signups.
            // Making the publisher the single renderer means the save-hook re-render that
            // fires for this same edit can't race a separate defeated-note edit. No-op if
            // the board was never posted (nothing to edit in Discord).
            if (ev.HnmDefeatedAt != null)
            {
                await _hnmBoardNotice.PostDefeatedNoticeAsync(
                    ev, useV2, cancellationToken,
                    await ResolveCampArchiveIdAsync(ev.Id, cancellationToken));

                // SAVE. The notice DELETES a wide board's extra messages from Discord (the note
                // replaces the first; the rest have to go, buttons and all) and clears
                // DiscordExtraMessageIds to match -- but it only stages that clear, and this was
                // returning without saving. So Discord and the database disagreed: the messages
                // were gone while the event still listed their ids, which is the state a live
                // camp was found in.
                //
                // Everything downstream reads those ids. SendWideAsync heals on the next re-post
                // (an edit to a deleted message falls through to a fresh post), but it heals by
                // POSTING, so the alliances it re-creates land at the bottom of the channel
                // instead of beside the board -- and any path that only edits finds nothing there
                // at all.
                await _db.SaveChangesAsync(cancellationToken);

                _logger.LogInformation("Event {EventId} board set to defeated note (message {MessageId}).",
                    eventId, ev.DiscordMessageId);
                return;
            }

            var signups = await LoadSignupsAsync(ev.Id, cancellationToken);
            var slotSignups = ev.PartySetup is null
                ? null
                : await EventPartySignupService.GetSignupsForEventAsync(_db, ev.Id, cancellationToken);

            // A wide board is a SET of messages, one per alliance, so its whole lifecycle goes
            // through SendWideAsync — which edits each in place, posts any the setup gained, and
            // deletes any it lost. Posting and editing are the same call here because matching
            // messages to alliances by position makes "post" just "edit nothing that exists yet".
            var wide = useV2 && ev.PartySetup is not null && slotSignups is not null;
            if (wide)
            {
                var channel = ev.DiscordChannelId;
                if (string.IsNullOrEmpty(channel))
                {
                    channel = await _routes.ResolveEventChannelIdAsync(
                        ev.LinkshellId, ev.EventType, ev.AssignedMonsterName, cancellationToken);
                    if (string.IsNullOrEmpty(channel))
                    {
                        LogNoRoute(eventId, ev);
                        return;
                    }
                }

                var posted = await _poster.SendWideAsync(
                    channel!, ev, signups, slotSignups!, BoardMessageIds(ev), cancellationToken,
                    await LoadClaimShieldAsync(ev.Id, cancellationToken));
                if (posted is null || posted.Count == 0)
                {
                    _logger.LogWarning(
                        "Event {EventId} board: the bot failed to post to channel {ChannelId} (linkshell {LinkshellId}). " +
                        "Check the bot is a member of the server and has the \"Send Messages\" permission there.",
                        eventId, channel, ev.LinkshellId);
                    return;
                }

                ev.DiscordChannelId = channel;
                ev.DiscordMessageId = posted[0];
                ev.DiscordExtraMessageIds = posted.Count > 1
                    ? string.Join(',', posted.Skip(1))
                    : null;
                await _db.SaveChangesAsync(cancellationToken);
                _logger.LogInformation(
                    "Event {EventId} board is {Count} message(s) in channel {ChannelId}.",
                    eventId, posted.Count, channel);
                return;
            }

            // Already announced → edit the posted message in place so detail edits
            // (name, time, DKP/hour, location, party setup) AND signup changes show,
            // without waiting for a signup interaction to refresh it.
            if (!string.IsNullOrEmpty(ev.DiscordMessageId))
            {
                if (string.IsNullOrEmpty(ev.DiscordChannelId))
                {
                    return;
                }
                await _poster.EditAsync(ev.DiscordChannelId, ev.DiscordMessageId, ev, signups, slotSignups, cancellationToken, ev.Linkshell?.EventBoardTheme, useV2);
                _logger.LogInformation(
                    "Event {EventId} board updated in place (message {MessageId}).",
                    eventId, ev.DiscordMessageId);
                return;
            }

            var channelId = await _routes.ResolveEventChannelIdAsync(ev.LinkshellId, ev.EventType, ev.AssignedMonsterName, cancellationToken);
            if (string.IsNullOrEmpty(channelId))
            {
                LogNoRoute(eventId, ev);
                return;
            }

            var messageId = await _poster.PostAsync(channelId, ev, signups, slotSignups, cancellationToken, ev.Linkshell?.EventBoardTheme, useV2);
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

    // Every message this board occupies, in display order: the first id plus the continuation
    // ids a wide (one-message-per-alliance) board carries.
    private static IReadOnlyList<string> BoardMessageIds(Event ev)
    {
        var ids = new List<string>();
        if (!string.IsNullOrWhiteSpace(ev.DiscordMessageId)) { ids.Add(ev.DiscordMessageId!); }
        if (!string.IsNullOrWhiteSpace(ev.DiscordExtraMessageIds))
        {
            ids.AddRange(ev.DiscordExtraMessageIds!
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        }
        return ids;
    }

    private void LogNoRoute(int eventId, Event ev)
        => _logger.LogInformation(
            "Event {EventId} not announced: linkshell {LinkshellId} has no Discord channel route that posts " +
            "events for type \"{EventType}\" (and no unfiltered event route). Set one under " +
            "Linkshell → Configurations → Discord channel routes.",
            eventId, ev.LinkshellId, ev.EventType ?? "(none)");

    // The PAST EVENT this camp was just archived as, for the "View Camp Details" button on the
    // defeated note.
    //
    // Found through the camp's review row rather than by matching event names and dates: End Camp
    // stages both together and links them (WindowEvent.CampEventHistoryId), so this is the camp's
    // own answer rather than a guess. Newest first, because a recycled board has ended several
    // times and the note is about the pop that just finished.
    //
    // Null for a camp that ended with nobody on it (no review row, no archive) and for any camp
    // ended before the archive moved to End Camp. The button is simply not drawn.
    private async Task<int?> ResolveCampArchiveIdAsync(int eventId, CancellationToken cancellationToken)
        => await _db.WindowEvents
            .AsNoTracking()
            .Where(w => w.SourceEventId == eventId && w.CampEventHistoryId != null)
            .OrderByDescending(w => w.CampEndedAtUtc)
            .ThenByDescending(w => w.Id)
            .Select(w => w.CampEventHistoryId)
            .FirstOrDefaultAsync(cancellationToken);

    // The camp's Claim Shield lotteries, for the block at the foot of the last board message.
    //
    // Read on every board render rather than only when a capture arrives: a board refreshes for
    // any number of reasons (a signup, a window turning over, an officer editing the setup), and
    // one that quietly dropped the Claim Shield block on the next unrelated refresh would look
    // exactly like the capture had been lost.
    private async Task<List<ClaimShieldBoardCapture>> LoadClaimShieldAsync(
        int eventId, CancellationToken cancellationToken)
        => await _db.ClaimShieldCaptures
            .AsNoTracking()
            .Where(capture => capture.EventId == eventId)
            .OrderByDescending(capture => capture.CapturedAtUtc)
            .Select(capture => new ClaimShieldBoardCapture(
                capture.MonsterName,
                capture.Won,
                capture.TotalPlayers,
                capture.CapturedAtUtc,
                capture.Members
                    .OrderBy(member => member.Id)
                    .Select(member => new ClaimShieldBoardMember(member.CharacterName, member.Matched))
                    .ToList()))
            .ToListAsync(cancellationToken);

    private async Task<List<EventSignupLine>> LoadSignupsAsync(int eventId, CancellationToken cancellationToken)
    {
        var rows = await _db.AppUserEvents
            .AsNoTracking()
            .Where(signup => signup.EventId == eventId)
            .OrderBy(signup => signup.CharacterName)
            .Select(signup => new
            {
                signup.CharacterName, signup.JobName, signup.SubJobName, signup.JobType, signup.WdArrivalWindow,
                signup.EnfeebReady, signup.ResistReady, signup.RelicWeapon,
            })
            .ToListAsync(cancellationToken);

        return rows
            .Select(row => new EventSignupLine(
                row.CharacterName ?? "Unknown", row.JobName, row.SubJobName, row.JobType, row.WdArrivalWindow,
                row.EnfeebReady, row.ResistReady, row.RelicWeapon))
            .ToList();
    }
}
