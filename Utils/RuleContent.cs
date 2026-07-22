namespace LinkshellManagerDiscordApp.Utils;

// Presentation helpers for the Rules / Announcements cards: turns free-text details
// into paragraph + bullet-list blocks, and maps an optional category label to an
// accent color class + icon so each card reads like the others on the page.
public static class RuleContent
{
    // The preset category choices offered in the dropdown (plus a free-text "Other").
    // Mirrored in the Activity TS const RULE_CATEGORY_OPTIONS (rule-content.helpers.ts) —
    // keep the two in sync. Order is the dropdown order; Badge() colors each.
    public static readonly IReadOnlyList<string> CategoryOptions = new[]
    {
        "Overview", "Focus", "Conduct", "Standards", "DKP", "Attendance",
        "Loot", "Policy", "Event", "Update", "Reminder", "General"
    };

    // One rendered block of details: either a paragraph (Text) or a bullet list (Items).
    public sealed record Block(bool IsList, string? Text, IReadOnlyList<string> Items);

    private static readonly char[] BulletMarkers = { '-', '•', '*', '·', '–', '—', '▪', '●', '◦' };

    // Splits free-text details into blocks: runs of bullet lines (lines starting with
    // -, •, *, ·, –, etc.) become one list; other non-blank lines become paragraphs.
    public static List<Block> Parse(string? details)
    {
        var blocks = new List<Block>();
        if (string.IsNullOrWhiteSpace(details))
        {
            return blocks;
        }

        var lines = details.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var paragraph = new List<string>();
        var bullets = new List<string>();

        void FlushParagraph()
        {
            if (paragraph.Count > 0)
            {
                blocks.Add(new Block(false, string.Join(" ", paragraph), Array.Empty<string>()));
                paragraph.Clear();
            }
        }
        void FlushBullets()
        {
            if (bullets.Count > 0)
            {
                blocks.Add(new Block(true, null, bullets.ToList()));
                bullets.Clear();
            }
        }

        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.Length == 0)
            {
                // Blank line ends the current paragraph but lets a list keep going only
                // if the next non-blank line is also a bullet (handled naturally below).
                FlushParagraph();
                continue;
            }

            if (line.Length > 1 && Array.IndexOf(BulletMarkers, line[0]) >= 0 && char.IsWhiteSpace(line[1]))
            {
                FlushParagraph();
                bullets.Add(line[1..].Trim());
            }
            else
            {
                FlushBullets();
                paragraph.Add(line);
            }
        }
        FlushParagraph();
        FlushBullets();
        return blocks;
    }

    // Accent CSS class + icon glyph for a card. Known categories get a matching color +
    // icon (so "Loot" is always orange-chest, "DKP" blue-coin, etc.); anything else
    // falls back to a color cycled by position so the page still looks varied.
    public static (string Accent, string Icon) Badge(string? category, int index)
    {
        var key = (category ?? string.Empty).Trim().ToLowerInvariant();

        if (key.Length > 0)
        {
            if (key.Contains("dkp")) return ("acc-blue", "\U0001FA99");          // 🪙
            if (key.Contains("loot")) return ("acc-orange", "\U0001F4E6");       // 📦
            if (key.Contains("conduct")) return ("acc-green", "\U0001F91D");     // 🤝
            if (key.Contains("standard") || key.Contains("community")) return ("acc-gold", "⚖️"); // ⚖️
            if (key.Contains("attend")) return ("acc-cyan", "\U0001F465");       // 👥
            if (key.Contains("focus")) return ("acc-purple", "\U0001F3AF");      // 🎯
            if (key.Contains("policy") || key.Contains("trial")) return ("acc-purple", "\U0001F4DC"); // 📜
            if (key.Contains("loot")) return ("acc-orange", "\U0001F4E6");
            if (key.Contains("event")) return ("acc-cyan", "\U0001F5D3️"); // 🗓️
            if (key.Contains("overview") || key.Contains("rule") || key.Contains("system")) return ("acc-blue", "\U0001F6E1️"); // 🛡️
        }

        // Fallback: cycle the palette so untagged cards still vary by position.
        var palette = new[] { "acc-blue", "acc-purple", "acc-green", "acc-gold", "acc-cyan", "acc-orange" };
        return (palette[((index % palette.Length) + palette.Length) % palette.Length], "✦"); // ✦
    }
}
