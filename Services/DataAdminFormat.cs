using System.Globalization;

namespace LinkshellManagerDiscordApp.Services;

// How a column value is shown on the list/details pages and how it is put back into an input.
public static class DataAdminFormat
{
    public const string Empty = "—"; // em dash

    public static string Display(object? value, bool truncate = false)
    {
        var text = value switch
        {
            null => Empty,
            bool flag => flag ? "Yes" : "No",
            DateTime when_ => when_.Kind == DateTimeKind.Utc || when_.Kind == DateTimeKind.Unspecified
                ? when_.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) + " UTC"
                : when_.ToUniversalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) + " UTC",
            DateTimeOffset offset => offset.ToUniversalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) + " UTC",
            double number => number.ToString("0.##", CultureInfo.InvariantCulture),
            decimal money => money.ToString("0.##", CultureInfo.InvariantCulture),
            string s when s.Length == 0 => Empty,
            IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture) ?? Empty,
            _ => value.ToString() ?? Empty,
        };
        if (truncate && text.Length > DataAdminDefaults.ListCellMaxLength)
        {
            text = text[..(DataAdminDefaults.ListCellMaxLength - 1)] + "…";
        }
        return text;
    }

    // Value for an <input>: dates in the datetime-local shape with seconds (always UTC), everything
    // else invariant text. The editor compares a posted value against this to detect "unchanged".
    public static string ToInputValue(object? value) => value switch
    {
        null => string.Empty,
        bool flag => flag ? "true" : "false",
        DateTime when_ => (when_.Kind == DateTimeKind.Local ? when_.ToUniversalTime() : when_).ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture),
        DateTimeOffset offset => offset.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture),
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture) ?? string.Empty,
        _ => value.ToString() ?? string.Empty,
    };
}
