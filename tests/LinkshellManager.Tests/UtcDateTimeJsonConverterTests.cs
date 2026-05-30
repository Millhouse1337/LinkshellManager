using System.Text.Json;
using LinkshellManagerDiscordApp.Utils;
using Xunit;

namespace LinkshellManager.Tests;

// The whole client/server contract assumes every DateTime crosses the wire as
// UTC with an explicit `Z`. These tests pin that behaviour so a future
// serializer change can't silently reintroduce browser-local drift.
public class UtcDateTimeJsonConverterTests
{
    private static JsonSerializerOptions Options()
    {
        var options = new JsonSerializerOptions();
        options.Converters.Add(new UtcDateTimeJsonConverter());
        options.Converters.Add(new UtcNullableDateTimeJsonConverter());
        return options;
    }

    [Fact]
    public void Write_UnspecifiedKind_IsStampedAsUtc()
    {
        // EF/Npgsql hands back Unspecified for `timestamp without time zone`.
        var value = new DateTime(2026, 5, 29, 13, 0, 0, DateTimeKind.Unspecified);

        var json = JsonSerializer.Serialize(value, Options());

        Assert.Equal("\"2026-05-29T13:00:00.0000000Z\"", json);
    }

    [Fact]
    public void Write_UtcKind_EndsWithZ()
    {
        var value = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);

        var json = JsonSerializer.Serialize(value, Options());

        Assert.EndsWith("Z\"", json);
        Assert.Equal("\"2026-01-02T03:04:05.0000000Z\"", json);
    }

    [Fact]
    public void Write_LocalKind_ConvertsToUtc()
    {
        var utc = new DateTime(2026, 5, 29, 18, 30, 0, DateTimeKind.Utc);
        var local = utc.ToLocalTime();

        var json = JsonSerializer.Serialize(local, Options());

        Assert.Equal("\"2026-05-29T18:30:00.0000000Z\"", json);
    }

    [Fact]
    public void Read_NaiveIsoString_ReturnsUtcKind()
    {
        var result = JsonSerializer.Deserialize<DateTime>("\"2026-05-29T13:00:00\"", Options());

        Assert.Equal(DateTimeKind.Utc, result.Kind);
        Assert.Equal(new DateTime(2026, 5, 29, 13, 0, 0, DateTimeKind.Utc), result);
    }

    [Fact]
    public void Read_OffsetIsoString_IsConvertedToUtc()
    {
        // +02:00 means 13:00 local == 11:00 UTC.
        var result = JsonSerializer.Deserialize<DateTime>("\"2026-05-29T13:00:00+02:00\"", Options());

        Assert.Equal(DateTimeKind.Utc, result.Kind);
        Assert.Equal(new DateTime(2026, 5, 29, 11, 0, 0, DateTimeKind.Utc), result);
    }

    [Fact]
    public void RoundTrip_PreservesInstant()
    {
        var original = new DateTime(2026, 12, 31, 23, 59, 58, DateTimeKind.Utc);

        var json = JsonSerializer.Serialize(original, Options());
        var restored = JsonSerializer.Deserialize<DateTime>(json, Options());

        Assert.Equal(original, restored);
        Assert.Equal(DateTimeKind.Utc, restored.Kind);
    }

    [Fact]
    public void Nullable_Null_WritesAndReadsNull()
    {
        DateTime? value = null;

        var json = JsonSerializer.Serialize(value, Options());
        var restored = JsonSerializer.Deserialize<DateTime?>(json, Options());

        Assert.Equal("null", json);
        Assert.Null(restored);
    }

    [Fact]
    public void Nullable_Value_IsStampedAsUtc()
    {
        DateTime? value = new DateTime(2026, 5, 29, 9, 15, 0, DateTimeKind.Unspecified);

        var json = JsonSerializer.Serialize(value, Options());

        Assert.Equal("\"2026-05-29T09:15:00.0000000Z\"", json);
    }
}
