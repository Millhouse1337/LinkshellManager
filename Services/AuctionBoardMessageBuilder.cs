using System.Text;
using LinkshellManagerDiscordApp.Models;

namespace LinkshellManagerDiscordApp.Services;

// Builds the Discord payloads for an auction's card: the live card (one embed + a "Bid: {item}"
// button per item) and the results card posted when the auction closes.
//
// Pure (no DB, no config) so it can be tested: the caller passes the loaded auction, the "open
// in app" link (null when no public base URL is configured) and, for the live card, the time to
// judge Queued / Live / Ended against.
public static class AuctionBoardMessageBuilder
{
    private const int LiveColor = 0x5865F2; // Blurple.
    private const int ClosedColor = 0x2ECC71; // Green.

    // Embed descriptions cap at 4096; the rest is headroom for the overflow notes.
    private const int DescriptionBudget = 3500;
    private const int NotesMax = 100;

    // Discord allows 5 action rows of 5 buttons; one row is kept for the "Bid in app" link.
    public const int MaxBidButtons = 20;

    public const string BidHelp = "Bid with an item's button or in the app · highest bid when the timer ends wins";

    // The live card. `components` is always sent — an empty array is what clears buttons off a
    // card on edit — and so is `attachments`, so a card first posted back when it carried a PNG
    // drops that file on its next edit.
    public static object BuildLivePayload(Auction auction, string? appLink, DateTime nowUtc) => new
    {
        embeds = new[] { BuildLiveEmbed(auction, appLink, nowUtc) },
        components = BuildLiveComponents(auction, appLink),
        attachments = Array.Empty<object>(),
        allowed_mentions = new { parse = Array.Empty<string>() },
    };

    // The results card: winner + final DKP per item, and an "open in app" link when there is one.
    public static object BuildClosedPayload(AuctionHistory history, string? appLink) => new
    {
        embeds = new[] { BuildClosedEmbed(history) },
        components = BuildLinkRow("View results in app", appLink) ?? Array.Empty<object>(),
        attachments = Array.Empty<object>(),
        allowed_mentions = new { parse = Array.Empty<string>() },
    };

    // "Queued" before it starts, "Live" while running, "Ended" once the window has elapsed —
    // mirrors the status the Activity board derives (MapAuctionDto).
    public static string Status(Auction auction, DateTime nowUtc)
    {
        var started = auction.StartedAt is not null
            || (auction.StartTime is { } start && AsUtc(start) <= nowUtc);
        if (!started)
        {
            return "Queued";
        }
        return auction.EndTime is { } end && AsUtc(end) <= nowUtc ? "Ended" : "Live";
    }

    private static object BuildLiveEmbed(Auction auction, string? appLink, DateTime nowUtc)
    {
        var items = auction.AuctionItems.OrderBy(i => i.Id).ToList();
        var status = Status(auction, nowUtc);
        var statusIcon = status switch
        {
            "Live" => "🟢",
            "Queued" => "⏳",
            _ => "⏹️",
        };

        var sb = new StringBuilder($"{statusIcon} **{status}** · {ItemCount(items.Count)}");
        var notes = new List<string>();
        if (items.Count > MaxBidButtons)
        {
            notes.Add($"_Items after the first {MaxBidButtons} have no button here — bid on them in the app._");
        }
        AppendItems(sb, items, notes, LiveLine);

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
        if (appLink is not null)
        {
            fields.Add(new { name = "App", value = $"[Open auctions]({appLink})", inline = false });
        }

        return new
        {
            title = Truncate($"🪙 {auction.AuctionTitle ?? $"Auction #{auction.Id}"}", 250),
            description = sb.ToString(),
            color = LiveColor,
            fields = fields.ToArray(),
            footer = new { text = BidHelp },
            timestamp = DateTime.UtcNow.ToString("o"),
        };
    }

    private static object BuildClosedEmbed(AuctionHistory history)
    {
        var items = history.AuctionItems.OrderBy(i => i.Id).ToList();
        var sold = items.Count(IsSold);

        var sb = new StringBuilder($"**{ItemCount(items.Count)}** · {sold} sold");
        AppendItems(sb, items, new List<string>(), ClosedLine);

        var fields = new List<object>();
        if (!string.IsNullOrWhiteSpace(history.CreatedBy))
        {
            fields.Add(new { name = "Created by", value = Escape(history.CreatedBy!), inline = true });
        }

        return new
        {
            title = Truncate($"✅ Auction closed — {history.AuctionTitle ?? $"#{history.Id}"}", 250),
            description = sb.ToString(),
            color = ClosedColor,
            fields = fields.ToArray(),
            footer = new { text = "Auction closed" },
            timestamp = DateTime.UtcNow.ToString("o"),
        };
    }

    // One block per item: the item line, then its type / notes beneath it. Stops at the
    // description budget and says how many items didn't fit, so a big auction never just
    // silently loses its tail. `notes` are appended last and their room is kept free.
    private static void AppendItems(
        StringBuilder sb, IReadOnlyList<AuctionItem> items, List<string> notes, Func<AuctionItem, string> line)
    {
        if (items.Count == 0)
        {
            sb.Append("\n\n_No items._");
            return;
        }

        sb.Append('\n');
        // Room for the notes, plus the "…and N more" line in case it is needed.
        var reserved = notes.Sum(n => n.Length + 2) + 48;
        for (var i = 0; i < items.Count; i++)
        {
            var block = "\n" + line(items[i]);
            var detail = DetailLine(items[i]);
            if (detail is not null)
            {
                block += "\n" + detail;
            }
            if (sb.Length + block.Length + reserved > DescriptionBudget)
            {
                notes.Insert(0, $"_…and {items.Count - i} more — see the app._");
                break;
            }
            sb.Append(block);
        }

        foreach (var note in notes)
        {
            sb.Append("\n\n").Append(note);
        }
    }

    private static string LiveLine(AuctionItem item)
    {
        var name = $"• **{Escape(ItemName(item))}**";
        if (item.CurrentHighestBid is > 0)
        {
            var leader = string.IsNullOrWhiteSpace(item.CurrentHighestBidder)
                ? string.Empty
                : $" · leading {Escape(item.CurrentHighestBidder!.Trim())}";
            return $"{name} — **{item.CurrentHighestBid} DKP**{leader}";
        }
        return item.StartingBidDkp is > 0
            ? $"{name} — starts at {item.StartingBidDkp} DKP · no bids yet"
            : $"{name} — no bids yet";
    }

    private static string ClosedLine(AuctionItem item)
    {
        var name = $"• **{Escape(ItemName(item))}**";
        return IsSold(item)
            ? $"{name} — won by {Escape(item.CurrentHighestBidder!.Trim())} for **{item.EndingBidDkp} DKP**"
            : $"{name} — _no bids_";
    }

    // "Weapon · Dropped by Ouryu" under the item, or null when it has neither.
    private static string? DetailLine(AuctionItem item)
    {
        var bits = new List<string>();
        if (!string.IsNullOrWhiteSpace(item.ItemType))
        {
            bits.Add(Escape(item.ItemType!.Trim()));
        }
        if (!string.IsNullOrWhiteSpace(item.Notes))
        {
            bits.Add(Escape(Truncate(item.Notes!.Trim().ReplaceLineEndings(" "), NotesMax)));
        }
        return bits.Count == 0 ? null : $"↳ _{string.Join(" · ", bits)}_";
    }

    private static bool IsSold(AuctionItem item)
        => !string.IsNullOrWhiteSpace(item.CurrentHighestBidder) && item.EndingBidDkp.HasValue;

    private static string ItemName(AuctionItem item)
        => string.IsNullOrWhiteSpace(item.ItemName) ? "(item)" : item.ItemName!.Trim();

    private static string ItemCount(int count) => count == 1 ? "1 item" : $"{count} items";

    // A "Bid: {item}" button per item (each opens the inline bid modal), plus a "Bid in app" link
    // button. Capped to stay within Discord's 5-action-row limit (4 rows of item buttons + 1 link).
    private static object[] BuildLiveComponents(Auction auction, string? appLink)
    {
        var rows = new List<object>();
        var current = new List<object>();
        foreach (var item in auction.AuctionItems.OrderBy(i => i.Id).Take(MaxBidButtons))
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

        var link = BuildLinkRow("Bid in app", appLink);
        if (link is not null && rows.Count < 5)
        {
            rows.Add(link[0]);
        }
        return rows.ToArray();
    }

    // A single-row "open in app" link button (style 5 = URL; no interaction). Null when there
    // is no link to open.
    private static object[]? BuildLinkRow(string label, string? appLink)
    {
        if (appLink is null)
        {
            return null;
        }
        return new object[]
        {
            new
            {
                type = 1,
                components = new object[] { new { type = 2, style = 5, label, url = appLink } },
            },
        };
    }

    private static string TimestampMarkup(DateTime utc)
    {
        var unix = ((DateTimeOffset)AsUtc(utc)).ToUnixTimeSeconds();
        return $"<t:{unix}:f> (<t:{unix}:R>)";
    }

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
