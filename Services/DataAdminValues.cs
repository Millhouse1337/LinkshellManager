using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace LinkshellManagerDiscordApp.Services;

// Text -> typed column value, the one conversion path shared by list filters and the editor.
// Everything the database would reject is turned into a message here first: required columns,
// max lengths, option lists, unparsable numbers/dates. Every DateTime is stamped Utc and every
// DateTimeOffset moved to UTC because all persisted timestamps are timestamptz, which Npgsql
// refuses to write with any other Kind.
public static class DataAdminValues
{
    // Accepts what <input type="datetime-local"> posts and what the display format prints.
    private static readonly string[] DateTimeFormats =
    {
        "yyyy-MM-dd'T'HH:mm", "yyyy-MM-dd'T'HH:mm:ss", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF",
        "yyyy-MM-dd HH:mm", "yyyy-MM-dd HH:mm:ss", "yyyy-MM-dd",
    };

    public static bool TryConvert(string? raw, DataAdminColumn column, out object? value, [NotNullWhen(false)] out string? error)
    {
        value = null;
        error = null;
        var text = raw?.Trim();

        // Blank means "no value": fine for a nullable column, an error for anything else. A
        // non-nullable string is required too -- metadata cannot tell a C# `required` string from
        // one initialised to "", so the safe reading is that both need a value.
        if (string.IsNullOrEmpty(text))
        {
            if (column.IsNullable)
            {
                return true;
            }
            error = $"{column.DisplayName} is required.";
            return false;
        }

        var type = column.UnderlyingType;
        if (type == typeof(string))
        {
            if (column.MaxLength is int max && text.Length > max)
            {
                error = $"{column.DisplayName} must be {max} characters or fewer.";
                return false;
            }
            if (column.Options is { Count: > 0 } options && !options.Contains(text, StringComparer.Ordinal))
            {
                error = $"{column.DisplayName} must be one of: {string.Join(", ", options)}.";
                return false;
            }
            value = text;
            return true;
        }
        if (type == typeof(bool))
        {
            if (string.Equals(text, "true", StringComparison.OrdinalIgnoreCase) || string.Equals(text, "on", StringComparison.OrdinalIgnoreCase) || text == "1")
            {
                value = true;
                return true;
            }
            if (string.Equals(text, "false", StringComparison.OrdinalIgnoreCase) || string.Equals(text, "off", StringComparison.OrdinalIgnoreCase) || text == "0")
            {
                value = false;
                return true;
            }
            error = $"{column.DisplayName} must be true or false.";
            return false;
        }
        if (type == typeof(int))
        {
            return TryNumber<int>(text, column, int.TryParse, NumberStyles.Integer, out value, out error);
        }
        if (type == typeof(long))
        {
            return TryNumber<long>(text, column, long.TryParse, NumberStyles.Integer, out value, out error);
        }
        if (type == typeof(double))
        {
            return TryNumber<double>(text, column, double.TryParse, NumberStyles.Float, out value, out error);
        }
        if (type == typeof(decimal))
        {
            return TryNumber<decimal>(text, column, decimal.TryParse, NumberStyles.Number, out value, out error);
        }
        if (type == typeof(Guid))
        {
            if (Guid.TryParse(text, out var guid))
            {
                value = guid;
                return true;
            }
            error = $"{column.DisplayName} must be a GUID.";
            return false;
        }
        if (type == typeof(DateTime))
        {
            if (DateTime.TryParseExact(text, DateTimeFormats, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed))
            {
                value = DateTime.SpecifyKind(parsed, DateTimeKind.Utc);
                return true;
            }
            error = $"{column.DisplayName} must be a date and time like 2026-09-18 21:30 (UTC).";
            return false;
        }
        if (type == typeof(DateTimeOffset))
        {
            if (DateTimeOffset.TryParseExact(text, DateTimeFormats, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsedOffset))
            {
                value = parsedOffset.ToUniversalTime();
                return true;
            }
            error = $"{column.DisplayName} must be a date and time like 2026-09-18 21:30 (UTC).";
            return false;
        }

        error = $"{column.DisplayName} has a type ({type.Name}) Data Admin cannot edit.";
        return false;
    }

    // Backslash-escape the LIKE metacharacters; the two-argument ILike has no ESCAPE clause and
    // PostgreSQL's default escape character is the backslash.
    public static string EscapeLike(string term) =>
        term.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("%", "\\%", StringComparison.Ordinal)
            .Replace("_", "\\_", StringComparison.Ordinal);

    private delegate bool NumberParser<T>(string text, NumberStyles styles, IFormatProvider provider, out T result);

    private static bool TryNumber<T>(string text, DataAdminColumn column, NumberParser<T> parser, NumberStyles styles, out object? value, out string? error)
        where T : struct
    {
        if (parser(text, styles, CultureInfo.InvariantCulture, out var number))
        {
            value = number;
            error = null;
            return true;
        }
        value = null;
        error = $"{column.DisplayName} must be a number.";
        return false;
    }
}
