using LinkshellManagerDiscordApp.Data;
using LinkshellManagerDiscordApp.Models;
using Microsoft.EntityFrameworkCore;

namespace LinkshellManagerDiscordApp.Services;

// Posts (and keeps refreshed, edit-in-place) the linkshell's live DKP sheet to a Discord
// channel: the totals and a paged member table as text embeds (DkpSheetMessageBuilder), AND
// the complete .xlsx attached for download. A roster past ~95 members runs on into more
// messages; their ids are kept on the route beside the first. Driven off the app's own DKP (DkpSheetService) —
// no Google. The target channel is the linkshell's channel route with the "DKP sheet" flag
// (ChannelRouteResolver), mirroring the ToD board; no-op when no such route is configured or
// the bot isn't configured. Best-effort: failures are logged, never thrown, so a Discord
// hiccup can't break the DKP write that triggered it.
public sealed class DiscordDkpSheetPublisher
{
    private const string XlsxContentType =
        "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";

    private readonly ApplicationDbContext _db;
    private readonly DkpSheetService _dkpSheet;
    private readonly ChannelRouteResolver _routes;
    private readonly DiscordBotClient _bot;
    private readonly ILogger<DiscordDkpSheetPublisher> _logger;

    public DiscordDkpSheetPublisher(
        ApplicationDbContext db,
        DkpSheetService dkpSheet,
        ChannelRouteResolver routes,
        DiscordBotClient bot,
        ILogger<DiscordDkpSheetPublisher> logger)
    {
        _db = db;
        _dkpSheet = dkpSheet;
        _routes = routes;
        _bot = bot;
        _logger = logger;
    }

    public async Task PublishAsync(int linkshellId, CancellationToken cancellationToken)
    {
        if (!_bot.IsConfigured)
        {
            return;
        }

        if (!await _db.Linkshells.AnyAsync(l => l.Id == linkshellId, cancellationToken))
        {
            return;
        }

        // The DKP sheet posts to the route flagged "DKP sheet" (tracked, so we can
        // read/write its live message id). No route → feature off for this linkshell.
        var route = await _routes.ResolveRouteAsync(linkshellId, ChannelPostTypes.DkpSheet, cancellationToken);
        if (route is null || string.IsNullOrWhiteSpace(route.ChannelId))
        {
            return;
        }

        var channelId = route.ChannelId;
        var data = await _dkpSheet.BuildAsync(linkshellId, cancellationToken);

        var xlsxName = DkpWorkbookBuilder.FileName(data.LinkshellName, DateTime.UtcNow);
        var payloads = DkpSheetMessageBuilder.BuildPayloads(data, xlsxName);
        var files = new List<DiscordBotClient.OutgoingFile> { new(DkpWorkbookBuilder.Build(data), xlsxName, XlsxContentType) };

        // The post's messages so far, in channel order: the first, then any it ran on into.
        var existing = new List<string>();
        if (!string.IsNullOrEmpty(route.DkpSheetMessageId))
        {
            existing.Add(route.DkpSheetMessageId!);
            existing.AddRange(SplitIds(route.DkpSheetExtraMessageIds));
        }

        // Edit the existing messages in place. Only repost when one is genuinely gone (deleted,
        // channel changed): then every message is deleted and the whole post goes up again, so
        // the pages stay in order. On a transient Discord error keep the stored ids, so the next
        // DKP change retries the edits idempotently instead of orphaning messages and uploading a
        // duplicate .xlsx.
        var ids = new List<string>();
        var repost = existing.Count == 0;
        for (var i = 0; i < existing.Count && i < payloads.Count && !repost; i++)
        {
            var edit = i == 0
                ? await _bot.EditMessageWithFilesAsync(channelId, existing[0], payloads[0], files, cancellationToken)
                : await _bot.EditMessageAsync(channelId, existing[i], payloads[i], cancellationToken);
            if (edit == DiscordEditResult.TransientFailure)
            {
                return;
            }
            if (edit == DiscordEditResult.MessageGone)
            {
                repost = true;
            }
            else
            {
                ids.Add(existing[i]);
            }
        }

        if (repost)
        {
            foreach (var id in existing)
            {
                await _bot.DeleteMessageAsync(channelId, id, cancellationToken);
            }
            ids.Clear();
        }
        else
        {
            // A shorter roster needs fewer messages: delete the spare ones from the end.
            foreach (var id in existing.Skip(payloads.Count))
            {
                await _bot.DeleteMessageAsync(channelId, id, cancellationToken);
            }
        }

        // Post whatever isn't on the channel yet: everything on a repost, or the new pages at
        // the bottom when the roster grew past what the existing messages hold.
        for (var i = ids.Count; i < payloads.Count; i++)
        {
            var messageId = i == 0
                ? await _bot.PostMessageWithFilesAsync(channelId, payloads[0], files, cancellationToken)
                : await _bot.PostMessageAsync(channelId, payloads[i], cancellationToken);
            if (string.IsNullOrEmpty(messageId))
            {
                _logger.LogWarning(
                    "DKP sheet for linkshell {LinkshellId}: the bot failed to post to channel {ChannelId} " +
                    "(check the bot is in the server with Send Messages).",
                    linkshellId, channelId);
                break;
            }
            ids.Add(messageId.Length > 32 ? messageId[..32] : messageId);
        }

        route.DkpSheetMessageId = ids.Count > 0 ? ids[0] : null;
        route.DkpSheetExtraMessageIds = ids.Count > 1 ? string.Join(',', ids.Skip(1)) : null;
        if (_db.ChangeTracker.HasChanges())
        {
            await _db.SaveChangesAsync(cancellationToken);
        }
    }

    private static IEnumerable<string> SplitIds(string? ids)
        => (ids ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
