using System.Net;
using System.Text;

namespace LinkshellManagerDiscordApp.Services;

// Builds the standalone HTML for a linkshell's DKP sheet — the same "Esports HUD"
// card language as the event and auction boards (EventBoardHtmlBuilder /
// AuctionBoardHtmlBuilder), so the DKP post in Discord reads as the same family: a
// top rule, a Cinzel header, the stat tiles, a themed palette, and the member
// table laid out as striped rows. EventBoardImageRenderer screenshots the `.embed`
// root to a PNG that becomes the Discord post's image.
//
// Pure string building (no DB) so it's cheap and easy to render. The caller passes
// the computed DkpSheetData and the linkshell's resolved theme key.
//
// The card carries NO live time — a baked PNG can't localise per viewer — so the
// "Updated" timestamp lives on the embed footer (DiscordDkpSheetPublisher).
public static class DkpSheetBoardHtmlBuilder
{
    // Rows per rendered image. A baked PNG can't scroll and Discord caps an image near
    // ~4096px/side (≈2048px logical at 2× scale), so a big roster is split across several
    // page-images (DiscordDkpSheetPublisher posts them as stacked embeds); this is the
    // per-page row budget that keeps each one under that height.
    public const int RowsPerPage = 45;

    // Renders ONE page of the sheet: the header (with a "page x / y" tag when multi-page),
    // the stat tiles (first page only — the totals repeat), and the given member rows.
    // `omittedBeyond` is the count of members past the last rendered page (only shown in
    // the final page's footer); the full roster is always in the attached .xlsx.
    public static string Build(
        DkpSheetData data, string? theme,
        IReadOnlyList<DkpSheetMemberRow> rows, int pageIndex, int pageCount, int omittedBeyond)
    {
        var sb = new StringBuilder();
        AppendHead(sb, theme);

        // Header: 📊 + eyebrow (+ page tag when paginated) + linkshell name, MEMBERS count.
        var pageTag = pageCount > 1 ? $" &middot; PAGE {pageIndex + 1} / {pageCount}" : string.Empty;
        sb.Append($"""
<div class="head"><span class="coin">&#128202;</span><div>
<div class="eyebrow">DKP SHEET{pageTag}</div><div class="title">{Enc(data.LinkshellName)}</div></div>
<div class="count"><div class="lab">MEMBERS</div><div class="num">{data.TotalMembers}</div></div></div>
""");

        // Stat tiles — the three column totals. Only on the first page (they're the same
        // linkshell-wide totals on every page, so repeating them just wastes height).
        if (pageIndex == 0)
        {
            sb.Append("""<div class="tiles">""");
            sb.Append($"""<div class="tile"><div class="lab">Total DKP</div><div class="val">{Num(data.TotalDkp)}</div></div>""");
            sb.Append($"""<div class="tile"><div class="lab">Biddable</div><div class="val">{Num(data.Biddable)}</div></div>""");
            sb.Append($"""<div class="tile"><div class="lab">Total Spent</div><div class="val">{Num(data.TotalSpent)}</div></div>""");

            // Pools get TILES, not table columns. This card renders at a FIXED width
            // (EventBoardImageRenderer.DefaultCardWidth), so widening the table would clip the
            // right-hand columns off the PNG with no error — the failure would be invisible.
            // Tiles wrap. Capped so a linkshell with many pools can't push the table off the page;
            // the per-member split lives on the DKP page, which the footer already points at.
            const int maxPoolTiles = 6;
            for (var i = 0; i < data.Pools.Count && i < maxPoolTiles; i++)
            {
                var total = i < data.PoolTotals.Count ? data.PoolTotals[i] : 0;
                sb.Append($"""<div class="tile"><div class="lab">{Enc(data.Pools[i].Name)}</div><div class="val">{Num(total)}</div></div>""");
            }
            sb.Append("</div>");
        }

        // Member table (this page's rows).
        sb.Append("""
<table class="dkp"><thead><tr>
<th class="name">Member</th><th class="num">Current</th><th class="num">Biddable</th><th class="num">Total</th><th class="num">Spent</th>
</tr></thead><tbody>
""");
        foreach (var m in rows)
        {
            sb.Append($"""
<tr><td class="name"><div class="who"><span class="gem"><span class="face"></span><span class="spark"></span></span>{Enc(m.Name)}</div></td>
<td class="num cur">{Num(m.Current)}</td><td class="num">{Num(m.Biddable)}</td><td class="num">{Num(m.Total)}</td><td class="num spent">{Num(m.Spent)}</td></tr>
""");
        }
        if (data.Members.Count == 0)
        {
            sb.Append("""<tr><td colspan="5" class="empty">No members.</td></tr>""");
        }
        sb.Append("</tbody></table>");

        string foot;
        if (pageIndex < pageCount - 1)
        {
            foot = $"Page {pageIndex + 1} of {pageCount} &middot; continued below &middot; full list in the attached spreadsheet";
        }
        else if (omittedBeyond > 0)
        {
            foot = $"&hellip; and {omittedBeyond} more &middot; full list in the attached spreadsheet";
        }
        else
        {
            foot = "Current &middot; Biddable (after active bids) &middot; lifetime Total &middot; Spent &middot; full data in the attached spreadsheet";
        }
        sb.Append($"""<div class="foot"><div class="help">{foot}</div></div>""");

        sb.Append("</div></div></body></html>");
        return sb.ToString();
    }

    // The document head + the shared card shell CSS. The :root block mirrors the
    // event/auction boards: the theme-independent half (fonts, the neutral) is fixed
    // here; the per-theme palette is injected from EventBoardThemes so a linkshell's
    // chosen theme swaps the colours without touching any rule below. Only the
    // table rules (.dkp/…) are DKP-specific.
    private static void AppendHead(StringBuilder sb, string? theme)
    {
        sb.Append("""
<!DOCTYPE html><html lang="en"><head><meta charset="UTF-8"/>
<link rel="preconnect" href="https://fonts.googleapis.com"/>
<link rel="preconnect" href="https://fonts.gstatic.com" crossorigin/>
<link href="https://fonts.googleapis.com/css2?family=Cinzel:wght@600;700;800&family=Spectral:ital,wght@0,400;0,600;1,400&display=swap" rel="stylesheet"/>
<style>
  :root{
    --cinzel:"Cinzel",serif;--spectral:"Spectral",Georgia,serif;
    --any:#5f8088;
""");
        sb.Append(EventBoardThemes.PaletteFor(theme));
        sb.Append("""
  }
  *{box-sizing:border-box;}
  body{margin:0;padding:0;background:var(--page-bg);font-family:var(--spectral);}
  .embed{width:1100px;position:relative;color:var(--txt);overflow:hidden;background:var(--bg-grad);}
  .toprule{height:3px;background:linear-gradient(90deg,transparent,var(--accent),transparent);}
  .pad{padding:28px 28px 30px;display:flex;flex-direction:column;gap:22px;}
  .head{display:flex;align-items:flex-end;gap:16px;}
  .coin{font-size:32px;filter:drop-shadow(0 0 8px var(--glow));}
  .eyebrow{font-family:var(--cinzel);font-size:12px;font-weight:700;letter-spacing:6px;color:var(--eyebrow);}
  .title{font-family:var(--cinzel);font-size:38px;font-weight:800;line-height:1;color:var(--txt);text-shadow:var(--title-shadow);}
  .count{margin-left:auto;text-align:right;}
  .count .lab{font-family:var(--cinzel);font-size:11px;letter-spacing:2px;color:var(--dim);}
  .count .num{font-family:var(--cinzel);font-size:26px;font-weight:700;color:var(--accent);white-space:nowrap;}
  .tiles{display:flex;gap:2px;border-top:1px solid var(--line);border-bottom:1px solid var(--line);}
  .tile{flex:1;padding:13px 18px;background:var(--tint);border-left:2px solid var(--accent);}
  .tile:first-child{border-left:none;}
  .tile .lab{font-family:var(--cinzel);font-size:11px;letter-spacing:2px;color:var(--dim);text-transform:uppercase;}
  .tile .val{font-size:22px;font-weight:600;color:var(--soft);margin-top:5px;font-variant-numeric:tabular-nums;}
  /* member table */
  table.dkp{width:100%;border-collapse:collapse;font-size:17px;}
  table.dkp thead th{font-family:var(--cinzel);font-size:13px;font-weight:700;letter-spacing:1.5px;text-transform:uppercase;color:var(--dim);padding:10px 14px;border-bottom:1px solid var(--line);text-align:right;}
  table.dkp thead th.name{text-align:left;}
  table.dkp td{padding:11px 14px;border-bottom:1px solid var(--slot-line);white-space:nowrap;text-align:right;font-variant-numeric:tabular-nums;}
  table.dkp tbody tr:nth-child(even){background:var(--tint);}
  table.dkp td.name{font-family:var(--cinzel);font-size:17px;font-weight:600;letter-spacing:.5px;color:var(--soft);text-align:left;}
  table.dkp td.name .who{display:flex;align-items:center;gap:11px;}
  table.dkp td.num{color:var(--name);}
  table.dkp td.cur{color:var(--accent);font-weight:700;}
  table.dkp td.spent{color:var(--dim);}
  table.dkp td.empty{text-align:center;font-style:italic;color:var(--dim);}
  /* gem marker — a single accent diamond per member, matching the other boards */
  .gem{position:relative;display:inline-block;flex-shrink:0;transform:rotate(45deg);width:13px;height:13px;}
  .gem .face{position:absolute;inset:0;border-radius:3px;background:linear-gradient(135deg,var(--accent-bright),var(--accent-deep));border:1px solid var(--accent);box-shadow:0 0 8px var(--glow),inset 1px 1px 2px rgba(255,255,255,.4);}
  .gem .spark{position:absolute;left:18%;top:18%;width:32%;height:32%;background:rgba(255,255,255,.7);border-radius:1px;}
  .foot{padding-top:18px;border-top:1px solid var(--line);display:flex;flex-direction:column;gap:10px;align-items:center;}
  .help{font-size:13.5px;font-style:italic;color:var(--dim);text-align:center;}
</style></head><body><div class="embed"><div class="toprule"></div><div class="pad">
""");
    }

    private static string Num(double v) => v.ToString("N2");

    private static string Enc(string? value) => WebUtility.HtmlEncode(value ?? string.Empty);
}
