using LinkshellManagerDiscordApp.Data;
using LinkshellManagerDiscordApp.Models;
using LinkshellManagerDiscordApp.ViewModels;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NodaTime;

namespace LinkshellManagerDiscordApp.Controllers;

[Authorize]
public class EventHistoryController : Controller
{
    private readonly ApplicationDbContext _context;
    private readonly UserManager<AppUser> _userManager;
    private readonly IDateTimeZoneProvider _dateTimeZoneProvider;

    public EventHistoryController(
        ApplicationDbContext context,
        UserManager<AppUser> userManager,
        IDateTimeZoneProvider dateTimeZoneProvider)
    {
        _context = context;
        _userManager = userManager;
        _dateTimeZoneProvider = dateTimeZoneProvider;
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
        return View(history);
    }

    private DateTime? ConvertUtcToUserTimeZone(DateTime? utcDateTime, string? timeZoneId)
    {
        if (!utcDateTime.HasValue)
        {
            return null;
        }

        var instant = Instant.FromDateTimeUtc(DateTime.SpecifyKind(utcDateTime.Value, DateTimeKind.Utc));
        var zone = ResolveTimeZone(timeZoneId);
        return instant.InZone(zone).ToDateTimeUnspecified();
    }

    private DateTimeZone ResolveTimeZone(string? timeZoneId)
    {
        if (!string.IsNullOrWhiteSpace(timeZoneId) && _dateTimeZoneProvider.Ids.Contains(timeZoneId))
        {
            return _dateTimeZoneProvider[timeZoneId];
        }

        return DateTimeZone.Utc;
    }
}

