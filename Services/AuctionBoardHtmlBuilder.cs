using System.Net;
using System.Text;
using LinkshellManagerDiscordApp.Models;

namespace LinkshellManagerDiscordApp.Services;

// Builds the standalone HTML for an auction's board — the same "Esports HUD" card
// language as the event board (EventBoardHtmlBuilder), so an auction post reads as
// the same family: a top rule, a Cinzel header, the stat tiles, and a themed
// palette. EventBoardImageRenderer screenshots the `.embed` root to a PNG that
// becomes the Discord post's image.
//
// Pure string building (no DB) so it's cheap and easy to render. The caller passes
// the loaded auction (or archived history) and the linkshell's resolved theme key.
//
// The card carries NO live time — a baked PNG can't localise per viewer or tick a
// countdown. The auction's end/close time lives in the Discord message `content`
// line above the image as a timestamp (see DiscordAuctionChannelPublisher), exactly
// as the event board does.
public static class AuctionBoardHtmlBuilder
{
    // Live / queued board for an open auction.
    public static string Build(Auction auction, string? theme)
    {
        var items = auction.AuctionItems.OrderBy(i => i.Id).ToList();
        var status = AuctionStatus(auction);

        var sb = new StringBuilder();
        AppendHead(sb, theme);
        AppendHeader(sb, auction.AuctionTitle, items.Count);
        AppendTiles(sb, status.Label, status.IsLive, items.Count, auction.CreatedBy);

        sb.Append("<div class=\"items\">");
        if (items.Count == 0)
        {
            sb.Append("""<div class="empty">No items.</div>""");
        }
        else
        {
            foreach (var item in items)
            {
                AppendItemRow(sb, item, closed: false);
            }
        }
        sb.Append("</div>");

        sb.Append("""<div class="foot"><div class="help">Bid on an item with its button below &middot; or bid in the app &middot; highest bid when the timer ends wins</div></div>""");
        sb.Append("</div></div></body></html>");
        return sb.ToString();
    }

    // Final results board for a closed auction (archived to AuctionHistory).
    public static string BuildClosed(AuctionHistory history, string? theme)
    {
        var items = history.AuctionItems.OrderBy(i => i.Id).ToList();

        var sb = new StringBuilder();
        AppendHead(sb, theme);
        AppendHeader(sb, history.AuctionTitle, items.Count);
        AppendTiles(sb, "Closed", isLive: false, items.Count, history.CreatedBy);

        sb.Append("<div class=\"items\">");
        if (items.Count == 0)
        {
            sb.Append("""<div class="empty">No items.</div>""");
        }
        else
        {
            foreach (var item in items)
            {
                AppendItemRow(sb, item, closed: true);
            }
        }
        sb.Append("</div>");

        sb.Append("""<div class="foot"><div class="help">Auction closed &middot; winners and final bids above</div></div>""");
        sb.Append("</div></div></body></html>");
        return sb.ToString();
    }

    // The document head + the shared card shell CSS. The :root block mirrors the
    // event board: the theme-independent half (fonts, the neutral) is fixed here;
    // the per-theme palette is injected from EventBoardThemes so a linkshell's
    // chosen theme swaps the colours without touching any rule below. Only the
    // item-list rules (.items/.item-row/…) are auction-specific.
    private static void AppendHead(StringBuilder sb, string? theme)
    {
        sb.Append("""
<!DOCTYPE html><html lang="en"><head><meta charset="UTF-8"/>
<link rel="preconnect" href="https://fonts.googleapis.com"/>
<link rel="preconnect" href="https://fonts.gstatic.com" crossorigin/>
<link href="https://fonts.googleapis.com/css2?family=Cinzel:wght@600;700;800&family=Spectral:ital,wght@0,400;0,600;1,400&display=swap" rel="stylesheet"/>
<style>
  :root{
    --win:#3fcf6b;
    --cinzel:"Cinzel",serif;--spectral:"Spectral",Georgia,serif;
    --any:#5f8088;
""");
        sb.Append(EventBoardThemes.PaletteFor(theme));
        sb.Append("""
  }
  *{box-sizing:border-box;}
  body{margin:0;padding:0;background:var(--page-bg);font-family:var(--spectral);}
  .embed{width:1000px;position:relative;color:var(--txt);overflow:hidden;background:var(--bg-grad);}
  .toprule{height:3px;background:linear-gradient(90deg,transparent,var(--accent),transparent);}
  .pad{padding:26px 26px 28px;display:flex;flex-direction:column;gap:22px;}
  .head{display:flex;align-items:flex-end;gap:16px;}
  .coin{font-size:30px;filter:drop-shadow(0 0 8px var(--glow));}
  .eyebrow{font-family:var(--cinzel);font-size:11px;font-weight:700;letter-spacing:6px;color:var(--eyebrow);}
  .title{font-family:var(--cinzel);font-size:34px;font-weight:800;line-height:1;color:var(--txt);text-shadow:var(--title-shadow);}
  .count{margin-left:auto;text-align:right;}
  .count .lab{font-family:var(--cinzel);font-size:11px;letter-spacing:2px;color:var(--dim);}
  .count .num{font-family:var(--cinzel);font-size:22px;font-weight:700;color:var(--accent);white-space:nowrap;}
  .tiles{display:flex;gap:2px;border-top:1px solid var(--line);border-bottom:1px solid var(--line);}
  .tile{flex:1;padding:11px 16px;background:var(--tint);border-left:2px solid var(--accent);}
  .tile:first-child{border-left:none;}
  .tile .lab{font-family:var(--cinzel);font-size:10px;letter-spacing:2px;color:var(--dim);text-transform:uppercase;}
  .tile .val{font-size:15px;font-weight:600;color:var(--soft);margin-top:4px;display:flex;align-items:center;gap:7px;}
  .dot{width:9px;height:9px;border-radius:50%;background:var(--win);box-shadow:0 0 8px var(--win);flex-shrink:0;}
  .items{display:flex;flex-direction:column;gap:2px;}
  .item-row{display:flex;align-items:center;gap:13px;padding:13px 6px;border-bottom:1px solid var(--slot-line);}
  .item-row:last-child{border-bottom:none;}
  /* gem marker — a single accent diamond per item, matching the event board's gems */
  .gem{position:relative;display:inline-block;flex-shrink:0;transform:rotate(45deg);width:15px;height:15px;}
  .gem .face{position:absolute;inset:0;border-radius:3px;background:linear-gradient(135deg,var(--accent-bright),var(--accent-deep));border:1px solid var(--accent);box-shadow:0 0 9px var(--glow),inset 1px 1px 2px rgba(255,255,255,.4);}
  .gem .spark{position:absolute;left:18%;top:18%;width:32%;height:32%;background:rgba(255,255,255,.7);border-radius:1px;}
  .item-main{min-width:0;flex:1;}
  .item-name{font-family:var(--cinzel);font-size:16px;font-weight:700;letter-spacing:.5px;color:var(--soft);white-space:nowrap;overflow:hidden;text-overflow:ellipsis;}
  .item-type{font-size:12.5px;font-style:italic;color:var(--dim);margin-top:2px;white-space:nowrap;overflow:hidden;text-overflow:ellipsis;}
  .item-bid{text-align:right;flex-shrink:0;}
  .item-bid .val{font-family:var(--cinzel);font-size:18px;font-weight:700;color:var(--accent);white-space:nowrap;}
  .item-bid .val.win{color:var(--win);}
  .item-bid .val.none{color:var(--vacant);font-size:13px;font-weight:600;letter-spacing:1px;text-transform:uppercase;}
  .item-bid .sub{font-size:12.5px;color:var(--name);margin-top:2px;white-space:nowrap;overflow:hidden;text-overflow:ellipsis;max-width:340px;}
  .item-bid .sub .lead{font-style:normal;color:var(--dim);}
  .empty{font-size:13.5px;font-style:italic;color:var(--dim);padding:13px 6px;}
  .foot{padding-top:18px;border-top:1px solid var(--line);display:flex;flex-direction:column;gap:10px;align-items:center;}
  .help{font-size:12.5px;font-style:italic;color:var(--dim);text-align:center;}
</style></head><body><div class="embed"><div class="toprule"></div><div class="pad">
""");
    }

    private static void AppendHeader(StringBuilder sb, string? title, int itemCount)
    {
        var name = Enc(string.IsNullOrWhiteSpace(title) ? "Auction" : title!.Trim());
        sb.Append($"""
<div class="head"><span class="coin">&#129689;</span><div>
<div class="eyebrow">AUCTION</div><div class="title">{name}</div></div>
<div class="count"><div class="lab">ITEMS</div><div class="num">{itemCount}</div></div></div>
""");
    }

    private static void AppendTiles(StringBuilder sb, string statusLabel, bool isLive, int itemCount, string? createdBy)
    {
        var statusDot = isLive ? "<span class=\"dot\"></span>" : string.Empty;
        var creator = string.IsNullOrWhiteSpace(createdBy) ? "&mdash;" : Enc(createdBy!.Trim());
        sb.Append($"""
<div class="tiles">
<div class="tile"><div class="lab">Status</div><div class="val">{statusDot}{Enc(statusLabel)}</div></div>
<div class="tile"><div class="lab">Items</div><div class="val">{itemCount}</div></div>
<div class="tile"><div class="lab">Created by</div><div class="val">{creator}</div></div></div>
""");
    }

    private static void AppendItemRow(StringBuilder sb, AuctionItem item, bool closed)
    {
        var name = Enc(string.IsNullOrWhiteSpace(item.ItemName) ? "(item)" : item.ItemName!.Trim());
        var typeBits = new List<string>();
        if (!string.IsNullOrWhiteSpace(item.ItemType)) { typeBits.Add(Enc(item.ItemType!.Trim())); }
        if (!string.IsNullOrWhiteSpace(item.Notes)) { typeBits.Add(Enc(item.Notes!.Trim())); }
        var type = typeBits.Count > 0 ? string.Join(" &middot; ", typeBits) : "&mdash;";

        string bidHtml;
        if (closed)
        {
            // Final result: winner + ending bid, else no bids.
            if (!string.IsNullOrWhiteSpace(item.CurrentHighestBidder) && item.EndingBidDkp.HasValue)
            {
                bidHtml = $"""<div class="val win">{item.EndingBidDkp.Value} DKP</div><div class="sub"><span class="lead">won by</span> {Enc(item.CurrentHighestBidder!.Trim())}</div>""";
            }
            else
            {
                bidHtml = """<div class="val none">no bids</div>""";
            }
        }
        else if (item.CurrentHighestBid.HasValue && item.CurrentHighestBid.Value > 0)
        {
            var bidder = string.IsNullOrWhiteSpace(item.CurrentHighestBidder)
                ? string.Empty
                : $"""<div class="sub"><span class="lead">leading</span> {Enc(item.CurrentHighestBidder!.Trim())}</div>""";
            bidHtml = $"""<div class="val">{item.CurrentHighestBid.Value} DKP</div>{bidder}""";
        }
        else if (item.StartingBidDkp.HasValue && item.StartingBidDkp.Value > 0)
        {
            bidHtml = $"""<div class="val">{item.StartingBidDkp.Value} DKP</div><div class="sub"><span class="lead">starting</span> &middot; no bids yet</div>""";
        }
        else
        {
            bidHtml = """<div class="val none">no bids yet</div>""";
        }

        sb.Append($"""
<div class="item-row"><span class="gem"><span class="face"></span><span class="spark"></span></span>
<div class="item-main"><div class="item-name">{name}</div><div class="item-type">{type}</div></div>
<div class="item-bid">{bidHtml}</div></div>
""");
    }

    // "Queued" before it starts, "Live" while running, "Ended" once the window
    // has elapsed — mirrors the status the Activity board derives (MapAuctionDto).
    private static (string Label, bool IsLive) AuctionStatus(Auction auction)
    {
        var now = DateTime.UtcNow;
        var started = auction.StartedAt is not null
            || (auction.StartTime is { } st && AsUtc(st) <= now);
        if (!started)
        {
            return ("Queued", false);
        }
        if (auction.EndTime is { } et && AsUtc(et) <= now)
        {
            return ("Ended", false);
        }
        return ("Live", true);
    }

    private static DateTime AsUtc(DateTime value) =>
        value.Kind == DateTimeKind.Utc ? value : DateTime.SpecifyKind(value, DateTimeKind.Utc);

    private static string Enc(string? value) => WebUtility.HtmlEncode(value ?? string.Empty);
}
