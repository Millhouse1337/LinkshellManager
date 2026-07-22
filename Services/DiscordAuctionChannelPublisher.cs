using System.Text;
using LinkshellManagerDiscordApp.Data;
using LinkshellManagerDiscordApp.Models;
using Microsoft.EntityFrameworkCore;

namespace LinkshellManagerDiscordApp.Services;

// Posts the auction board to the linkshell's Auctions channel route via the bot —
// a wide, readable embed (items + start/end as fields) with the rendered, themed
// PNG (same visual language + linkshell theme as the event board) shown INSIDE it,
// carrying "Bid: {item}" buttons + an "open in app" link. The embed fills the
// message column (a bare image attachment is capped narrow by Discord) and still
// carries the themed image:
//  • Create — post the board (one message).
//  • Update — EDIT that same message in place with a freshly-rendered board, so a
//             live auction is one evolving card (like the event post), not a stack
//             of per-bid messages.
//  • Close  — post the final results board (winner + DKP per item).
// When Chromium isn't available the renderer returns null and each path falls back
// to the same embed without the image so the auction always posts. Best-effort: a
// failed post is logged and dropped; the auction itself is unaffected. Never throws
// to the caller. No-op when no Auctions route is set or the bot isn't configured.
public sealed class DiscordAuctionChannelPublisher
{
    private const int CreateColor = 0x5865F2; // Blurple.
    private const int ClosedColor = 0x2ECC71; // Green.

    private readonly ApplicationDbContext _db;
    private readonly ChannelRouteResolver _routes;
    private readonly IConfiguration _configuration;
    private readonly DiscordBotClient _bot;
    private readonly EventBoardImageRenderer _renderer;
    private readonly ILogger<DiscordAuctionChannelPublisher> _logger;

    public DiscordAuctionChannelPublisher(
        ApplicationDbContext db,
        ChannelRouteResolver routes,
        IConfiguration configuration,
        DiscordBotClient bot,
        EventBoardImageRenderer renderer,
        ILogger<DiscordAuctionChannelPublisher> logger)
    {
        _db = db;
        _routes = routes;
        _configuration = configuration;
        _bot = bot;
        _renderer = renderer;
        _logger = logger;
    }

    public async Task HandleAsync(AuctionChannelJob job, CancellationToken cancellationToken)
    {
        try
        {
            switch (job.Kind)
            {
                case AuctionChannelJobKind.Create:
                    await CreateAsync(job.EntityId, cancellationToken);
                    break;
                case AuctionChannelJobKind.Close:
                    await CloseAsync(job.EntityId, cancellationToken);
                    break;
                case AuctionChannelJobKind.Update:
                    await UpdateAsync(job.EntityId, cancellationToken);
                    break;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Auction channel job {Kind} for entity {EntityId} failed.", job.Kind, job.EntityId);
        }
    }

    private async Task CreateAsync(int auctionId, CancellationToken ct)
    {
        // Tracked (not AsNoTracking) so we can persist the posted message id back
        // onto the auction for later in-place bid edits.
        var auction = await _db.Auctions
            .Include(a => a.AuctionItems)
            .FirstOrDefaultAsync(a => a.Id == auctionId, ct);
        if (auction is null)
        {
            return;
        }

        var channelId = await _routes.ResolveChannelIdAsync(auction.LinkshellId, ChannelPostTypes.Auctions, ct);
        if (string.IsNullOrEmpty(channelId) || !_bot.IsConfigured)
        {
            return;
        }

        // Prefer the rendered board image; fall back to the text embed when the
        // renderer is unavailable so the auction always posts.
        var messageId = await PostBoardImageAsync(channelId, auction, ct)
            ?? await _bot.PostMessageAsync(
                channelId, BuildEmbedPayload(BuildCreateEmbed(auction), BuildCreateComponents(auction)), ct);

        if (!string.IsNullOrEmpty(messageId))
        {
            auction.DiscordChannelId = channelId;
            auction.DiscordMessageId = messageId;
            AppendMessageId(auction, messageId);
            await _db.SaveChangesAsync(ct);
        }
    }

    // Track every posted card id so they can all be deleted from the channel on
    // close (leaving just the results summary).
    private static void AppendMessageId(Auction auction, string messageId)
    {
        auction.DiscordMessageIds = string.IsNullOrEmpty(auction.DiscordMessageIds)
            ? messageId
            : $"{auction.DiscordMessageIds},{messageId}";
    }

    // A bid landed → EDIT the single board message in place with a freshly-rendered
    // board (like the event post), so the auction is one evolving card rather than a
    // stack of per-bid messages. No-op when the auction wasn't bot-posted (webhook
    // fallback can't carry components) or the bot isn't configured.
    private async Task UpdateAsync(int auctionId, CancellationToken ct)
    {
        // Tracked so we can repoint DiscordMessageId if we have to re-post.
        var auction = await _db.Auctions
            .Include(a => a.AuctionItems)
            .FirstOrDefaultAsync(a => a.Id == auctionId, ct);
        if (auction is null
            || string.IsNullOrEmpty(auction.DiscordChannelId)
            || !_bot.IsConfigured)
        {
            return;
        }

        var channelId = auction.DiscordChannelId!;

        // Edit the existing board message in place when we have one.
        if (!string.IsNullOrEmpty(auction.DiscordMessageId))
        {
            var messageId = auction.DiscordMessageId!;
            var theme = await ResolveThemeAsync(auction.LinkshellId, ct);
            var png = await _renderer.RenderAsync(AuctionBoardHtmlBuilder.Build(auction, theme), ct);
            if (png is not null)
            {
                var fileName = $"auction-{auction.Id}-board.png";
                if (await _bot.EditMessageWithImageAsync(
                        channelId, messageId, BuildBoardImagePayload(auction, fileName, "New bid"), png, fileName, ct))
                {
                    return;
                }
            }
            // Intentionally collapses the 3-way result to success/not — on any non-success
            // (transient or gone) we fall through and re-post a fresh card below, since this
            // path re-points DiscordMessageId rather than protecting a kept-alive id.
            else if (await _bot.EditMessageAsync(
                         channelId, messageId,
                         BuildEmbedPayload(BuildCreateEmbed(auction, "New bid"), BuildCreateComponents(auction)), ct)
                     == DiscordEditResult.Edited)
            {
                // Renderer unavailable → edited the same message to the text embed
                // (image↔embed edits are allowed; both are classic messages).
                return;
            }
            // The edit failed (message deleted, etc.) — fall through and re-post.
        }

        // No board message yet, or the edit failed: post a fresh card and repoint
        // DiscordMessageId at it.
        var newId = await PostBoardImageAsync(channelId, auction, ct, "New bid")
            ?? await _bot.PostMessageAsync(
                channelId, BuildEmbedPayload(BuildCreateEmbed(auction, "New bid"), BuildCreateComponents(auction)), ct);
        if (!string.IsNullOrEmpty(newId))
        {
            auction.DiscordMessageId = newId;
            AppendMessageId(auction, newId);
            await _db.SaveChangesAsync(ct);
        }
    }

    private async Task CloseAsync(int auctionHistoryId, CancellationToken ct)
    {
        var history = await _db.AuctionHistories
            .AsNoTracking()
            .Include(h => h.AuctionItems)
            .FirstOrDefaultAsync(h => h.Id == auctionHistoryId, ct);
        if (history is null)
        {
            return;
        }

        var channelId = await _routes.ResolveChannelIdAsync(history.LinkshellId, ChannelPostTypes.Auctions, ct);
        if (string.IsNullOrEmpty(channelId) || !_bot.IsConfigured)
        {
            return;
        }

        // Prefer the rendered results board; fall back to the text embed.
        var theme = await ResolveThemeAsync(history.LinkshellId, ct);
        var png = await _renderer.RenderAsync(AuctionBoardHtmlBuilder.BuildClosed(history, theme), ct);
        if (png is not null)
        {
            var fileName = $"auction-history-{history.Id}-results.png";
            var posted = await _bot.PostMessageWithImageAsync(
                channelId, BuildClosedImagePayload(history, fileName), png, fileName, ct);
            if (!string.IsNullOrEmpty(posted))
            {
                return;
            }
        }

        await _bot.PostMessageAsync(
            channelId, BuildEmbedPayload(BuildClosedEmbed(history), BuildLinkButton("View results in app")), ct);
    }

    // The classic text-embed payload — the fallback used when the board image can't
    // be rendered. `components` is omitted when empty.
    private static object BuildEmbedPayload(object embed, object[]? components)
    {
        return components is null || components.Length == 0
            ? new { embeds = new[] { embed }, allowed_mentions = new { parse = Array.Empty<string>() } }
            : new { embeds = new[] { embed }, components, allowed_mentions = new { parse = Array.Empty<string>() } };
    }

    // The linkshell's chosen board theme key (the same palette the event board
    // uses). Null/blank/unknown is normalised to the default by the builder.
    private async Task<string?> ResolveThemeAsync(int linkshellId, CancellationToken ct)
    {
        return await _db.Linkshells
            .AsNoTracking()
            .Where(l => l.Id == linkshellId)
            .Select(l => l.EventBoardTheme)
            .FirstOrDefaultAsync(ct);
    }

    // Render the live board to a PNG and post it (carrying the bid buttons). Returns
    // the new message id, or null when rendering is unavailable (caller falls back to
    // the text embed). Assumes the channel is resolved and the bot is configured.
    private async Task<string?> PostBoardImageAsync(
        string channelId, Auction auction, CancellationToken ct, string footerText = "Auction opened")
    {
        var theme = await ResolveThemeAsync(auction.LinkshellId, ct);
        var png = await _renderer.RenderAsync(AuctionBoardHtmlBuilder.Build(auction, theme), ct);
        if (png is null)
        {
            return null;
        }
        var fileName = $"auction-{auction.Id}-board.png";
        return await _bot.PostMessageWithImageAsync(
            channelId, BuildBoardImagePayload(auction, fileName, footerText), png, fileName, ct);
    }

    // The board message: the wide, readable embed (items + start/end as fields) with
    // the rendered PNG shown INSIDE it (image: attachment://file) + the bid buttons.
    // The embed fills the message column (a bare image attachment is capped narrow by
    // Discord) and still carries the themed visual. `attachments:[{id:0,...}]`
    // references the file uploaded as files[0].
    private object BuildBoardImagePayload(Auction auction, string fileName, string footerText = "Auction opened")
    {
        return new
        {
            embeds = new[] { BuildCreateEmbed(auction, footerText, fileName) },
            components = BuildCreateComponents(auction),
            attachments = new object[] { new { id = 0, filename = fileName } },
            allowed_mentions = new { parse = Array.Empty<string>() },
        };
    }

    // The results message: the closed embed (winner + DKP per item) with the rendered
    // results PNG inside it + an "open in app" link (no bid buttons).
    private object BuildClosedImagePayload(AuctionHistory history, string fileName)
    {
        object[]? components = BuildLinkButton("View results in app");
        return new
        {
            embeds = new[] { BuildClosedEmbed(history, fileName) },
            components, // omitted by the serializer when null (no public base URL)
            attachments = new object[] { new { id = 0, filename = fileName } },
            allowed_mentions = new { parse = Array.Empty<string>() },
        };
    }

    // The "auction opened" components: a "Bid: {item}" button per item (each
    // opens the inline bid modal), plus a "Bid in app" link button. Capped so we
    // stay within Discord's 5-action-row limit (4 rows of item buttons + 1 link).
    private object[] BuildCreateComponents(Auction auction)
    {
        var rows = new List<object>();
        var current = new List<object>();
        foreach (var item in auction.AuctionItems.OrderBy(i => i.Id).Take(20))
        {
            current.Add(new
            {
                type = 2,   // button
                style = 1,  // primary
                label = Truncate($"Bid: {item.ItemName ?? "item"}", 80),
                custom_id = $"{AuctionBidService.BidButtonPrefix}{item.Id}",
            });
            if (current.Count == 5)
            {
                rows.Add(new { type = 1, components = current.ToArray() });
                current = new List<object>();
            }
        }
        if (current.Count > 0)
        {
            rows.Add(new { type = 1, components = current.ToArray() });
        }

        var link = BuildLinkButton("Bid in app");
        if (link is not null && rows.Count < 5)
        {
            rows.Add(link[0]);
        }
        return rows.ToArray();
    }

    // A single-row "open in app" link button (style 5 = URL; no interaction).
    // Null when no public base URL is configured.
    private object[]? BuildLinkButton(string label)
    {
        var appLink = BuildAppLink();
        if (appLink is null)
        {
            return null;
        }
        return new object[]
        {
            new
            {
                type = 1,
                components = new object[]
                {
                    new { type = 2, style = 5, label, url = appLink },
                },
            },
        };
    }

    private object BuildCreateEmbed(Auction auction, string footerText = "Auction opened", string? imageFileName = null)
    {
        var sb = new StringBuilder();
        var items = auction.AuctionItems.OrderBy(i => i.Id).ToList();
        if (items.Count == 0)
        {
            sb.Append("_No items._");
        }
        else
        {
            foreach (var i in items)
            {
                // Show the current high bid once there is one (the message is
                // edited in place on each bid), else the starting bid.
                string line;
                if (i.CurrentHighestBid.HasValue && i.CurrentHighestBid.Value > 0)
                {
                    line = $"• **{Escape(i.ItemName ?? "(item)")}** — current highest bid **{i.CurrentHighestBid.Value} DKP**"
                         + (string.IsNullOrWhiteSpace(i.CurrentHighestBidder)
                             ? string.Empty
                             : $" ({Escape(i.CurrentHighestBidder!)})");
                }
                else
                {
                    line = $"• **{Escape(i.ItemName ?? "(item)")}**"
                         + (i.StartingBidDkp.HasValue ? $" — start {i.StartingBidDkp} DKP" : string.Empty);
                }
                if (sb.Length + line.Length + 1 > 3500) { break; }
                if (sb.Length > 0) { sb.Append('\n'); }
                sb.Append(line);
            }
        }

        var fields = new List<object>();
        if (auction.StartTime.HasValue)
        {
            fields.Add(new { name = "Starts", value = TimestampMarkup(auction.StartTime.Value), inline = true });
        }
        if (auction.EndTime.HasValue)
        {
            fields.Add(new { name = "Ends", value = TimestampMarkup(auction.EndTime.Value), inline = true });
        }
        if (!string.IsNullOrWhiteSpace(auction.CreatedBy))
        {
            fields.Add(new { name = "Created by", value = Escape(auction.CreatedBy!), inline = true });
        }
        var appLink = BuildAppLink();
        if (appLink is not null)
        {
            fields.Add(new { name = "App", value = $"[Open auctions]({appLink})", inline = false });
        }

        return new
        {
            title = Truncate($"🪙 {auction.AuctionTitle ?? $"Auction #{auction.Id}"}", 250),
            description = sb.ToString(),
            color = CreateColor,
            fields = fields.ToArray(),
            // The rendered board PNG, shown inside the embed (omitted when null).
            image = imageFileName is null ? null : new { url = $"attachment://{imageFileName}" },
            footer = new { text = footerText },
            timestamp = DateTime.UtcNow.ToString("o"),
        };
    }

    private object BuildClosedEmbed(AuctionHistory history, string? imageFileName = null)
    {
        var sb = new StringBuilder();
        var items = history.AuctionItems.OrderBy(i => i.Id).ToList();
        if (items.Count == 0)
        {
            sb.Append("_No items._");
        }
        else
        {
            foreach (var i in items)
            {
                string line;
                if (!string.IsNullOrWhiteSpace(i.CurrentHighestBidder) && i.EndingBidDkp.HasValue)
                {
                    line = $"• **{Escape(i.ItemName ?? "(item)")}** — "
                         + $"{Escape(i.CurrentHighestBidder!)} for **{i.EndingBidDkp} DKP**";
                }
                else
                {
                    line = $"• **{Escape(i.ItemName ?? "(item)")}** — _no bids_";
                }
                if (sb.Length + line.Length + 1 > 3500) { break; }
                if (sb.Length > 0) { sb.Append('\n'); }
                sb.Append(line);
            }
        }

        return new
        {
            title = Truncate($"✅ Auction closed — {history.AuctionTitle ?? $"#{history.Id}"}", 250),
            description = sb.ToString(),
            color = ClosedColor,
            // The rendered results PNG, shown inside the embed (omitted when null).
            image = imageFileName is null ? null : new { url = $"attachment://{imageFileName}" },
            footer = new { text = "Auction closed" },
            timestamp = DateTime.UtcNow.ToString("o"),
        };
    }

    private string? BuildAppLink()
    {
        var baseUrl = _configuration["App:PublicBaseUrl"];
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            return null;
        }
        return $"{baseUrl.TrimEnd('/')}/Auction";
    }

    private static string TimestampMarkup(DateTime utc)
    {
        var unix = ToUnix(utc);
        return $"<t:{unix}:f> (<t:{unix}:R>)";
    }

    private static long ToUnix(DateTime utc) =>
        ((DateTimeOffset)AsUtc(utc)).ToUnixTimeSeconds();

    private static DateTime AsUtc(DateTime value) =>
        value.Kind == DateTimeKind.Utc ? value : DateTime.SpecifyKind(value, DateTimeKind.Utc);

    private static string Escape(string value) => value
        .Replace("\\", "\\\\").Replace("`", "\\`").Replace("*", "\\*")
        .Replace("_", "\\_").Replace("~", "\\~").Replace("|", "\\|");

    private static string Truncate(string value, int max)
    {
        if (string.IsNullOrEmpty(value) || value.Length <= max)
        {
            return value;
        }
        return value[..Math.Max(0, max - 1)] + "…";
    }
}
