using System.Globalization;
using Microsoft.Extensions.Logging;
using NodaTime;

namespace LinkshellManagerDiscordApp.Services;

// Single source of truth for NodaTime-backed UTC ↔ user-zone conversions.
// Previously six controllers each carried an identical copy of these three
// helpers, which meant any correctness fix had to be made six times. All
// consumers should depend on this service so future tweaks (e.g. DST
// strategy, fallback policy) land in one place.
public sealed class TimeZoneConversionService
{
    private readonly IDateTimeZoneProvider _provider;
    private readonly ILogger<TimeZoneConversionService> _logger;

    public TimeZoneConversionService(
        IDateTimeZoneProvider provider,
        ILogger<TimeZoneConversionService> logger)
    {
        _provider = provider;
        _logger = logger;
    }

    // Project a UTC instant into the wall-clock of `timeZoneId`. The result
    // is `Kind.Unspecified` (NodaTime's way of saying "zone is implicit in
    // context") — callers that need to format the value should attach the
    // same zone they passed in.
    public DateTime? ToUserTime(DateTime? utcDateTime, string? timeZoneId)
    {
        if (!utcDateTime.HasValue)
        {
            return null;
        }

        var zone = Resolve(timeZoneId);
        var instant = Instant.FromDateTimeUtc(DateTime.SpecifyKind(utcDateTime.Value, DateTimeKind.Utc));
        return instant.InZone(zone).ToDateTimeUnspecified();
    }

    // Interpret a naive wall-clock `DateTime` as belonging to `timeZoneId`
    // and convert to a UTC instant. `AtLeniently` handles the two awkward
    // moments in any zone with DST: skipped wall-clock times (spring
    // forward) and ambiguous wall-clock times (fall back). It picks the
    // forward shift for skipped times and the earlier offset for ambiguous
    // times — behaviour identical to the per-controller copies it replaces.
    public DateTime? ToUtc(DateTime? localDateTime, string? timeZoneId)
    {
        if (!localDateTime.HasValue)
        {
            return null;
        }

        var zone = Resolve(timeZoneId);
        var local = LocalDateTime.FromDateTime(localDateTime.Value);
        return zone.AtLeniently(local).ToDateTimeUtc();
    }

    // All IANA zone IDs the provider knows about. Used by callers that need
    // to populate dropdowns (`CuratedTimeZones.BuildOrderedList`) or validate
    // user-supplied zone names against the canonical list.
    public IReadOnlyCollection<string> AvailableZoneIds => _provider.Ids;

    // Convenience predicate so callers don't have to reach into the
    // underlying provider just to check "is this string a known zone?".
    public bool IsKnownZone(string? timeZoneId)
        => !string.IsNullOrWhiteSpace(timeZoneId) && _provider.Ids.Contains(timeZoneId);

    // Look up the IANA zone by id, falling back to UTC for null / whitespace
    // / unrecognised input. Exposed because some callers (e.g. the activity
    // controller's naive-vs-explicit-offset parser) want the zone object
    // directly rather than going through ToUtc / ToUserTime.
    public DateTimeZone Resolve(string? timeZoneId)
    {
        if (string.IsNullOrWhiteSpace(timeZoneId))
        {
            return DateTimeZone.Utc;
        }

        if (_provider.Ids.Contains(timeZoneId))
        {
            return _provider[timeZoneId];
        }

        // The AppUser.TimeZone column is validated against the curated IANA
        // list on every profile save, so reaching this branch means a row
        // with a stale or hand-edited value. Warn (truncated to keep logs
        // bounded against arbitrary input) and fall back to UTC so the
        // user still sees timestamps rather than a server error.
        var safe = timeZoneId.Length > 64 ? timeZoneId[..64] : timeZoneId;
        _logger.LogWarning("Unknown time zone '{TimeZoneId}', falling back to UTC.", safe);
        return DateTimeZone.Utc;
    }

    // Parse a user-supplied datetime-local string into a UTC `DateTime`.
    // Centralises the "either explicit Z/offset or naive in profile zone"
    // logic the activity API needs to accept POST bodies from clients that
    // ship either format. Returns null on unparseable input; returns
    // `Kind.Utc` on success.
    public bool TryParseUserLocalOrUtc(string? localDateTimeValue, string? timeZoneId, out DateTime? utcDateTime)
    {
        utcDateTime = null;

        if (string.IsNullOrWhiteSpace(localDateTimeValue))
        {
            return true;
        }

        var trimmed = localDateTimeValue.Trim();

        if (HasExplicitUtcOffset(trimmed)
            && DateTime.TryParse(
                trimmed,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal,
                out var parsedUtc))
        {
            utcDateTime = DateTime.SpecifyKind(parsedUtc, DateTimeKind.Utc);
            return true;
        }

        if (!DateTime.TryParseExact(
                trimmed,
                ["yyyy-MM-ddTHH:mm", "yyyy-MM-ddTHH:mm:ss"],
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var localDateTime))
        {
            return false;
        }

        utcDateTime = ToUtc(localDateTime, timeZoneId);
        return utcDateTime.HasValue;
    }

    private static bool HasExplicitUtcOffset(string value)
    {
        if (value.EndsWith("Z", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var tIndex = value.IndexOf('T');
        if (tIndex < 0)
        {
            return false;
        }

        for (var i = value.Length - 1; i > tIndex; i--)
        {
            var c = value[i];
            if (c == '+' || c == '-')
            {
                return true;
            }
            if (c == ':' || char.IsDigit(c))
            {
                continue;
            }
            return false;
        }

        return false;
    }
}
