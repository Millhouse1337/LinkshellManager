using LinkshellManagerDiscordApp.Data;
using LinkshellManagerDiscordApp.Models;
using LinkshellManagerDiscordApp.ViewModels;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace LinkshellManagerDiscordApp.Controllers;

[Authorize]
public sealed class LinkshellAttendanceSnapshotsController : Controller
{
    private const int MaxRecentSnapshots = 100;

    private readonly ApplicationDbContext _db;
    private readonly UserManager<AppUser> _userManager;

    public LinkshellAttendanceSnapshotsController(ApplicationDbContext db, UserManager<AppUser> userManager)
    {
        _db = db;
        _userManager = userManager;
    }

    [HttpGet("/linkshells/{linkshellId:int}/attendance-snapshots")]
    public async Task<IActionResult> Index(int linkshellId, CancellationToken cancellationToken)
    {
        var user = await _userManager.GetUserAsync(User);
        if (user is null) return Challenge();

        var isMember = await _db.AppUserLinkshells
            .AsNoTracking()
            .AnyAsync(link => link.AppUserId == user.Id && link.LinkshellId == linkshellId, cancellationToken);
        if (!isMember) return Forbid();

        var linkshell = await _db.Linkshells
            .AsNoTracking()
            .Where(l => l.Id == linkshellId)
            .Select(l => new { l.LinkshellName })
            .FirstOrDefaultAsync(cancellationToken);
        if (linkshell is null) return NotFound();

        // Pull the most recent N snapshots with their entries pre-loaded.
        // Each snapshot caps at 18 entries (alliance max), so eager-loading is
        // safe and avoids per-row queries on the view.
        var snapshots = await _db.AttendanceSnapshots
            .AsNoTracking()
            .Where(s => s.LinkshellId == linkshellId)
            .OrderByDescending(s => s.CapturedAtUtc)
            .Take(MaxRecentSnapshots)
            .Include(s => s.Entries)
            .ToListAsync(cancellationToken);

        var rows = snapshots.Select(s =>
        {
            var entries = s.Entries
                .OrderBy(e => e.CharacterName, StringComparer.OrdinalIgnoreCase)
                .Select(e => new AttendanceSnapshotEntryRow
                {
                    CharacterName = e.CharacterName,
                    MainJob = e.MainJob,
                    MainJobLevel = e.MainJobLevel,
                    SubJob = e.SubJob,
                    SubJobLevel = e.SubJobLevel,
                    Zone = e.Zone,
                })
                .ToList();

            // Pick the most common zone among entries as the snapshot's
            // primary zone for the header line. Mixed-zone alliances are rare
            // but the mode (most-frequent) is the most representative value.
            var primaryZone = entries
                .Where(e => !string.IsNullOrWhiteSpace(e.Zone))
                .GroupBy(e => e.Zone!, StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(g => g.Count())
                .ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.Key)
                .FirstOrDefault();

            return new AttendanceSnapshotRow
            {
                Id = s.Id,
                CapturedAtUtc = s.CapturedAtUtc,
                CapturedByCharacterName = s.CapturedByCharacterName,
                UtcOffset = s.UtcOffset,
                EntryCount = s.EntryCount,
                PrimaryZone = primaryZone,
                Entries = entries,
            };
        }).ToList();

        var viewModel = new LinkshellAttendanceSnapshotsViewModel
        {
            LinkshellId = linkshellId,
            LinkshellName = linkshell.LinkshellName,
            Snapshots = rows,
        };
        return View(viewModel);
    }
}
