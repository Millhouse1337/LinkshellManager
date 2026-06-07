using System.Text;
using System.Text.Json;
using LinkshellManagerDiscordApp.Data;
using LinkshellManagerDiscordApp.Models;
using Microsoft.EntityFrameworkCore;

namespace LinkshellManagerDiscordApp.Services;

// Posts auction lifecycle embeds to every webhook flagged PostAuctions for
// the auction's linkshell (replaces the old Discord-bot per-auction-channel
// feature — no bot token, no channel create/delete):
//  • Create — an "auction opened" embed (items + start/end + creator).
//  • Close  — the final results embed (winner + DKP per item).
// Best-effort per channel: a failed post is logged and dropped; the auction
// itself is unaffected. Never throws to the caller. Mirrors
// DiscordDkpSpendPublisher's webhook delivery.
public sealed class DiscordAuctionChannelPublisher
{
    private const int CreateColor = 0x5865F2; // Blurple.
    private const int ClosedColor = 0x2ECC71; // Green.

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly ApplicationDbContext _db;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _configuration;
    private readonly DiscordBotClient _bot;
    private readonly ILogger<DiscordAuctionChannelPublisher> _logger;

    public DiscordAuctionChannelPublisher(
        ApplicationDbContext db,
        IHttpClientFactory httpClientFactory,
        IConfiguration configuration,
        DiscordBotClient bot,
        ILogger<DiscordAuctionChannelPublisher> logger)
    {
        _db = db;
        _httpClientFactory = httpClientFactory;
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
                "Auction webhook job {Kind} for entity {EntityId} failed.", job.Kind, job.EntityId);
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
            await _db.SaveChangesAsync(ct);
        }
    }

    // A bid landed → edit the auction's "opened" message in place to reflect the
    // current high bid per item (no new message). No-op when the auction wasn't
    // bot-posted (webhook fallback can't be edited) or the bot isn't configured.
    private async Task UpdateAsync(int auctionId, CancellationToken ct)
    {
        var auction = await _db.Auctions
            .AsNoTracking()
            .Include(a => a.AuctionItems)
            .FirstOrDefaultAsync(a => a.Id == auctionId, ct);
        if (auction is null
            || string.IsNullOrEmpty(auction.DiscordChannelId)
            || string.IsNullOrEmpty(auction.DiscordMessageId)
            || !_bot.IsConfigured)
        {
            return;
        }

        var payload = new
        {
            embeds = new[] { BuildCreateEmbed(auction) },
            components = BuildCreateComponents(auction),
            allowed_mentions = new { parse = Array.Empty<string>() }
        };
        await _bot.EditMessageAsync(auction.DiscordChannelId, auction.DiscordMessageId, payload, ct);
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

    // Prefer the bot-posted Auctions channel (so the embed can carry a "Bid in
    // app" / "View results" link button); fall back to the legacy PostAuctions
    // webhooks when no Auctions channel is configured. One or the other, never
    // both, so an officer who sets up the new channel doesn't get double posts.
    // Returns the (channelId, messageId) when posted via the bot so the caller can
    // persist them for later edits; (null, null) when it fell back to webhooks.
    private async Task<(string? ChannelId, string? MessageId)> DispatchAsync(
        int linkshellId, object embed, object[]? components, CancellationToken ct)
    {
        var channelId = await _db.LinkshellDiscordChannels
            .AsNoTracking()
            .Where(channel => channel.LinkshellId == linkshellId
                && channel.Purpose == DiscordChannelPurposes.Auctions
                && channel.ChannelId != "")
            .Select(channel => channel.ChannelId)
            .FirstOrDefaultAsync(ct);

        if (!string.IsNullOrEmpty(channelId) && _bot.IsConfigured)
        {
            object payload = components is null || components.Length == 0
                ? new { embeds = new[] { embed }, allowed_mentions = new { parse = Array.Empty<string>() } }
                : new { embeds = new[] { embed }, components, allowed_mentions = new { parse = Array.Empty<string>() } };
            var messageId = await _bot.PostMessageAsync(channelId, payload, ct);
            return (channelId, messageId);
        }

        // Webhook fallback can't carry components (Discord strips them); post the
        // embed only.
        await PostToWebhooksAsync(linkshellId, embed, ct);
        return (null, null);
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

    // Loads every Auctions-flagged webhook for the linkshell and posts the
    // embed to each. No-op when none configured (the feature is "off").
    private async Task PostToWebhooksAsync(int linkshellId, object embed, CancellationToken ct)
    {
        var webhooks = await _db.LinkshellDiscordWebhooks
            .AsNoTracking()
            .Where(w => w.LinkshellId == linkshellId
                        && w.PostAuctions
                        && w.Url != null && w.Url != "")
            .OrderBy(w => w.Id)
            .ToListAsync(ct);
        if (webhooks.Count == 0)
        {
            return;
        }

        var payload = new
        {
            username = "LSM",
            embeds = new[] { embed },
            allowed_mentions = new { parse = Array.Empty<string>() },
        };
        var json = JsonSerializer.Serialize(payload, JsonOptions);

        var client = _httpClientFactory.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(15);

        foreach (var webhook in webhooks)
        {
            var url = webhook.Url?.Trim();
            if (string.IsNullOrEmpty(url) || !IsDiscordWebhookUrl(url))
            {
                _logger.LogWarning(
                    "Linkshell {LinkshellId} auctions webhook \"{Name}\" has a non-Discord URL; skipping.",
                    linkshellId, webhook.Name ?? "(unnamed)");
                continue;
            }
            try
            {
                using var content = new StringContent(json, Encoding.UTF8, "application/json");
                using var response = await client.PostAsync(url, content, ct);
                if (!response.IsSuccessStatusCode)
                {
                    var body = await response.Content.ReadAsStringAsync(ct);
                    _logger.LogWarning(
                        "Auction post returned {Status} for linkshell {LinkshellId} webhook \"{Name}\": {Body}",
                        (int)response.StatusCode, linkshellId, webhook.Name ?? "(unnamed)", Truncate(body, 300));
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Failed posting auction embed for linkshell {LinkshellId} webhook \"{Name}\".",
                    linkshellId, webhook.Name ?? "(unnamed)");
            }
        }
    }

    private object BuildCreateEmbed(Auction auction)
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
                    line = $"• **{Escape(i.ItemName ?? "(item)")}** — current **{i.CurrentHighestBid.Value} DKP**"
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
            footer = new { text = "Auction opened" },
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

    private static bool IsDiscordWebhookUrl(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri)
        && uri.Scheme == Uri.UriSchemeHttps
        && uri.Host.EndsWith("discord.com", StringComparison.OrdinalIgnoreCase)
        && uri.AbsolutePath.Contains("/api/webhooks/", StringComparison.OrdinalIgnoreCase);

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
