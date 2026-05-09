namespace LinkshellManagerDiscordApp.Services;

// Mirrors the curated list in
// `discord-activity/src/app/home/sidebar-panel.helpers.ts` (CURATED_TIME_ZONES)
// so the web app's Profile dropdown shows the same zones in the same order
// as the Discord Activity.
//
// Web pages combine this with the full IANA zone list from
// `IDateTimeZoneProvider.Ids` to get the same superset the activity surfaces
// via `Intl.supportedValuesOf('timeZone')`.
public static class CuratedTimeZones
{
    public static readonly IReadOnlyList<string> Ids = new[]
    {
        "UTC",
        "America/New_York",
        "America/Chicago",
        "America/Denver",
        "America/Los_Angeles",
        "America/Phoenix",
        "America/Anchorage",
        "Pacific/Honolulu",
        "America/Toronto",
        "America/Vancouver",
        "America/Mexico_City",
        "America/Sao_Paulo",
        "America/Argentina/Buenos_Aires",
        "Europe/London",
        "Europe/Dublin",
        "Europe/Paris",
        "Europe/Berlin",
        "Europe/Madrid",
        "Europe/Rome",
        "Europe/Warsaw",
        "Europe/Helsinki",
        "Europe/Athens",
        "Europe/Istanbul",
        "Europe/Kyiv",
        "Africa/Johannesburg",
        "Asia/Dubai",
        "Asia/Kolkata",
        "Asia/Dhaka",
        "Asia/Bangkok",
        "Asia/Singapore",
        "Asia/Manila",
        "Asia/Hong_Kong",
        "Asia/Taipei",
        "Asia/Seoul",
        "Asia/Tokyo",
        "Australia/Perth",
        "Australia/Adelaide",
        "Australia/Sydney",
        "Pacific/Auckland"
    };

    // Curated zones first (in the canonical order), then any remaining IANA
    // zones from the supplied provider in alphabetical order. Matches the
    // dedup-while-preserving-order pattern used by the Discord Activity's
    // `resolveTimeZoneOptions`.
    public static IReadOnlyList<string> BuildOrderedList(IEnumerable<string> ianaZones)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var ordered = new List<string>(Ids.Count + 600);

        foreach (var id in Ids)
        {
            if (seen.Add(id))
            {
                ordered.Add(id);
            }
        }

        foreach (var id in ianaZones.OrderBy(value => value, StringComparer.Ordinal))
        {
            if (seen.Add(id))
            {
                ordered.Add(id);
            }
        }

        return ordered;
    }
}
