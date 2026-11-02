using System.Text;
using LinkshellManagerDiscordApp.Models;

namespace LinkshellManagerDiscordApp.Services;

// Builds the Discord payloads for an auction's card: the live card (one embed, an item picker
// that opens the bid modal, and My bids / Open in app buttons) and the results card posted when
// the auction closes.
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

    // A Discord select menu holds at most 25 options.
    public const int MaxPickerItems = 25;

    public const string BidHelp = "Pick an item below to bid · highest bid when the timer ends wins";

    // The live card. `components` is always sent — an empty array is what clears buttons off a
    // card on edit — and so is `attachments`, so a card first posted back when it carried a PNG
    // drops that file on its next edit.
    public static object BuildLivePayload(Auction auction, string? appLink, DateTime nowUtc) => new
    {
        embeds = new[] { BuildLiveEmbed(auction, appLink, nowUtc) },
        components = BuildLiveComponents(auction, appLink, nowUtc),
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

    // The details first -- status, when it starts and ends, who is running it -- and the items
    // UNDER them as a table (AppendItemTable). Everything sits in the description so the order is
    // fixed: Discord always draws fields after the description, which put the item list above
    // the Starts / Ends / Created by it belongs under.
    private static object BuildLiveEmbed(Auction auction, string? appLink, DateTime nowUtc)
    {
        var items = auction.AuctionItems.OrderBy(i => i.Id).ToList();
        var status = Status(auction, nowUtc);

        var sb = new StringBuilder(StatusLine(auction, status));
        var times = new List<string>();
        if (auction.StartTime.HasValue)
        {
            times.Add($"Starts {Timestamp(auction.StartTime.Value, 'f')}");
        }
        if (auction.EndTime.HasValue)
        {
            times.Add($"Ends {Timestamp(auction.EndTime.Value, 'f')}");
        }
        if (times.Count > 0)
        {
            sb.Append('\n').Append(string.Join(" · ", times));
        }
        var host = string.IsNullOrWhiteSpace(auction.CreatedBy) ? null : $"Host **{Escape(auction.CreatedBy!.Trim())}**";
        sb.Append('\n').Append(host is null ? ItemCount(items.Count) : $"{host} · {ItemCount(items.Count)}");

        var notes = new List<string>();
        if (items.Count > MaxPickerItems && status != "Ended")
        {
            notes.Add($"_Items after the first {MaxPickerItems} aren't in the picker — bid on them in the app._");
        }
        AppendItemTable(sb, items, notes);

        return new
        {
            title = Truncate(auction.AuctionTitle ?? $"Auction #{auction.Id}", 250),
            description = sb.ToString(),
            color = LiveColor,
            footer = new { text = status == "Ended" ? "Bidding has ended · highest bid wins" : BidHelp },
            timestamp = DateTime.UtcNow.ToString("o"),
        };
    }

    // "🟢 **Live** · ends in 2 days" -- the one line a member reads first.
    private static string StatusLine(Auction auction, string status) => status switch
    {
        "Live" => auction.EndTime is { } end ? $"🟢 **Live** · ends {Timestamp(end, 'R')}" : "🟢 **Live**",
        "Queued" => auction.StartTime is { } start ? $"⏳ **Queued** · starts {Timestamp(start, 'R')}" : "⏳ **Queued**",
        _ => auction.EndTime is { } ended ? $"⏹️ **Ended** {Timestamp(ended, 'R')}" : "⏹️ **Ended**",
    };

    private static object BuildClosedEmbed(AuctionHistory history)
    {
        var items = history.AuctionItems.OrderBy(i => i.Id).ToList();
        var sold = items.Count(IsSold);

        var sb = new StringBuilder($"**{ItemCount(items.Count)}** · {sold} sold");
        AppendItems(sb, items, new List<string>(),
            (item, _) => DetailLine(item) is { } detail ? $"{ClosedLine(item)}\n{detail}" : ClosedLine(item));

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

    // One block per item (`block` gets the item and its 0-based position). Stops at the
    // description budget and says how many items didn't fit, so a big auction never just
    // silently loses its tail. `notes` are appended last and their room is kept free.
    private static void AppendItems(
        StringBuilder sb, IReadOnlyList<AuctionItem> items, List<string> notes, Func<AuctionItem, int, string> block)
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
            var text = "\n" + block(items[i], i);
            if (sb.Length + text.Length + reserved > DescriptionBudget)
            {
                notes.Insert(0, $"_…and {items.Count - i} more — see the app._");
                break;
            }
            sb.Append(text);
        }

        foreach (var note in notes)
        {
            sb.Append("\n\n").Append(note);
        }
    }

    // ANSI styles Discord draws inside an ```ansi code block. Desktop shows the colors; phones
    // show the same table in plain text. Plain markdown has no colors and no right alignment,
    // which is why the item list is a fixed-width table at all.
    private const string Reset = "\u001b[0m";
    private const string BoldWhite = "\u001b[1;37m";
    private const string Gray = "\u001b[30m";
    private const string Yellow = "\u001b[33m";
    private const string BoldGreen = "\u001b[1;32m";

    // Column caps, so a row stays inside the embed's width instead of wrapping and breaking the
    // columns: about 55 characters with every column full.
    private const int NameWidthMax = 14;
    private const int NotesWidthMax = 14;
    private const int BidderWidthMax = 12;

    // The live card's item list:
    //   1  Kirin's Pole  abc            10 DKP  Millhouse
    //   2  test item     Endgame Loot…  25 DKP  no bids
    // Number (matches the item picker), name in bold white, its type / notes, then the DKP
    // lined up on the right and the leader in bold green. No bids yet: the opening DKP and
    // "no bids", in gray. Stops at the description budget like AppendItems.
    private static void AppendItemTable(StringBuilder sb, IReadOnlyList<AuctionItem> items, List<string> notes)
    {
        if (items.Count == 0)
        {
            sb.Append("\n\n_No items._");
            return;
        }

        var rows = items.Select((item, index) =>
        {
            var hasBid = item.CurrentHighestBid is > 0;
            var dkp = hasBid ? $"{item.CurrentHighestBid} DKP"
                : item.StartingBidDkp is > 0 ? $"{item.StartingBidDkp} DKP"
                : "—";
            var bidder = !hasBid ? "no bids"
                : string.IsNullOrWhiteSpace(item.CurrentHighestBidder) ? "?"
                : Cell(item.CurrentHighestBidder!, BidderWidthMax);
            return (
                Number: (index + 1).ToString(),
                Name: Cell(ItemName(item), NameWidthMax),
                Notes: Cell(RawDetail(item) ?? string.Empty, NotesWidthMax),
                Dkp: dkp,
                Bidder: bidder,
                HasBid: hasBid);
        }).ToList();
        var numberWidth = rows.Max(r => r.Number.Length);
        var nameWidth = rows.Max(r => r.Name.Length);
        var notesWidth = rows.Max(r => r.Notes.Length);
        var dkpWidth = rows.Max(r => r.Dkp.Length);

        sb.Append("\n```ansi");
        // Room for the notes, the "…and N more" line and the closing fence.
        var reserved = notes.Sum(n => n.Length + 1) + 52;
        for (var i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            var notesCell = notesWidth == 0 ? string.Empty : "  " + row.Notes.PadRight(notesWidth);
            var bidStyle = row.HasBid ? Yellow : Gray;
            var bidderStyle = row.HasBid ? BoldGreen : Gray;
            var line = $"\n{row.Number.PadLeft(numberWidth)}  {BoldWhite}{row.Name.PadRight(nameWidth)}{Reset}"
                + $"{notesCell}  {bidStyle}{row.Dkp.PadLeft(dkpWidth)}{Reset}  {bidderStyle}{row.Bidder}{Reset}";
            if (sb.Length + line.Length + reserved > DescriptionBudget)
            {
                notes.Insert(0, $"_…and {rows.Count - i} more — see the app._");
                break;
            }
            sb.Append(line);
        }
        sb.Append("\n```");

        foreach (var note in notes)
        {
            sb.Append('\n').Append(note);
        }
    }

    // One table cell: single line, no backticks (they would close the code block), cut to `max`.
    private static string Cell(string value, int max)
    {
        var text = value.Trim().ReplaceLineEndings(" ").Replace('`', '\'');
        return text.Length <= max ? text : text[..(max - 1)].TrimEnd() + "…";
    }

    // Where an item's bidding stands, in plain text (the picker's option description uses it
    // too): "10 DKP, Millhouse", "opens 10 DKP", or "no bids yet".
    private static string BidState(AuctionItem item)
    {
        if (item.CurrentHighestBid is > 0)
        {
            return string.IsNullOrWhiteSpace(item.CurrentHighestBidder)
                ? $"{item.CurrentHighestBid} DKP"
                : $"{item.CurrentHighestBid} DKP, {item.CurrentHighestBidder!.Trim()}";
        }
        return item.StartingBidDkp is > 0 ? $"opens {item.StartingBidDkp} DKP" : "no bids yet";
    }

    private static string ClosedLine(AuctionItem item)
    {
        var name = $"• **{Escape(ItemName(item))}**";
        return IsSold(item)
            ? $"{name} — won by {Escape(item.CurrentHighestBidder!.Trim())} for **{item.EndingBidDkp} DKP**"
            : $"{name} — _no bids_";
    }

    // "↳ _Weapon · Dropped by Ouryu_" under the item on the results card, or null.
    private static string? DetailLine(AuctionItem item)
        => DetailText(item) is { } detail ? $"↳ _{detail}_" : null;

    // "Weapon · Dropped by Ouryu" (escaped for markdown), or null when the item has neither.
    private static string? DetailText(AuctionItem item)
        => RawDetail(item) is { } detail ? Escape(detail) : null;

    // The same, unescaped -- for the code-block table, where markdown is not parsed.
    private static string? RawDetail(AuctionItem item)
    {
        var bits = new List<string>();
        if (!string.IsNullOrWhiteSpace(item.ItemType))
        {
            bits.Add(item.ItemType!.Trim());
        }
        if (!string.IsNullOrWhiteSpace(item.Notes))
        {
            bits.Add(Truncate(item.Notes!.Trim().ReplaceLineEndings(" "), NotesMax));
        }
        return bits.Count == 0 ? null : string.Join(" · ", bits);
    }

    private static bool IsSold(AuctionItem item)
        => !string.IsNullOrWhiteSpace(item.CurrentHighestBidder) && item.EndingBidDkp.HasValue;

    private static string ItemName(AuctionItem item)
        => string.IsNullOrWhiteSpace(item.ItemName) ? "(item)" : item.ItemName!.Trim();

    private static string ItemCount(int count) => count == 1 ? "1 item" : $"{count} items";

    // Row 1: "Pick an item to bid on…", a select whose options are the numbered items; picking
    // one opens the bid modal (it replaced a "Bid: {item}" button per item, which took up to four
    // rows). Gone once bidding has ended. Row 2: My bids (an ephemeral list of the clicker's
    // bids on this auction) and Open in app when there is a link.
    private static object[] BuildLiveComponents(Auction auction, string? appLink, DateTime nowUtc)
    {
        var rows = new List<object>();
        var items = auction.AuctionItems.OrderBy(i => i.Id).ToList();
        if (items.Count > 0 && Status(auction, nowUtc) != "Ended")
        {
            var options = items
                .Take(MaxPickerItems)
                .Select((item, index) => new
                {
                    // Discord caps an option's label and description at 100 characters.
                    label = Truncate($"{index + 1}. {ItemName(item)}", 100),
                    value = item.Id.ToString(),
                    description = Truncate(BidState(item), 100),
                })
                .ToArray();
            rows.Add(new
            {
                type = 1,
                components = new object[]
                {
                    new
                    {
                        type = 3, // string select
                        custom_id = $"{AuctionBidService.PickSelectPrefix}{auction.Id}",
                        placeholder = "Pick an item to bid on…",
                        min_values = 1,
                        max_values = 1,
                        options,
                    },
                },
            });
        }

        var buttons = new List<object>
        {
            new
            {
                type = 2,   // button
                style = 2,  // secondary
                label = "My bids",
                custom_id = $"{AuctionBidService.MyBidsPrefix}{auction.Id}",
            },
        };
        if (appLink is not null)
        {
            buttons.Add(new { type = 2, style = 5, label = "Open in app", url = appLink });
        }
        rows.Add(new { type = 1, components = buttons.ToArray() });
        return rows.ToArray();
    }

    // The ephemeral "My bids" reply: each item this member has bid on, numbered like the card,
    // with their best bid and whether they still lead. `items` is the whole auction in card
    // order, each carrying (at least) this member's own bids.
    public static string BuildMyBidsText(IReadOnlyList<AuctionItem> items, string appUserId)
    {
        var lines = new List<string>();
        var ordered = items.OrderBy(i => i.Id).ToList();
        for (var i = 0; i < ordered.Count; i++)
        {
            var item = ordered[i];
            var mine = item.Bids
                .Where(b => string.Equals(b.AppUserId, appUserId, StringComparison.OrdinalIgnoreCase))
                .Select(b => (int?)b.BidAmount)
                .Max();
            if (mine is null)
            {
                continue;
            }
            var leading = string.Equals(item.CurrentHighestBidderAppUserId, appUserId, StringComparison.OrdinalIgnoreCase);
            var standing = leading
                ? "✅ leading"
                : $"outbid — {item.CurrentHighestBid ?? mine} DKP now";
            lines.Add($"`{i + 1}` **{Escape(ItemName(item))}** · your bid {mine} DKP · {standing}");
        }
        return lines.Count == 0
            ? "You haven't bid on anything in this auction yet. Pick an item on the card to bid."
            : "**Your bids**\n" + string.Join("\n", lines);
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

    // Discord renders <t:unix:style> in each viewer's own time zone: f = date + time, R = "in 2 days".
    private static string Timestamp(DateTime utc, char style)
        => $"<t:{((DateTimeOffset)AsUtc(utc)).ToUnixTimeSeconds()}:{style}>";

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
