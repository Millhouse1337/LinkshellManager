using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Html;

namespace LinkshellManagerDiscordApp.Utils;

// Renders Discord-flavored markdown as HTML for the web Rules / Announcements pages.
//
// Rules and announcements are almost always written in Discord first and pasted in, so they
// arrive full of Discord syntax (## headings, - bullets, **bold**, > quotes, ||spoilers||,
// <@mentions>, <:custom:emoji>, <t:timestamps>). Rendering that verbatim showed the raw
// markers on the page; this turns it into the same shapes Discord draws, styled by the
// .dc-* rules in wwwroot/css/lsm-theme.css.
//
// The dialect is Discord's, not CommonMark: single newlines are hard breaks, headings stop at
// ###, there are no setext headings / reference links / inline HTML, and "-# " is subtext.
// All literal text is HTML-encoded on the way out, so stored content can never inject markup.
public static class DiscordMarkdown
{
    // Discord tops out at three heading levels; the post title above the body is an <h2>,
    // so the body's headings start at <h3> to keep the document outline sane.
    private static readonly string[] HeadingTags = { "h3", "h4", "h5" };

    private static readonly Regex ListItemPattern =
        new(@"^([ \t]*)(?:([-*•·–—▪●◦])|(\d{1,3})[.)])[ \t]+(.+)$", RegexOptions.Compiled);
    private static readonly Regex MentionPattern =
        new(@"\G<(@!?|@&|#)(\d{5,25})>", RegexOptions.Compiled);
    private static readonly Regex CustomEmojiPattern =
        new(@"\G<(a?):([A-Za-z0-9_~]{1,64}):(\d{5,25})>", RegexOptions.Compiled);
    private static readonly Regex TimestampPattern =
        new(@"\G<t:(-?\d{1,15})(?::([tTdDfFR]))?>", RegexOptions.Compiled);
    private static readonly Regex AngleUrlPattern =
        new(@"\G<((?:https?|mailto):[^\s<>]+)>", RegexOptions.Compiled);
    private static readonly Regex TagPattern = new("<[^>]+>", RegexOptions.Compiled);
    private static readonly Regex RunOfSpacePattern = new(@"\s{2,}", RegexOptions.Compiled);

    // Longest markers first: *** must win over **, which must win over *.
    private static readonly (string Marker, string Open, string Close)[] Emphasis =
    {
        ("***", "<strong><em>", "</em></strong>"),
        ("___", "<u><em>", "</em></u>"),
        ("**", "<strong>", "</strong>"),
        ("__", "<u>", "</u>"),
        ("~~", "<s>", "</s>"),
        ("||", "<span class=\"dc-spoiler\" tabindex=\"0\" role=\"button\" aria-label=\"Spoiler, activate to reveal\">", "</span>"),
        ("*", "<em>", "</em>"),
        ("_", "<em>", "</em>"),
    };

    // Ready-to-emit HTML for a Razor view: @DiscordMarkdown.Render(model.Details).
    public static IHtmlContent Render(string? source) => new HtmlString(ToHtml(source));

    public static string ToHtml(string? source)
    {
        if (string.IsNullOrWhiteSpace(source))
        {
            return string.Empty;
        }

        var lines = source.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var sb = new StringBuilder();
        RenderBlocks(lines, 0, lines.Length, sb);
        return sb.ToString();
    }

    // Markdown stripped back to a single readable line -- for the dashboard preview rows,
    // which show the first slice of a rule/announcement inside one row of a card.
    public static string ToPlainText(string? source)
    {
        if (string.IsNullOrWhiteSpace(source))
        {
            return string.Empty;
        }

        var sb = new StringBuilder();
        foreach (var raw in source.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith("```", StringComparison.Ordinal))
            {
                continue;
            }

            if (HeadingLevel(line, out var headingText) > 0)
            {
                line = headingText;
            }
            else if (line.StartsWith("-# ", StringComparison.Ordinal))
            {
                line = line[3..].Trim();
            }
            else if (line.StartsWith(">>> ", StringComparison.Ordinal))
            {
                line = line[4..].Trim();
            }
            else if (line.StartsWith("> ", StringComparison.Ordinal))
            {
                line = line[2..].Trim();
            }
            else if (ListItemPattern.Match(line) is { Success: true } item)
            {
                line = "• " + item.Groups[4].Value.Trim();
            }

            if (sb.Length > 0)
            {
                sb.Append(' ');
            }
            sb.Append(line);
        }

        // Reuse the inline renderer for the fiddly parts (escapes, links, mentions, emoji,
        // timestamps), then drop the tags it produced.
        var text = TagPattern.Replace(Inline(sb.ToString()), string.Empty);
        return WebUtility.HtmlDecode(RunOfSpacePattern.Replace(text, " ")).Trim();
    }

    private static void RenderBlocks(string[] lines, int start, int end, StringBuilder sb)
    {
        var i = start;
        while (i < end)
        {
            var line = lines[i].TrimEnd();
            if (line.Trim().Length == 0)
            {
                i++;
                continue;
            }

            var trimmed = line.TrimStart();

            // ``` fenced code block (the language hint on the opening fence is ignored).
            if (trimmed.StartsWith("```", StringComparison.Ordinal))
            {
                var opener = trimmed[3..];
                if (opener.Length > 3 && opener.EndsWith("```", StringComparison.Ordinal))
                {
                    AppendCodeBlock(opener[..^3], sb);
                    i++;
                    continue;
                }

                var body = new List<string>();
                var j = i + 1;
                var closed = false;
                while (j < end)
                {
                    if (lines[j].Trim().StartsWith("```", StringComparison.Ordinal))
                    {
                        closed = true;
                        break;
                    }
                    body.Add(lines[j]);
                    j++;
                }
                if (closed)
                {
                    AppendCodeBlock(string.Join("\n", body).Trim('\n'), sb);
                    i = j + 1;
                    continue;
                }
                // An unterminated fence is just text; fall through.
            }

            // # / ## / ### headings.
            var level = HeadingLevel(trimmed, out var heading);
            if (level > 0)
            {
                var tag = HeadingTags[level - 1];
                sb.Append('<').Append(tag).Append(" class=\"dc-h").Append(level).Append("\">")
                  .Append(Inline(heading))
                  .Append("</").Append(tag).Append('>');
                i++;
                continue;
            }

            // -# subtext. Checked before lists so it is not read as a "-" bullet.
            if (trimmed.StartsWith("-# ", StringComparison.Ordinal))
            {
                sb.Append("<div class=\"dc-subtext\">").Append(Inline(trimmed[3..].Trim())).Append("</div>");
                i++;
                continue;
            }

            // >>> quotes everything that follows; > quotes its own run of lines.
            if (trimmed.StartsWith(">>> ", StringComparison.Ordinal))
            {
                var rest = new List<string> { trimmed[4..] };
                for (var j = i + 1; j < end; j++)
                {
                    rest.Add(lines[j]);
                }
                AppendQuote(rest, sb);
                return;
            }
            if (trimmed.StartsWith("> ", StringComparison.Ordinal) || trimmed == ">")
            {
                var quoted = new List<string>();
                var j = i;
                while (j < end)
                {
                    var candidate = lines[j].TrimStart();
                    if (candidate.StartsWith("> ", StringComparison.Ordinal))
                    {
                        quoted.Add(candidate[2..]);
                    }
                    else if (candidate == ">")
                    {
                        quoted.Add(string.Empty);
                    }
                    else
                    {
                        break;
                    }
                    j++;
                }
                AppendQuote(quoted, sb);
                i = j;
                continue;
            }

            // Bullet / numbered lists, including indented sub-lists.
            if (TryParseListItem(line, out _, out _, out _))
            {
                var items = new List<ListItem>();
                var j = i;
                while (j < end && TryParseListItem(lines[j].TrimEnd(), out var indent, out var ordered, out var text))
                {
                    items.Add(new ListItem(indent, ordered, text));
                    j++;
                }

                var k = 0;
                while (k < items.Count)
                {
                    k = RenderList(items, k, sb);
                }
                i = j;
                continue;
            }

            // Paragraph: consecutive plain lines. Discord keeps single newlines, so they
            // become <br> rather than being reflowed into one run of text.
            var paragraph = new List<string>();
            var p = i;
            while (p < end)
            {
                var candidate = lines[p].TrimEnd();
                if (candidate.Trim().Length == 0)
                {
                    break;
                }
                if (p > i && IsBlockStart(candidate))
                {
                    break;
                }
                paragraph.Add(candidate.Trim());
                p++;
            }

            sb.Append("<p class=\"dc-p\">");
            for (var q = 0; q < paragraph.Count; q++)
            {
                if (q > 0)
                {
                    sb.Append("<br>");
                }
                sb.Append(Inline(paragraph[q]));
            }
            sb.Append("</p>");
            i = p;
        }
    }

    private static void AppendCodeBlock(string code, StringBuilder sb) =>
        sb.Append("<pre class=\"dc-codeblock\"><code>").Append(Encode(code)).Append("</code></pre>");

    private static void AppendQuote(List<string> inner, StringBuilder sb)
    {
        sb.Append("<blockquote class=\"dc-quote\">");
        RenderBlocks(inner.ToArray(), 0, inner.Count, sb);
        sb.Append("</blockquote>");
    }

    private static bool IsBlockStart(string line)
    {
        var trimmed = line.TrimStart();
        return trimmed.StartsWith("```", StringComparison.Ordinal)
            || trimmed.StartsWith("-# ", StringComparison.Ordinal)
            || trimmed.StartsWith("> ", StringComparison.Ordinal)
            || trimmed.StartsWith(">>> ", StringComparison.Ordinal)
            || trimmed == ">"
            || HeadingLevel(trimmed, out _) > 0
            || TryParseListItem(line, out _, out _, out _);
    }

    private static int HeadingLevel(string line, out string text)
    {
        text = string.Empty;
        var hashes = 0;
        while (hashes < line.Length && line[hashes] == '#')
        {
            hashes++;
        }
        if (hashes is < 1 or > 3 || hashes >= line.Length || !char.IsWhiteSpace(line[hashes]))
        {
            return 0;
        }

        text = line[hashes..].Trim();
        return text.Length == 0 ? 0 : hashes;
    }

    private sealed record ListItem(int Indent, bool Ordered, string Text);

    private static bool TryParseListItem(string line, out int indent, out bool ordered, out string text)
    {
        indent = 0;
        ordered = false;
        text = string.Empty;

        var match = ListItemPattern.Match(line);
        if (!match.Success)
        {
            return false;
        }

        foreach (var c in match.Groups[1].Value)
        {
            indent += c == '\t' ? 4 : 1;
        }
        ordered = match.Groups[3].Success;
        text = match.Groups[4].Value.Trim();
        return true;
    }

    // Emits one list level and returns the index of the first item it did not consume.
    // A deeper-indented item is rendered as a nested list inside the <li> above it.
    private static int RenderList(IReadOnlyList<ListItem> items, int start, StringBuilder sb)
    {
        var indent = items[start].Indent;
        var ordered = items[start].Ordered;

        sb.Append(ordered ? "<ol class=\"dc-list\">" : "<ul class=\"dc-list\">");
        var i = start;
        while (i < items.Count && items[i].Indent >= indent && items[i].Ordered == ordered)
        {
            sb.Append("<li>").Append(Inline(items[i].Text));
            i++;
            while (i < items.Count && items[i].Indent > indent)
            {
                i = RenderList(items, i, sb);
            }
            sb.Append("</li>");
        }
        sb.Append(ordered ? "</ol>" : "</ul>");
        return i;
    }

    private static string Inline(string text)
    {
        var sb = new StringBuilder();
        ParseInline(text, 0, text.Length, sb);
        return sb.ToString();
    }

    private static void ParseInline(string s, int start, int end, StringBuilder sb)
    {
        var literal = start;
        var i = start;

        void Flush(int upTo)
        {
            if (upTo > literal)
            {
                sb.Append(Encode(s[literal..upTo]));
            }
        }

        while (i < end)
        {
            var c = s[i];

            // \* and friends: a backslash escapes the marker that follows it.
            if (c == '\\' && i + 1 < end && !char.IsLetterOrDigit(s[i + 1]) && !char.IsWhiteSpace(s[i + 1]))
            {
                Flush(i);
                sb.Append(Encode(s[i + 1].ToString()));
                i += 2;
                literal = i;
                continue;
            }

            // `code` / ``code`` -- wins over every other marker, and is never parsed inside.
            if (c == '`')
            {
                var fence = i + 1 < end && s[i + 1] == '`' ? "``" : "`";
                var close = IndexOfUnescaped(s, fence, i + fence.Length, end);
                if (close > i + fence.Length)
                {
                    Flush(i);
                    sb.Append("<code class=\"dc-code\">")
                      .Append(Encode(s[(i + fence.Length)..close].Trim()))
                      .Append("</code>");
                    i = close + fence.Length;
                    literal = i;
                    continue;
                }
            }

            // <@user>, <#channel>, <:emoji:id>, <t:unix:R>, <https://no-embed-link>
            if (c == '<' && TryAngleToken(s, i, end, out var angleHtml, out var angleLength))
            {
                Flush(i);
                sb.Append(angleHtml);
                i += angleLength;
                literal = i;
                continue;
            }

            // [label](https://url)
            if (c == '[' && TryMaskedLink(s, i, end, sb, Flush, out var afterLink))
            {
                i = afterLink;
                literal = i;
                continue;
            }

            // Bare https://... links.
            if ((c == 'h' || c == 'H') && TryBareUrl(s, i, end, out var urlHtml, out var urlLength))
            {
                Flush(i);
                sb.Append(urlHtml);
                i += urlLength;
                literal = i;
                continue;
            }

            if (c == '@')
            {
                var everyone = MatchesAt(s, i, "@everyone", end) ? "@everyone"
                    : MatchesAt(s, i, "@here", end) ? "@here"
                    : null;
                if (everyone is not null)
                {
                    Flush(i);
                    sb.Append("<span class=\"dc-mention\">").Append(everyone).Append("</span>");
                    i += everyone.Length;
                    literal = i;
                    continue;
                }
            }

            if (TryEmphasis(s, i, end, sb, Flush, out var afterEmphasis))
            {
                i = afterEmphasis;
                literal = i;
                continue;
            }

            i++;
        }

        Flush(end);
    }

    private static bool TryEmphasis(string s, int i, int end, StringBuilder sb, Action<int> flush, out int next)
    {
        next = i;
        foreach (var (marker, open, close) in Emphasis)
        {
            if (!MatchesAt(s, i, marker, end))
            {
                continue;
            }

            // Discord only italicises underscores at word boundaries, so snake_case_names
            // survive intact; * and _ pairs also need non-blank content ("2 * 3 * 4" is math).
            var underscore = marker[0] == '_';
            var strict = underscore || marker[0] == '*';
            if (underscore && i > 0 && IsWordChar(s[i - 1]))
            {
                continue;
            }

            var from = i + marker.Length;
            var closeIndex = IndexOfUnescaped(s, marker, from, end);
            while (closeIndex >= 0)
            {
                var searchFrom = closeIndex + marker.Length;

                // A longer run of the same marker character closes from its tail: in
                // "**bold *and italic***" the italic's * pairs with the first of the three,
                // leaving the last ** to close the bold.
                var runEnd = closeIndex;
                while (runEnd < end && s[runEnd] == marker[0])
                {
                    runEnd++;
                }
                if (runEnd - closeIndex > marker.Length)
                {
                    closeIndex = runEnd - marker.Length;
                }

                var after = closeIndex + marker.Length;
                var valid = closeIndex > from;
                if (valid && strict && (char.IsWhiteSpace(s[from]) || char.IsWhiteSpace(s[closeIndex - 1])))
                {
                    valid = false;
                }
                if (valid && underscore && after < end && IsWordChar(s[after]))
                {
                    valid = false;
                }

                if (valid)
                {
                    flush(i);
                    sb.Append(open);
                    ParseInline(s, from, closeIndex, sb);
                    sb.Append(close);
                    next = after;
                    return true;
                }

                closeIndex = IndexOfUnescaped(s, marker, searchFrom, end);
            }
        }

        return false;
    }

    private static bool TryMaskedLink(string s, int i, int end, StringBuilder sb, Action<int> flush, out int next)
    {
        next = i;
        var closeBracket = IndexOfUnescaped(s, "]", i + 1, end);
        if (closeBracket < 0 || closeBracket == i + 1 || closeBracket + 1 >= end || s[closeBracket + 1] != '(')
        {
            return false;
        }

        var closeParen = s.IndexOf(')', closeBracket + 2);
        if (closeParen < 0 || closeParen >= end)
        {
            return false;
        }

        var url = s[(closeBracket + 2)..closeParen].Trim();
        if (!IsSafeUrl(url))
        {
            return false;
        }

        flush(i);
        AppendLinkOpen(url, sb);
        ParseInline(s, i + 1, closeBracket, sb);
        sb.Append("</a>");
        next = closeParen + 1;
        return true;
    }

    private static bool TryBareUrl(string s, int i, int end, out string html, out int length)
    {
        html = string.Empty;
        length = 0;

        if (!MatchesAt(s, i, "http://", end) && !MatchesAt(s, i, "https://", end))
        {
            return false;
        }

        var j = i;
        while (j < end && !char.IsWhiteSpace(s[j]) && s[j] != '<' && s[j] != '>' && s[j] != '|')
        {
            j++;
        }

        var url = s[i..j].TrimEnd('.', ',', '!', '?', ';', ':', ')', ']', '}', '"', '\'');
        if (url.Length <= 8 || !IsSafeUrl(url))
        {
            return false;
        }

        var sb = new StringBuilder();
        AppendLinkOpen(url, sb);
        sb.Append(Encode(url)).Append("</a>");
        html = sb.ToString();
        length = url.Length;
        return true;
    }

    private static void AppendLinkOpen(string url, StringBuilder sb) =>
        sb.Append("<a class=\"dc-link\" href=\"").Append(Encode(url))
          .Append("\" target=\"_blank\" rel=\"noopener noreferrer nofollow\">");

    private static bool TryAngleToken(string s, int i, int end, out string html, out int length)
    {
        html = string.Empty;
        length = 0;

        var mention = MentionPattern.Match(s, i);
        if (mention.Success && mention.Index + mention.Length <= end)
        {
            var kind = mention.Groups[1].Value;
            html = kind == "#"
                ? "<span class=\"dc-mention\">#channel</span>"
                : kind == "@&"
                    ? "<span class=\"dc-mention\">@role</span>"
                    : "<span class=\"dc-mention\">@user</span>";
            length = mention.Length;
            return true;
        }

        // Custom emoji resolve straight off Discord's CDN (allowed by the app's CSP img-src).
        var emoji = CustomEmojiPattern.Match(s, i);
        if (emoji.Success && emoji.Index + emoji.Length <= end)
        {
            var animated = emoji.Groups[1].Value == "a";
            var name = Encode(emoji.Groups[2].Value);
            var id = emoji.Groups[3].Value;
            html = $"<img class=\"dc-emoji\" src=\"https://cdn.discordapp.com/emojis/{id}.{(animated ? "gif" : "png")}\" " +
                   $"alt=\":{name}:\" title=\":{name}:\" loading=\"lazy\">";
            length = emoji.Length;
            return true;
        }

        var timestamp = TimestampPattern.Match(s, i);
        if (timestamp.Success && timestamp.Index + timestamp.Length <= end
            && long.TryParse(timestamp.Groups[1].Value, out var seconds))
        {
            var style = timestamp.Groups[2].Success ? timestamp.Groups[2].Value[0] : 'f';
            html = FormatTimestamp(seconds, style);
            length = timestamp.Length;
            return true;
        }

        var angleUrl = AngleUrlPattern.Match(s, i);
        if (angleUrl.Success && angleUrl.Index + angleUrl.Length <= end)
        {
            var url = angleUrl.Groups[1].Value;
            if (IsSafeUrl(url))
            {
                var sb = new StringBuilder();
                AppendLinkOpen(url, sb);
                sb.Append(Encode(url)).Append("</a>");
                html = sb.ToString();
                length = angleUrl.Length;
                return true;
            }
        }

        return false;
    }

    private static string FormatTimestamp(long unixSeconds, char style)
    {
        DateTimeOffset moment;
        try
        {
            moment = DateTimeOffset.FromUnixTimeSeconds(unixSeconds);
        }
        catch (ArgumentOutOfRangeException)
        {
            return "<span class=\"dc-timestamp\">Invalid date</span>";
        }

        var utc = moment.UtcDateTime;
        var text = style switch
        {
            't' => utc.ToString("h:mm tt", CultureInfo.InvariantCulture),
            'T' => utc.ToString("h:mm:ss tt", CultureInfo.InvariantCulture),
            'd' => utc.ToString("MM/dd/yyyy", CultureInfo.InvariantCulture),
            'D' => utc.ToString("MMMM d, yyyy", CultureInfo.InvariantCulture),
            'F' => utc.ToString("dddd, MMMM d, yyyy h:mm tt", CultureInfo.InvariantCulture),
            'R' => Relative(utc),
            _ => utc.ToString("MMMM d, yyyy h:mm tt", CultureInfo.InvariantCulture)
        };

        var title = utc.ToString("dddd, MMMM d, yyyy h:mm tt 'UTC'", CultureInfo.InvariantCulture);
        return $"<span class=\"dc-timestamp\" title=\"{Encode(title)}\">{Encode(text)}</span>";
    }

    private static string Relative(DateTime utc)
    {
        var delta = utc - DateTime.UtcNow;
        var ago = delta < TimeSpan.Zero;
        var span = delta.Duration();

        var (value, unit) = span.TotalDays >= 365 ? (span.TotalDays / 365, "year")
            : span.TotalDays >= 30 ? (span.TotalDays / 30, "month")
            : span.TotalDays >= 1 ? (span.TotalDays, "day")
            : span.TotalHours >= 1 ? (span.TotalHours, "hour")
            : span.TotalMinutes >= 1 ? (span.TotalMinutes, "minute")
            : (span.TotalSeconds, "second");

        var rounded = Math.Max(1, (int)Math.Round(value, MidpointRounding.AwayFromZero));
        var plural = rounded == 1 ? unit : unit + "s";
        return ago ? $"{rounded} {plural} ago" : $"in {rounded} {plural}";
    }

    private static bool IsSafeUrl(string url) =>
        (url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
         || url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
         || url.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase))
        && !url.Any(char.IsControl)
        && Uri.IsWellFormedUriString(url, UriKind.Absolute);

    private static bool MatchesAt(string s, int i, string value, int end) =>
        i + value.Length <= end && string.CompareOrdinal(s, i, value, 0, value.Length) == 0;

    private static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c == '_';

    private static int IndexOfUnescaped(string s, string needle, int from, int end)
    {
        var i = from;
        while (i >= 0 && i < end)
        {
            var found = s.IndexOf(needle, i, StringComparison.Ordinal);
            if (found < 0 || found + needle.Length > end)
            {
                return -1;
            }
            if (found == 0 || s[found - 1] != '\\')
            {
                return found;
            }
            i = found + 1;
        }
        return -1;
    }

    private static string Encode(string s) => WebUtility.HtmlEncode(s);
}
