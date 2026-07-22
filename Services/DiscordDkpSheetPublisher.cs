using System.Text;
using LinkshellManagerDiscordApp.Data;
using LinkshellManagerDiscordApp.Models;
using Microsoft.EntityFrameworkCore;

namespace LinkshellManagerDiscordApp.Services;

// Posts (and keeps refreshed, edit-in-place) the linkshell's live DKP sheet to a
// Discord channel: a wide, themed EMBED carrying the rendered DKP-board PNG
// (DkpSheetBoardHtmlBuilder → EventBoardImageRenderer, the same "Esports HUD" card
// family as the event/auction boards), AND the complete .xlsx attached for
// download. When the image renderer is unavailable it falls back to the classic
// monospace-table embed so the sheet always posts. Driven off the app's own DKP
// (DkpSheetService) — no Google. The target channel is the linkshell's channel route
// with the "DKP sheet" flag (ChannelRouteResolver), mirroring the ToD board; no-op
// when no such route is configured or the bot isn't configured. Best-effort: failures
// are logged, never thrown, so a Discord hiccup can't break the DKP write that
// triggered it.
public sealed class DiscordDkpSheetPublisher
{
    private const int EmbedColor = 0x5865F2; // Discord blurple.
    // Embed description hard limit is 4096; leave headroom for the title/fences.
    private const int MaxTableChars = 3600;
    // Discord allows at most 10 embeds AND 10 attachments per message. With one attachment
    // reserved for the .xlsx, that leaves 9 page-images (9 × RowsPerPage rows shown in
    // channel); the rest fall to the .xlsx, noted in the last page's footer.
    private const int MaxPages = 9;
    private const string XlsxContentType =
        "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";
    private const string PngContentType = "image/png";

    private readonly ApplicationDbContext _db;
    private readonly DkpSheetService _dkpSheet;
    private readonly ChannelRouteResolver _routes;
    private readonly EventBoardImageRenderer _renderer;
    private readonly DiscordBotClient _bot;
    private readonly ILogger<DiscordDkpSheetPublisher> _logger;

    public DiscordDkpSheetPublisher(
        ApplicationDbContext db,
        DkpSheetService dkpSheet,
        ChannelRouteResolver routes,
        EventBoardImageRenderer renderer,
        DiscordBotClient bot,
        ILogger<DiscordDkpSheetPublisher> logger)
    {
        _db = db;
        _dkpSheet = dkpSheet;
        _routes = routes;
        _renderer = renderer;
        _bot = bot;
        _logger = logger;
    }

    public async Task PublishAsync(int linkshellId, CancellationToken cancellationToken)
    {
        if (!_bot.IsConfigured)
        {
            return;
        }

        var linkshell = await _db.Linkshells.FirstOrDefaultAsync(l => l.Id == linkshellId, cancellationToken);
        if (linkshell is null)
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
        var workbook = DkpWorkbookBuilder.Build(data);

        // A baked PNG can't scroll, so a big roster is split across several page-images
        // posted as stacked embeds. Cap the page count so total attachments (page PNGs +
        // the one .xlsx) and embeds stay within Discord's per-message limit of 10; anything
        // past the last page is noted in its footer and lives in the always-attached .xlsx.
        var pageCount = data.Members.Count == 0
            ? 1
            : Math.Min(MaxPages, (data.Members.Count + DkpSheetBoardHtmlBuilder.RowsPerPage - 1) / DkpSheetBoardHtmlBuilder.RowsPerPage);
        var shownCount = Math.Min(data.Members.Count, pageCount * DkpSheetBoardHtmlBuilder.RowsPerPage);
        var omittedBeyond = data.Members.Count - shownCount;

        // Render each page to its own themed PNG. If ANY page fails to render, fall through
        // to the single monospace-table embed so the sheet still posts. The .xlsx is always
        // attached for download in both cases.
        var pageImages = new List<DiscordBotClient.OutgoingFile>(pageCount);
        for (var p = 0; p < pageCount; p++)
        {
            var rows = data.Members
                .Skip(p * DkpSheetBoardHtmlBuilder.RowsPerPage)
                .Take(DkpSheetBoardHtmlBuilder.RowsPerPage)
                .ToList();
            var html = DkpSheetBoardHtmlBuilder.Build(data, linkshell.EventBoardTheme, rows, p, pageCount, omittedBeyond);
            var png = await _renderer.RenderAsync(html, cancellationToken);
            if (png is null)
            {
                pageImages.Clear();
                break;
            }
            pageImages.Add(new(png, $"dkp-{linkshellId}-sheet-{p + 1}.png", PngContentType));
        }

        object payload;
        List<DiscordBotClient.OutgoingFile> files;
        if (pageImages.Count > 0)
        {
            // files[0..n-1] are the page PNGs (each consumed by its embed image);
            // files[n] is the .xlsx (a separate downloadable attachment).
            payload = BuildImagePayload(data, pageImages.Select(f => f.FileName).ToList(), xlsxName);
            files = new(pageImages) { new(workbook, xlsxName, XlsxContentType) };
        }
        else
        {
            payload = BuildTextPayload(data, xlsxName);
            files = new() { new(workbook, xlsxName, XlsxContentType) };
        }

        // Edit the existing post in place. Only repost when the message is genuinely
        // gone (deleted/channel changed) — on a transient Discord error keep the stored
        // id so the next DKP change retries the edit idempotently instead of orphaning
        // this post and uploading a duplicate PNG + .xlsx.
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

    // The themed post: one embed per rendered page-image (each shown inside it via
    // image: attachment://), stacked, plus the .xlsx attached for download. The first
    // embed carries the title + "Updated" footer; the rest are bare image embeds (each
    // page-image already draws its own header). attachments[i] (i < pageImageNames.Count)
    // is the page PNG = files[i]; the final attachment is the .xlsx = files[pageImageNames.Count].
    private static object BuildImagePayload(DkpSheetData data, IReadOnlyList<string> pageImageNames, string xlsxName)
    {
        var embeds = new List<object>(pageImageNames.Count);
        for (var i = 0; i < pageImageNames.Count; i++)
        {
            embeds.Add(i == 0
                ? new
                {
                    title = Clip($"📊 {data.LinkshellName} — DKP", 256),
                    color = EmbedColor,
                    image = new { url = $"attachment://{pageImageNames[i]}" },
                    footer = new { text = "Updated" },
                    timestamp = DateTime.UtcNow.ToString("o"),
                }
                : (object)new
                {
                    color = EmbedColor,
                    image = new { url = $"attachment://{pageImageNames[i]}" },
                });
        }

        var attachments = new List<object>(pageImageNames.Count + 1);
        for (var i = 0; i < pageImageNames.Count; i++)
        {
            attachments.Add(new { id = i, filename = pageImageNames[i] });
        }
        attachments.Add(new { id = pageImageNames.Count, filename = xlsxName });

        return new
        {
            embeds = embeds.ToArray(),
            attachments = attachments.ToArray(),
            allowed_mentions = new { parse = Array.Empty<string>() },
        };
    }

    // Fallback embed (renderer unavailable): a summary line + a monospace table of
    // every member (Current / Biddable / Total / Spent). The attachment reference
    // (attachments:[{id:0,...}]) ties the uploaded .xlsx (files[0]) to the message.
    private static object BuildTextPayload(DkpSheetData data, string fileName)
    {
        var sb = new StringBuilder();
        sb.Append("**Members** ").Append(data.TotalMembers)
          .Append("  ·  **Total DKP** ").Append(Num(data.TotalDkp))
          .Append("  ·  **Biddable** ").Append(Num(data.Biddable))
          .Append("  ·  **Spent** ").Append(Num(data.TotalSpent))
          .Append("\n\n");

        // Fixed-width table inside a code block so columns line up in Discord.
        var table = new StringBuilder();
        table.Append("```\n");
        table.Append(Row("Member", "Current", "Biddable", "Total", "Spent")).Append('\n');
        var omitted = 0;
        foreach (var m in data.Members)
        {
            var line = Row(Clip(m.Name, 16), Num(m.Current), Num(m.Biddable), Num(m.Total), Num(m.Spent));
            // +4 keeps room for the closing fence + the "…and N more" note.
            if (sb.Length + table.Length + line.Length + 4 > MaxTableChars)
            {
                omitted++;
                continue;
            }
            table.Append(line).Append('\n');
        }
        table.Append("```");
        if (omitted > 0)
        {
            table.Append($"\n_…and {omitted} more — full list in the attached spreadsheet._");
        }
        sb.Append(table);

        return new
        {
            embeds = new[]
            {
                new
                {
                    title = Clip($"📊 {data.LinkshellName} — DKP", 256),
                    description = sb.ToString(),
                    color = EmbedColor,
                    footer = new { text = "Updated" },
                    timestamp = DateTime.UtcNow.ToString("o"),
                },
            },
            attachments = new object[] { new { id = 0, filename = fileName } },
            allowed_mentions = new { parse = Array.Empty<string>() },
        };
    }

    // 16-wide name, then 9-wide right-aligned number columns — compact enough to
    // avoid wrapping on most clients while staying readable.
    private static string Row(string name, string current, string biddable, string total, string spent)
        => $"{Pad(name, 16)} {PadLeft(current, 9)} {PadLeft(biddable, 9)} {PadLeft(total, 9)} {PadLeft(spent, 9)}";

    private static string Num(double v) => v.ToString("N2");

    private static string Clip(string s, int max)
        => string.IsNullOrEmpty(s) || s.Length <= max ? s : s[..Math.Max(0, max - 1)] + "…";

    private static string Pad(string s, int width)
        => s.Length >= width ? s : s + new string(' ', width - s.Length);

    private static string PadLeft(string s, int width)
        => s.Length >= width ? s : new string(' ', width - s.Length) + s;
}
