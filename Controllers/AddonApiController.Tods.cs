using LinkshellManagerDiscordApp.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace LinkshellManagerDiscordApp.Controllers;

public sealed partial class AddonApiController
{
    // Read-only ToD list for the in-game ToD Tracker window. Returns the
    // newest ToD per monster for the caller's linkshell so the addon can
    // show "next repop" + cycle the spawn windows. Read path mirrors
    // ListEventsAsync: [AddonApiAuth] only (any paired linkshell member);
    // posting a ToD still goes through the permission-checked POST /tod.
    //
    // EVERY monster the linkshell has logged, not just the curated HNMs. The
    // old filter was an exact-name match against LongWindow ∪ ShortWindow,
    // which silently dropped the combined "Base/Stronger" labels every HNM
    // board writes ("Fafnir/Nidhogg", "Adamantoise/Aspidochelone") — the
    // in-game tracker showed one row where the web app showed a dozen. Rather
    // than widen the match to MonsterMatchNames we drop it entirely, so the
    // addon lists the same set the web ToDs tab does. The addon supplies its
    // own spawn-window cadence where it knows one and falls back to a plain
    // repop countdown where it doesn't.
    [HttpGet("tods")]
    [AddonApiAuth]
    public async Task<IActionResult> ListTodsAsync(CancellationToken cancellationToken)
    {
        var linkshellId = AddonApiAuthAttribute.GetLinkshellId(HttpContext);

        // Per-linkshell hidden-mob list (Activity Customize -> "Hide ToD Mobs").
        // The same filter the MVC ToD page and the Discord pop board honor, so a
        // monster hidden from the web trackers stays hidden in-game too.
        var hiddenRaw = await _dbContext.Linkshells
            .AsNoTracking()
            .Where(ls => ls.Id == linkshellId)
            .Select(ls => ls.HiddenTodMonsters)
            .FirstOrDefaultAsync(cancellationToken) ?? string.Empty;
        var hidden = new HashSet<string>(
            hiddenRaw.Split('|', StringSplitOptions.RemoveEmptyEntries)
                     .Select(name => name.Trim())
                     .Where(name => name.Length > 0),
            StringComparer.OrdinalIgnoreCase);

        // Pull this linkshell's ToDs newest-first, materialize, then collapse
        // to the newest per monster in-process (case-insensitive name group +
        // HashSet.Contains don't translate cleanly across EF providers).
        //
        // Rows with no RepopTime are INCLUDED: a camp that ended without anyone seeing the mob die
        // records neither a time nor a repop, and the in-game panels render that as "Not entered".
        // Filtering them out here would leave the addon showing the superseded pop's countdown.
        // Ordered by Time ?? TimeStamp so such a row still wins its monster group.
        var rows = await _dbContext.Tods
            .AsNoTracking()
            .Where(t => t.LinkshellId == linkshellId
                        && t.MonsterName != null)
            .OrderByDescending(t => t.Time ?? t.TimeStamp)
            .ThenByDescending(t => t.Id)
            .Select(t => new
            {
                t.Id,
                t.MonsterName,
                t.DayNumber,
                t.Time,
                t.RepopTime,
                t.Cooldown,
                t.Interval
            })
            .ToListAsync(cancellationToken);

        var tods = rows
            .Where(r => !hidden.Contains(r.MonsterName!.Trim()))
            .GroupBy(r => r.MonsterName!.Trim(), StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())   // newest per monster (source order preserved)
            .Select(r => new
            {
                id = r.Id,
                monsterName = r.MonsterName,
                dayNumber = r.DayNumber,
                defeatedAtUtc = r.Time,
                repopTimeUtc = r.RepopTime,
                cooldown = r.Cooldown,
                interval = r.Interval
            })
            .ToList();

        return Ok(new { tods });
    }
}
