using LinkshellManagerDiscordApp.Data;
using LinkshellManagerDiscordApp.Models;
using LinkshellManagerDiscordApp.Services;
using LinkshellManagerDiscordApp.ViewModels;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace LinkshellManagerDiscordApp.Controllers;

[Authorize]
public class EventHistoryController : Controller
{
    private readonly ApplicationDbContext _context;
    private readonly UserManager<AppUser> _userManager;
    private readonly TimeZoneConversionService _timeZones;

    public EventHistoryController(
        ApplicationDbContext context,
        UserManager<AppUser> userManager,
        TimeZoneConversionService timeZones)
    {
        _context = context;
        _userManager = userManager;
        _timeZones = timeZones;
    }
    public async Task<IActionResult> Index()
    {
        var user = await _userManager.GetUserAsync(User);
        if (user is null)
        {
            return Challenge();
        }

        var linkshellIds = await _context.AppUserLinkshells
            .Where(link => link.AppUserId == user.Id)
            .Select(link => link.LinkshellId)
            .Distinct()
            .ToListAsync();

        var histories = await _context.EventHistories
            .Where(history =>
                linkshellIds.Contains(history.LinkshellId) &&
                (!user.PrimaryLinkshellId.HasValue || history.LinkshellId == user.PrimaryLinkshellId.Value))
            .OrderByDescending(history => history.EndTime ?? history.TimeStamp)
            .ToListAsync();

        foreach (var history in histories)
        {
            history.StartTime = ConvertUtcToUserTimeZone(history.StartTime, user.TimeZone);
            history.EndTime = ConvertUtcToUserTimeZone(history.EndTime, user.TimeZone);
        }

        return View(histories);
    }
    public async Task<IActionResult> Details(int id)
    {
        var user = await _userManager.GetUserAsync(User);
        if (user is null)
        {
            return Challenge();
        }

        var history = await _context.EventHistories
            .Include(item => item.AppUserEventHistories)
            .FirstOrDefaultAsync(item => item.Id == id);

        if (history is null)
        {
            return NotFound();
        }

        var hasAccess = await _context.AppUserLinkshells
            .AnyAsync(link => link.AppUserId == user.Id && link.LinkshellId == history.LinkshellId);
        if (!hasAccess)
        {
            return Forbid();
        }

        history.StartTime = ConvertUtcToUserTimeZone(history.StartTime, user.TimeZone);
        history.EndTime = ConvertUtcToUserTimeZone(history.EndTime, user.TimeZone);
        // Leaders/Officers can reconcile per-member active-status credit on this page.
        ViewBag.CanReconcileActive = await CanManageAsync(user.Id, history.LinkshellId);
        return View(history);
    }

    // Reconcile active-status credit: leadership (un)checks which attendees earned
    // credit toward active-member status for this event. Posted "credited" carries
    // the checked AppUserEventHistory row ids; everything else is set uncredited.
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SaveActiveCredits(int id, int[]? credited)
    {
        var user = await _userManager.GetUserAsync(User);
        if (user is null)
        {
            return Challenge();
        }

        var history = await _context.EventHistories
            .Include(item => item.AppUserEventHistories)
            .FirstOrDefaultAsync(item => item.Id == id);
        if (history is null)
        {
            return NotFound();
        }
        if (!await CanManageAsync(user.Id, history.LinkshellId))
        {
            return Forbid();
        }

        var creditedSet = (credited ?? Array.Empty<int>()).ToHashSet();
        foreach (var row in history.AppUserEventHistories)
        {
            row.ActiveCredit = creditedSet.Contains(row.Id);
        }
        await _context.SaveChangesAsync();

        TempData["EventHistoryStatus"] = "Active-status credit updated.";
        return RedirectToAction(nameof(Details), new { id });
    }

    private async Task<bool> CanManageAsync(string appUserId, int linkshellId)
    {
        var membership = await _context.AppUserLinkshells
            .AsNoTracking()
            .FirstOrDefaultAsync(link => link.AppUserId == appUserId && link.LinkshellId == linkshellId);
        return membership is not null && LinkshellRanks.IsLeaderOrOfficer(membership.Rank);
    }

    private DateTime? ConvertUtcToUserTimeZone(DateTime? utcDateTime, string? timeZoneId)
        => _timeZones.ToUserTime(utcDateTime, timeZoneId);
}

