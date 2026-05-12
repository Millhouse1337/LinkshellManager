using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LinkshellManagerDiscordApp.Utils;

// EF Core + Npgsql reads `timestamp without time zone` columns as
// DateTimeKind.Unspecified. System.Text.Json's default DateTime converter
// would then serialize those values WITHOUT a `Z` suffix, so the activity
// client's `new Date(value)` parses them as browser-local time and drifts
// every comparison by the browser's UTC offset.
//
// Project convention: every persisted DateTime is UTC. This converter
// enforces that on the wire — anything Unspecified is treated as UTC,
// Local is converted to UTC, and the value is always written in ISO 8601
// round-trip format ending in `Z`. On the read side, both naive and
// offset-bearing ISO strings are accepted; both end up as DateTimeKind.Utc.
public sealed class UtcDateTimeJsonConverter : JsonConverter<DateTime>
{
    public override DateTime Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String)
        {
            var value = reader.GetString();
            if (!string.IsNullOrWhiteSpace(value)
                && DateTime.TryParse(
                    value,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal,
                    out var parsed))
            {
                return DateTime.SpecifyKind(parsed, DateTimeKind.Utc);
            }
        }

        // Fall back to the framework default for non-string tokens or
        // unparseable strings so the standard error surfaces cleanly.
        return reader.GetDateTime();
    }

    public override void Write(Utf8JsonWriter writer, DateTime value, JsonSerializerOptions options)
    {
        var utc = value.Kind switch
        {
            DateTimeKind.Utc => value,
            DateTimeKind.Local => value.ToUniversalTime(),
            _ => DateTime.SpecifyKind(value, DateTimeKind.Utc),
        };
        writer.WriteStringValue(utc.ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ", CultureInfo.InvariantCulture));
    }
}

// Companion for nullable DateTime so JsonOptions only needs the two converters
// registered. Without this, nullable properties would still go through the
// default System.Text.Json converter and miss the Z-stamping.
public sealed class UtcNullableDateTimeJsonConverter : JsonConverter<DateTime?>
{
    private readonly UtcDateTimeJsonConverter _inner = new();

    public override DateTime? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null)
        {
            return null;
        }
        return _inner.Read(ref reader, typeof(DateTime), options);
    }

    public override void Write(Utf8JsonWriter writer, DateTime? value, JsonSerializerOptions options)
    {
        if (value.HasValue)
        {
            _inner.Write(writer, value.Value, options);
        }
        else
        {
            writer.WriteNullValue();
        }
    }
}
