using System.Text;

namespace LinkshellManagerDiscordApp.Services;

// One lottery window the Claim Shield recorded, flattened for the Discord board.
//
// A projection rather than the entity: the board renders from a background service that must not
// drag ClaimShieldCapture's navigations around, and keeping the shape explicit is what lets the
// layout below be tested without a database.
public sealed record ClaimShieldBoardCapture(
    string MonsterName,
    bool Won,
    int TotalPlayers,
    DateTime CapturedAtUtc,
    IReadOnlyList<ClaimShieldBoardMember> Members);

public sealed record ClaimShieldBoardMember(string CharacterName, bool Matched);

// The Claim Shield block that sits at the foot of the event board — who tagged the mob, out of how
// many people were racing us for it.
//
// WHY IT IS ON THE BOARD AT ALL. The tag list is not trivia: both finalizers gate the claim bonus
// on being in it, so it is part of the payout. It lived only in the web Event page and in the
// addon's own panel, which meant the people it pays could not see it, and nobody could see how
// contested a pop had actually been without going and looking. The board is where the camp already
// looks.
//
// It renders into the LAST board message's embed — after the party grid, before the buttons —
// because Discord stacks a classic message as content → embeds → components. An officer posting a
// capture from the addon re-renders the board, which is what puts it there.
public static class ClaimShieldBoardSection
{
    // Discord's embed description cap is 4096; the board shares it with the also-attending roster,
    // so this takes a slice rather than the lot.
    private const int MaxLength = 1800;

    // A contested pop can produce a dozen lotteries in a minute. The newest few are the ones being
    // talked about; the rest are audit, and the web card holds all of them.
    private const int MaxCaptures = 4;

    // Wide enough to read a proportion off, narrow enough that a phone does not wrap it. Rendered
    // inside inline code so the two block characters keep a fixed width — the same reason the party
    // grid is fenced.
    private const int BarCells = 20;

    public static string? Build(IReadOnlyList<ClaimShieldBoardCapture>? captures)
    {
        if (captures is null || captures.Count == 0) return null;

        var ordered = captures.OrderByDescending(c => c.CapturedAtUtc).ToList();

        // Per MEMBER, not per capture. Someone who tagged three of the four lotteries is one person
        // earning one bonus, and the header saying "12 tagged" when six people were there is the
        // kind of number an officer would go and re-count by hand.
        var tagged = ordered
            .SelectMany(c => c.Members)
            .Where(m => m.Matched && !string.IsNullOrWhiteSpace(m.CharacterName))
            .Select(m => m.CharacterName.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var sb = new StringBuilder();
        sb.Append("### 🛡️ Claim Shield");
        if (ordered.Count > 1) sb.Append($" · {ordered.Count} lotteries");
        sb.Append('\n');

        if (tagged.Count > 0)
        {
            // Says WILL EARN, not "tagged". The per-lottery line below counts everyone of ours who
            // acted, which can be one or two higher — somebody whose character name the roster does
            // not know acted in the lottery but is not going to be paid for it. Two different facts,
            // so they are worded as two different facts rather than as two numbers that look like
            // they ought to match and don't.
            sb.Append($"**{tagged.Count}** {(tagged.Count == 1 ? "member" : "members")} "
                + "will earn the claim bonus.\n");
        }

        foreach (var capture in ordered.Take(MaxCaptures))
        {
            sb.Append('\n');
            sb.Append(capture.Won ? "🏆 **Claimed**" : "❌ **Lost**");
            sb.Append(" · ");
            sb.Append(Escape(capture.MonsterName));
            sb.Append(" · ");
            // Discord renders :t per viewer's own clock, which is the only correct answer for a
            // linkshell spread across time zones.
            sb.Append($"<t:{ToUnixSeconds(capture.CapturedAtUtc)}:t>");
            sb.Append('\n');

            var ours = capture.Members.Count;
            sb.Append(Bar(ours, capture.TotalPlayers));
            sb.Append(' ');
            sb.Append(Share(ours, capture.TotalPlayers));
            sb.Append('\n');

            if (ours == 0)
            {
                sb.Append("-# Nobody landed an action before the lottery resolved.\n");
                continue;
            }

            var matched = capture.Members.Where(m => m.Matched).Select(m => m.CharacterName.Trim()).ToList();
            var unmatched = capture.Members.Where(m => !m.Matched).Select(m => m.CharacterName.Trim()).ToList();
            if (matched.Count > 0)
            {
                sb.Append(string.Join(", ", matched.Select(Escape)));
                sb.Append('\n');
            }
            if (unmatched.Count > 0)
            {
                // Said out loud rather than hidden. An unmatched name is somebody who acted and is
                // NOT going to be paid for it — almost always a roster name that needs fixing, and
                // the only place anyone would notice is here.
                sb.Append("-# Not on the roster, so not paid: ");
                sb.Append(string.Join(", ", unmatched.Select(Escape)));
                sb.Append('\n');
            }
        }

        if (ordered.Count > MaxCaptures)
        {
            sb.Append($"-# +{ordered.Count - MaxCaptures} earlier "
                + $"{(ordered.Count - MaxCaptures == 1 ? "lottery" : "lotteries")} on the event page.\n");
        }

        var text = sb.ToString().TrimEnd();
        return text.Length <= MaxLength ? text : text[..MaxLength].TrimEnd();
    }

    // "6 of 23 claimers were ours" — or just our own count when the addon could not tell how many
    // people were in the lottery. TotalPlayers arrives as 0 from a capture the parser could not
    // total, and "6 of 0" is worse than saying less.
    private static string Share(int ours, int total)
    {
        if (total <= 0) return $"**{ours}** of ours landed an action";
        var pct = (int)Math.Round(ours * 100.0 / total);
        return $"**{ours}** of **{total}** claimers were ours ({pct}%)";
    }

    // The bar is the whole point of putting this on the board: a number says six people tagged, a
    // bar says whether six was most of the pop or a corner of it.
    //
    // An unknown total renders EMPTY rather than full. "We were 100% of the lottery" is a claim
    // about other linkshells, and it must not be made by a missing field.
    private static string Bar(int ours, int total)
    {
        var filled = 0;
        if (total > 0 && ours > 0)
        {
            filled = (int)Math.Round(Math.Min(ours, total) * (double)BarCells / total);
            // A single tagger in a crowd still rounds to nothing; show one cell, because "some" and
            // "none" are the two answers this bar exists to tell apart.
            if (filled == 0) filled = 1;
            if (filled > BarCells) filled = BarCells;
        }
        return "`" + new string('█', filled) + new string('░', BarCells - filled) + "`";
    }

    private static long ToUnixSeconds(DateTime value)
        => ((DateTimeOffset)DateTime.SpecifyKind(value, DateTimeKind.Utc)).ToUnixTimeSeconds();

    // Character names are player-supplied. Markdown in one would otherwise reformat the block.
    private static string Escape(string? text)
        => string.IsNullOrEmpty(text)
            ? string.Empty
            : text.Replace("\\", "\\\\").Replace("*", "\\*").Replace("_", "\\_")
                  .Replace("`", "\\`").Replace("~", "\\~").Replace("|", "\\|");
}
