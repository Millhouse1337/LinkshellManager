using LinkshellManagerDiscordApp.Models;

namespace LinkshellManagerDiscordApp.Services;

// Which events GET /api/addon/events sends, and in what order.
//
// Only what the addon can use: HNM camps (the addon runs nothing else; timed events are run from
// the app) that have not ended. "Ended" is the addon's own test: started, then isLive gone false.
// Filtered in the query, before the cap. The addon used to drop the other rows itself, so a
// linkshell with 50 newer timed events got an empty camp list.
//
// Live camps first, so queued boards can never push a running camp past the cap. Ordering by
// CommencementStartTime alone put the queued rows first on Postgres, which sorts NULLs first in a
// descending order.
public static class AddonCampList
{
    public const int MaxRows = 50;

    public static IQueryable<Event> Query(IQueryable<Event> events, int linkshellId) =>
        events
            .Where(evt => evt.LinkshellId == linkshellId
                && evt.EventType != null
                && evt.EventType.Trim().ToUpper() == "HNM"
                && (evt.CommencementStartTime == null || evt.EndTime == null))
            .OrderByDescending(evt => evt.CommencementStartTime != null)
            .ThenByDescending(evt => evt.CommencementStartTime)
            .ThenByDescending(evt => evt.StartTime)
            .ThenByDescending(evt => evt.Id)
            .Take(MaxRows);
}
