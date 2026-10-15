using System.Globalization;
using System.Text;

namespace LinkshellManagerDiscordApp.Services;

// Builds the Discord payload for the linkshell's live DKP sheet: the totals (and per-pool totals
// when the linkshell has several pools), then a fixed-width table of members split into pages —
// one embed per page, all in ONE message — with the complete .xlsx attached for download.
//
// Discord caps a message at 10 embeds, each description at 4096 characters, and ALL its embeds
// together at 6000. The 6000 is the one that bites: at about 60 characters a row it holds roughly
// 95 members, so a bigger roster shows the first ~95 and says how many more are in the
// spreadsheet. Pure (no DB) so it can be tested.
public static class DkpSheetMessageBuilder
{
    private const int EmbedColor = 0x5865F2; // Discord blurple.

    // Kept under Discord's limits (6000 per message, 4096 per description) so the closing
    // note always has room, whichever page it lands on.
    private const int MessageBudget = 5800;
    private const int DescriptionBudget = 3900;
    private const int MaxEmbeds = 10;
    private const int MaxNameWidth = 16;
    private const string Fence = "```";
    private const string FooterText = "Updated";

    public const string Legend =
        "_Current · Biddable (after active bids) · lifetime Total · Spent — full data in the attached spreadsheet._";

    public static object BuildPayload(DkpSheetData data, string xlsxFileName)
    {
        var title = Clip($"📊 {data.LinkshellName} — DKP", 256);
        var summary = BuildSummary(data);

        var pages = new List<StringBuilder>();
        var shown = 0;
        if (data.Members.Count > 0)
        {
            var widths = ColumnWidths(data.Members);
            var header = Row(widths, "Member", "Current", "Biddable", "Total", "Spent");
            var pageOverhead = Fence.Length + 1 + header.Length + 1 + Fence.Length;

            // Everything that isn't a table row: the title, the footer, the summary, and room for
            // the closing note (the overflow note is never longer than the legend).
            var used = title.Length + FooterText.Length + summary.Length + 2 + Legend.Length + 2;
            StringBuilder? page = null;
            foreach (var member in data.Members)
            {
                var line = Row(widths, Clip(member.Name, MaxNameWidth),
                    Num(member.Current), Num(member.Biddable), Num(member.Total), Num(member.Spent));
                var rowCost = line.Length + 1;

                var pageLength = page is null ? 0 : page.Length + Fence.Length + (pages.Count == 1 ? summary.Length + 2 : 0);
                if (page is null || pageLength + rowCost > DescriptionBudget)
                {
                    if (pages.Count == MaxEmbeds || used + pageOverhead + rowCost > MessageBudget)
                    {
                        break;
                    }
                    page = new StringBuilder().Append(Fence).Append('\n').Append(header).Append('\n');
                    pages.Add(page);
                    used += pageOverhead;
                }
                else if (used + rowCost > MessageBudget)
                {
                    break;
                }

                page.Append(line).Append('\n');
                used += rowCost;
                shown++;
            }
        }

        var omitted = data.Members.Count - shown;
        var note = omitted > 0
            ? $"_…and {omitted} more — full list in the attached spreadsheet._"
            : Legend;

        var descriptions = pages.Select(p => p.ToString() + Fence).ToList();
        if (descriptions.Count == 0)
        {
            descriptions.Add("_No members._");
        }
        descriptions[0] = summary + "\n\n" + descriptions[0];
        descriptions[^1] += "\n" + note;

        var embeds = new List<object>(descriptions.Count);
        for (var i = 0; i < descriptions.Count; i++)
        {
            var last = i == descriptions.Count - 1;
            embeds.Add(new
            {
                title = i == 0 ? title : null,
                description = descriptions[i],
                color = EmbedColor,
                footer = last ? new { text = FooterText } : null,
                timestamp = last ? DateTime.UtcNow.ToString("o") : null,
            });
        }

        return new
        {
            embeds = embeds.ToArray(),
            // Ties the uploaded .xlsx (files[0]) to the message. Listing only it also drops any
            // page images an older version of this post carried.
            attachments = new object[] { new { id = 0, filename = xlsxFileName } },
            allowed_mentions = new { parse = Array.Empty<string>() },
        };
    }

    private static string BuildSummary(DkpSheetData data)
    {
        var sb = new StringBuilder();
        sb.Append("**Members** ").Append(data.TotalMembers)
          .Append("  ·  **Total DKP** ").Append(Num(data.TotalDkp))
          .Append("  ·  **Biddable** ").Append(Num(data.Biddable))
          .Append("  ·  **Spent** ").Append(Num(data.TotalSpent));

        // Pools is empty unless the linkshell has more than one.
        if (data.Pools.Count > 0)
        {
            var pools = data.Pools.Select((pool, i) =>
                $"{Escape(pool.Name)} {Num(i < data.PoolTotals.Count ? data.PoolTotals[i] : 0)}");
            sb.Append("\n**Pools** ").Append(string.Join("  ·  ", pools));
        }
        return sb.ToString();
    }

    // Each column is as wide as its widest value (or its header), so a seven-figure total
    // can't push its row out of line with the others.
    private static int[] ColumnWidths(IReadOnlyList<DkpSheetMemberRow> members)
    {
        var widths = new[] { "Member".Length, "Current".Length, "Biddable".Length, "Total".Length, "Spent".Length };
        foreach (var m in members)
        {
            widths[0] = Math.Max(widths[0], Clip(m.Name, MaxNameWidth).Length);
            widths[1] = Math.Max(widths[1], Num(m.Current).Length);
            widths[2] = Math.Max(widths[2], Num(m.Biddable).Length);
            widths[3] = Math.Max(widths[3], Num(m.Total).Length);
            widths[4] = Math.Max(widths[4], Num(m.Spent).Length);
        }
        return widths;
    }

    // Name left-aligned, the numbers right-aligned.
    private static string Row(int[] widths, string name, string current, string biddable, string total, string spent)
        => $"{name.PadRight(widths[0])} {current.PadLeft(widths[1])} {biddable.PadLeft(widths[2])} "
         + $"{total.PadLeft(widths[3])} {spent.PadLeft(widths[4])}";

    private static string Num(double v) => v.ToString("N2", CultureInfo.InvariantCulture);

    private static string Clip(string s, int max)
        => string.IsNullOrEmpty(s) || s.Length <= max ? s : s[..Math.Max(0, max - 1)] + "…";

    private static string Escape(string value) => value
        .Replace("\\", "\\\\").Replace("`", "\\`").Replace("*", "\\*")
        .Replace("_", "\\_").Replace("~", "\\~").Replace("|", "\\|");
}
