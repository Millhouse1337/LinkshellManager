using System.Text;
using LinkshellManagerDiscordApp.Data;
using LinkshellManagerDiscordApp.Models;
using Microsoft.EntityFrameworkCore;

namespace LinkshellManagerDiscordApp.Services;

// Posts auction lifecycle embeds to the linkshell's Auctions channel route via
// the bot (so the embed can carry "Bid: {item}" buttons + an "open in app" link):
//  • Create — an "auction opened" embed (items + start/end + creator).
//  • Update — a fresh "new bid" card at the bottom of the channel.
//  • Close  — the final results embed (winner + DKP per item).
// Best-effort: a failed post is logged and dropped; the auction itself is
// unaffected. Never throws to the caller. No-op when no Auctions route is set or
// the bot isn't configured.
public sealed class DiscordAuctionChannelPublisher
{
    private const int CreateColor = 0x5865F2; // Blurple.
    private const int ClosedColor = 0x2ECC71; // Green.

    private readonly ApplicationDbContext _db;
    private readonly ChannelRouteResolver _routes;
    private readonly IConfiguration _configuration;
    private readonly DiscordBotClient _bot;
    private readonly ILogger<DiscordAuctionChannelPublisher> _logger;

    public DiscordAuctionChannelPublisher(
        ApplicationDbContext db,
        ChannelRouteResolver routes,
        IConfiguration configuration,
        DiscordBotClient bot,
        ILogger<DiscordAuctionChannelPublisher> logger)
    {
        _db = db;
        _routes = routes;
        _configuration = configuration;
        _bot = bot;
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
        var (channelId, messageId) = await DispatchAsync(
            auction.LinkshellId, BuildCreateEmbed(auction), BuildCreateComponents(auction), ct);
        if (!string.IsNullOrEmpty(channelId) && !string.IsNullOrEmpty(messageId))
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

    // A bid landed → post a brand-new card at the BOTTOM of the auction channel
    // (rather than editing the original in place) so the current auction state is
    // never buried as people chat. Every bid becomes its own message — a running
    // bid history. No-op when the auction wasn't bot-posted (webhook fallback
    // can't carry components) or the bot isn't configured.
    private async Task UpdateAsync(int auctionId, CancellationToken ct)
    {
        // Tracked so we can keep DiscordMessageId pointing at the latest card.
        var auction = await _db.Auctions
            .Include(a => a.AuctionItems)
            .FirstOrDefaultAsync(a => a.Id == auctionId, ct);
        if (auction is null
            || string.IsNullOrEmpty(auction.DiscordChannelId)
            || !_bot.IsConfigured)
        {
            return;
        }

        var payload = new
        {
            embeds = new[] { BuildCreateEmbed(auction, "New bid") },
            components = BuildCreateComponents(auction),
            allowed_mentions = new { parse = Array.Empty<string>() }
        };
        var messageId = await _bot.PostMessageAsync(auction.DiscordChannelId, payload, ct);
        if (!string.IsNullOrEmpty(messageId))
        {
            auction.DiscordMessageId = messageId;
            AppendMessageId(auction, messageId);
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
        await DispatchAsync(history.LinkshellId, BuildClosedEmbed(history), BuildLinkButton("View results in app"), ct);
    }

    // Posts to the linkshell's Auctions channel route via the bot (so the embed
    // can carry the bid + link buttons). Returns the (channelId, messageId) so the
    // caller can persist them for later in-place edits; (null, null) when no
    // Auctions route is configured or the bot isn't available.
    private async Task<(string? ChannelId, string? MessageId)> DispatchAsync(
        int linkshellId, object embed, object[]? components, CancellationToken ct)
    {
        var channelId = await _routes.ResolveChannelIdAsync(linkshellId, ChannelPostTypes.Auctions, ct);
        if (string.IsNullOrEmpty(channelId) || !_bot.IsConfigured)
        {
            return (null, null);
        }

        object payload = components is null || components.Length == 0
            ? new { embeds = new[] { embed }, allowed_mentions = new { parse = Array.Empty<string>() } }
            : new { embeds = new[] { embed }, components, allowed_mentions = new { parse = Array.Empty<string>() } };
        var messageId = await _bot.PostMessageAsync(channelId, payload, ct);
        return (channelId, messageId);
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

    private object BuildCreateEmbed(Auction auction, string footerText = "Auction opened")
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
            footer = new { text = footerText },
            timestamp = DateTime.UtcNow.ToString("o"),
        };
    }

    private object BuildClosedEmbed(AuctionHistory history)
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
        var unix = ((DateTimeOffset)DateTime.SpecifyKind(utc, DateTimeKind.Utc)).ToUnixTimeSeconds();
        return $"<t:{unix}:f> (<t:{unix}:R>)";
    }

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
