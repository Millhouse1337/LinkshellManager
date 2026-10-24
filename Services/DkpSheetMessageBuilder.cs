using System.Globalization;
using System.Text;

namespace LinkshellManagerDiscordApp.Services;

// Builds the Discord payloads for the linkshell's live DKP sheet: the totals (and per-pool totals
// when the linkshell has several pools), then a fixed-width table of members split into pages —
// one embed per page — with the complete .xlsx attached to the first message for download.
//
// Discord caps a message at 10 embeds, each description at 4096 characters, and ALL its embeds
// together at 6000. The 6000 is the one that bites: at about 60 characters a row a message holds
// roughly 95 members. So the table runs on into as many messages as it needs (up to
// MaxMessages), which DiscordDkpSheetPublisher keeps edited in place. Pure (no DB) so it can be
// tested.
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

    // A message holds about 95 members, so this is a roster of well over a thousand. Past it the
    // last message says how many more are in the spreadsheet rather than flooding the channel.
    public const int MaxMessages = 15;

    // The post as one or more messages, in channel order. The first carries the title, the
    // summary and the attached .xlsx; each one after it continues the table under a "(2/3)"
    // title; the last ends with the legend and the "Updated" footer. One message used to be
    // everything, and a roster past ~95 members was cut off there.
    public static IReadOnlyList<object> BuildPayloads(DkpSheetData data, string xlsxFileName)
    {
        var title = Clip($"📊 {data.LinkshellName} — DKP", 240);
        var summary = BuildSummary(data);

        // Each message's pages (one embed each), filled row by row.
        var messages = new List<List<StringBuilder>>();
        var shown = 0;
        if (data.Members.Count > 0)
        {
            var widths = ColumnWidths(data.Members);
            var header = Row(widths, "Member", "Current", "Biddable", "Total", "Spent");
            var pageOverhead = Fence.Length + 1 + header.Length + 1 + Fence.Length;

            List<StringBuilder> pages = null!;
            StringBuilder? page = null;
            var used = 0;

            // Everything in a message that isn't a table row: its title (with room for " (2/3)"),
            // the summary on the first, and on every one room for the closing note and footer,
            // since which message ends up last isn't known until the rows run out.
            bool OpenMessage()
            {
                if (messages.Count == MaxMessages)
                {
                    return false;
                }
                pages = new List<StringBuilder>();
                messages.Add(pages);
                page = null;
                used = title.Length + 8 + FooterText.Length + Legend.Length + 2
                    + (messages.Count == 1 ? summary.Length + 2 : 0);
                return true;
            }

            void OpenPage()
            {
                page = new StringBuilder().Append(Fence).Append('\n').Append(header).Append('\n');
                pages.Add(page);
                used += pageOverhead;
            }

            OpenMessage();
            foreach (var member in data.Members)
            {
                var line = Row(widths, Clip(member.Name, MaxNameWidth),
                    Num(member.Current), Num(member.Biddable), Num(member.Total), Num(member.Spent));
                var rowCost = line.Length + 1;

                // The summary sits at the top of the very first page, so it counts against it.
                var onFirstPage = messages.Count == 1 && pages.Count == 1;
                var pageLength = page is null ? 0 : page.Length + Fence.Length + (onFirstPage ? summary.Length + 2 : 0);
                if (page is null || pageLength + rowCost > DescriptionBudget)
                {
                    if (pages.Count == MaxEmbeds || used + pageOverhead + rowCost > MessageBudget)
                    {
                        if (!OpenMessage()) break;
                    }
                    OpenPage();
                }
                else if (used + rowCost > MessageBudget)
                {
                    if (!OpenMessage()) break;
                    OpenPage();
                }

                page!.Append(line).Append('\n');
                used += rowCost;
                shown++;
            }
        }

        var omitted = data.Members.Count - shown;
        var note = omitted > 0
            ? $"_…and {omitted} more — full list in the attached spreadsheet._"
            : Legend;

        var bodies = messages.Count == 0
            ? new List<List<string>> { new() { "_No members._" } }
            : messages.Select(pages => pages.Select(p => p.ToString() + Fence).ToList()).ToList();
        bodies[0][0] = summary + "\n\n" + bodies[0][0];
        bodies[^1][^1] += "\n" + note;

        var payloads = new List<object>(bodies.Count);
        for (var m = 0; m < bodies.Count; m++)
        {
            var descriptions = bodies[m];
            var lastMessage = m == bodies.Count - 1;
            var embeds = new List<object>(descriptions.Count);
            for (var i = 0; i < descriptions.Count; i++)
            {
                var last = lastMessage && i == descriptions.Count - 1;
                embeds.Add(new
                {
                    title = i != 0 ? null : bodies.Count == 1 ? title : $"{title} ({m + 1}/{bodies.Count})",
                    description = descriptions[i],
                    color = EmbedColor,
                    footer = last ? new { text = FooterText } : null,
                    timestamp = last ? DateTime.UtcNow.ToString("o") : null,
                });
            }

            payloads.Add(m == 0
                ? new
                {
                    embeds = embeds.ToArray(),
                    // Ties the uploaded .xlsx (files[0]) to the message. Listing only it also drops
                    // any page images an older version of this post carried.
                    attachments = new object[] { new { id = 0, filename = xlsxFileName } },
                    allowed_mentions = new { parse = Array.Empty<string>() },
                }
                : new
                {
                    embeds = embeds.ToArray(),
                    attachments = Array.Empty<object>(),
                    allowed_mentions = new { parse = Array.Empty<string>() },
                });
        }
        return payloads;
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
