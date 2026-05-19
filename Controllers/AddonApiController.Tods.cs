using LinkshellManagerDiscordApp.Authorization;
using LinkshellManagerDiscordApp.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace LinkshellManagerDiscordApp.Controllers;

public sealed partial class AddonApiController
{
    // Read-only ToD list for the in-game ToD Tracker window. Returns the
    // newest ToD per tracked HNM for the caller's linkshell so the addon can
    // show "next repop" + cycle the spawn windows. Read path mirrors
    // ListEventsAsync: [AddonApiAuth] only (any paired linkshell member);
    // posting a ToD still goes through the permission-checked POST /tod.
    [HttpGet("tods")]
    [AddonApiAuth]
    public async Task<IActionResult> ListTodsAsync(CancellationToken cancellationToken)
    {
        var linkshellId = AddonApiAuthAttribute.GetLinkshellId(HttpContext);

        // Tracked set = LongWindow ∪ ShortWindow HNMs — one source of truth
        // with the addon's TOD_TRACKER_TIMING table, sourced from HnmConfig.
        var tracked = new HashSet<string>(
            HnmConfig.LongWindowHnms.Concat(HnmConfig.ShortWindowHnms),
            StringComparer.OrdinalIgnoreCase);

        // Pull this linkshell's ToDs newest-first, materialize, then collapse
        // to the newest per monster in-process (case-insensitive name group +
        // HashSet.Contains don't translate cleanly across EF providers).
        var rows = await _dbContext.Tods
            .AsNoTracking()
            .Where(t => t.LinkshellId == linkshellId
                        && t.MonsterName != null
                        && t.RepopTime != null)
            .OrderByDescending(t => t.Time)
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
            .Where(r => tracked.Contains(r.MonsterName!.Trim()))
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
