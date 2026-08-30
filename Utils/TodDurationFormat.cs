using System.Globalization;
using System.Text.RegularExpressions;

namespace LinkshellManagerDiscordApp.Utils;

// The one place a ToD duration turns into a display string and back.
//
// Tod.Cooldown and Tod.Interval are stored as HUMAN STRINGS ("22 Hour", "10 Min") — they are
// rendered verbatim on the ToD board, the tracker, the Activity tabs and the addon, so the
// storage format can't become a bare number without changing all of them. Per-monster setups,
// though, are stored as canonical minutes (LinkshellMonsterTiming.CooldownMinutes), because
// every consumer does arithmetic on them. This class is the bridge.
//
// It lives in Utils rather than on either surface because the web form and the Activity form
// both compose these strings: if they formatted independently, one would write "1 Hour" and the
// other "60 Min" for the same configured monster and the ToD list would look inconsistent.
public static class TodDurationFormat
{
    public const string HoursUnit = "hours";
    public const string MinutesUnit = "mins";

    // Accepts what the presets use ("22 Hour", "10 Min") plus anything the relaxed cooldown /
    // interval validators now allow: a bare number, an hours suffix, a minutes suffix, or an
    // "1 Hour 30 Min" pair. A bare number means HOURS for a cooldown and MINUTES for an interval,
    // which is why the caller passes the fallback unit rather than this guessing.
    private static readonly Regex PairPattern = new(
        @"^\s*(?:(\d+(?:\.\d+)?)\s*(?:Hours?|Hrs?|H)\b)?\s*(?:(\d+(?:\.\d+)?)\s*(?:Minutes?|Mins?|M)\b)?\s*$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex BarePattern = new(
        @"^\s*(\d+(?:\.\d+)?)\s*$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Canonical minutes -> the label written into Tod.Cooldown / Tod.Interval.
    // Whole hours render as hours ("22 Hour"); anything else stays in minutes ("90 Min"), so the
    // round trip is lossless and never invents a fractional hour.
    public static string Format(int minutes)
    {
        if (minutes <= 0)
        {
            return "0 Min";
        }
        return minutes % 60 == 0
            ? $"{minutes / 60} Hour"
            : $"{minutes} Min";
    }

    // The (number, unit) split the editor rows and the ToD forms bind to. Mirrors Format, so a
    // value saved as "22 Hour" comes back as (22, hours) rather than (1320, mins).
    public static (int Value, string Unit) Split(int minutes)
    {
        if (minutes > 0 && minutes % 60 == 0)
        {
            return (minutes / 60, HoursUnit);
        }
        return (minutes, MinutesUnit);
    }

    // A number + a unit from the UI -> canonical minutes. Anything that isn't recognisably hours
    // is treated as minutes, so a missing or misspelled unit can never silently multiply a value
    // by 60.
    public static int FromValueAndUnit(double value, string? unit)
    {
        var minutes = IsHours(unit) ? value * 60d : value;
        return (int)Math.Round(minutes, MidpointRounding.AwayFromZero);
    }

    public static bool IsHours(string? unit) =>
        unit is not null
        && (unit.Equals(HoursUnit, StringComparison.OrdinalIgnoreCase)
            || unit.Equals("hour", StringComparison.OrdinalIgnoreCase)
            || unit.Equals("h", StringComparison.OrdinalIgnoreCase)
            || unit.Equals("hr", StringComparison.OrdinalIgnoreCase)
            || unit.Equals("hrs", StringComparison.OrdinalIgnoreCase));

    // Parse a stored label back to minutes. `bareUnit` decides what a unit-less number means —
    // hours for a cooldown ("72"), minutes for an interval ("10") — matching how the two
    // validators have always read their own field.
    public static bool TryParseMinutes(string? text, string bareUnit, out int minutes)
    {
        minutes = 0;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var trimmed = text.Trim();

        var bare = BarePattern.Match(trimmed);
        if (bare.Success
            && double.TryParse(bare.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var bareValue))
        {
            minutes = FromValueAndUnit(bareValue, bareUnit);
            return minutes > 0;
        }

        var match = PairPattern.Match(trimmed);
        if (!match.Success || (!match.Groups[1].Success && !match.Groups[2].Success))
        {
            return false;
        }

        var hours = match.Groups[1].Success
            ? double.Parse(match.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture)
            : 0d;
        var mins = match.Groups[2].Success
            ? double.Parse(match.Groups[2].Value, NumberStyles.Float, CultureInfo.InvariantCulture)
            : 0d;

        minutes = (int)Math.Round((hours * 60d) + mins, MidpointRounding.AwayFromZero);
        return minutes > 0;
    }
}
