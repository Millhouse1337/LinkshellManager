using LinkshellManagerDiscordApp.Data;
using LinkshellManagerDiscordApp.Models;
using LinkshellManagerDiscordApp.ViewModels;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace LinkshellManagerDiscordApp.Controllers;

[Authorize]
public class DashboardController : Controller
{
    private readonly ApplicationDbContext _context;
    private readonly UserManager<AppUser> _userManager;

    public DashboardController(ApplicationDbContext context, UserManager<AppUser> userManager)
    {
        _context = context;
        _userManager = userManager;
    }
    public async Task<IActionResult> Index(int? linkshellId = null)
    {
        var user = await _userManager.GetUserAsync(User);
        if (user is null)
        {
            return Challenge();
        }

        var linkshells = await _context.AppUserLinkshells
            .Where(link => link.AppUserId == user.Id)
            .Select(link => link.Linkshell!)
            .OrderBy(linkshell => linkshell.LinkshellName)
            .ToListAsync();

        var selectedLinkshellId = linkshellId;
        if (selectedLinkshellId.HasValue && linkshells.All(linkshell => linkshell.Id != selectedLinkshellId.Value))
        {
            selectedLinkshellId = null;
        }

        selectedLinkshellId ??= user.PrimaryLinkshellId;
        if (selectedLinkshellId.HasValue && linkshells.All(linkshell => linkshell.Id != selectedLinkshellId.Value))
        {
            selectedLinkshellId = null;
        }

        selectedLinkshellId ??= linkshells.FirstOrDefault()?.Id;
        var members = selectedLinkshellId.HasValue
            ? await _context.AppUserLinkshells
                .Include(link => link.AppUser)
                .Where(link => link.LinkshellId == selectedLinkshellId.Value)
                .OrderBy(link => link.CharacterName)
                .ToListAsync()
            : new List<AppUserLinkshell>();

        var events = selectedLinkshellId.HasValue
            ? await _context.Events
                .Where(evt => evt.LinkshellId == selectedLinkshellId.Value)
                .OrderBy(evt => evt.StartTime)
                .Take(10)
                .ToListAsync()
            : new List<Event>();

        var eventHistories = selectedLinkshellId.HasValue
            ? await _context.EventHistories
                .Where(history => history.LinkshellId == selectedLinkshellId.Value)
                .OrderByDescending(history => history.EndTime ?? history.TimeStamp)
                .Take(10)
                .ToListAsync()
            : new List<EventHistory>();

        var selectedLinkshellName = linkshells.FirstOrDefault(linkshell => linkshell.Id == selectedLinkshellId)?.LinkshellName;

        return View(new DashboardViewModel
        {
            Linkshells = linkshells,
            SelectedLinkshellId = selectedLinkshellId,
            SelectedLinkshellName = selectedLinkshellName,
            Members = members,
            Events = events,
            EventHistories = eventHistories,
            TotalMembers = members.Count,
            UpcomingEvents = events.Count(evt => evt.CommencementStartTime is null),
            CompletedEvents = eventHistories.Count
        });
    }
}

