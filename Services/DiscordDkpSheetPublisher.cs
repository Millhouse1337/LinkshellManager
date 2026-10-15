using LinkshellManagerDiscordApp.Data;
using LinkshellManagerDiscordApp.Models;
using Microsoft.EntityFrameworkCore;

namespace LinkshellManagerDiscordApp.Services;

// Posts (and keeps refreshed, edit-in-place) the linkshell's live DKP sheet to a Discord
// channel: the totals and a paged member table as text embeds (DkpSheetMessageBuilder), AND
// the complete .xlsx attached for download. Driven off the app's own DKP (DkpSheetService) —
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
        var payload = DkpSheetMessageBuilder.BuildPayload(data, xlsxName);
        var files = new List<DiscordBotClient.OutgoingFile> { new(DkpWorkbookBuilder.Build(data), xlsxName, XlsxContentType) };

        // Edit the existing post in place. Only repost when the message is genuinely
        // gone (deleted/channel changed) — on a transient Discord error keep the stored
        // id so the next DKP change retries the edit idempotently instead of orphaning
        // this post and uploading a duplicate .xlsx.
        if (!string.IsNullOrEmpty(route.DkpSheetMessageId))
        {
            var edit = await _bot.EditMessageWithFilesAsync(
                channelId, route.DkpSheetMessageId!, payload, files, cancellationToken);
            if (edit == DiscordEditResult.Edited || edit == DiscordEditResult.TransientFailure)
            {
                return; // edited, or keep the id and retry on the next change
            }
            route.DkpSheetMessageId = null; // MessageGone → post fresh below
        }

        var messageId = await _bot.PostMessageWithFilesAsync(channelId, payload, files, cancellationToken);
        if (string.IsNullOrEmpty(messageId))
        {
            _logger.LogWarning(
                "DKP sheet for linkshell {LinkshellId}: the bot failed to post to channel {ChannelId} " +
                "(check the bot is in the server with Send Messages).",
                linkshellId, channelId);
            if (_db.ChangeTracker.HasChanges())
            {
                await _db.SaveChangesAsync(cancellationToken);
            }
            return;
        }

        route.DkpSheetMessageId = messageId.Length > 32 ? messageId[..32] : messageId;
        await _db.SaveChangesAsync(cancellationToken);
    }
}
