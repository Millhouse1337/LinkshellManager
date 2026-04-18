using LinkshellManagerDiscordApp.Data;
using LinkshellManagerDiscordApp.Models;
using LinkshellManagerDiscordApp.ViewModels;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace LinkshellManagerDiscordApp.ViewComponents;

public class LinkshellDropdownViewComponent : ViewComponent
{
    private readonly ApplicationDbContext _context;
    private readonly UserManager<AppUser> _userManager;

    public LinkshellDropdownViewComponent(ApplicationDbContext context, UserManager<AppUser> userManager)
    {
        _context = context;
        _userManager = userManager;
    }

    public async Task<IViewComponentResult> InvokeAsync()
    {
        var user = await _userManager.GetUserAsync(UserClaimsPrincipal);
        if (user is null)
        {
            return View(new SettingsViewModel());
        }

        var userLinkshells = await _context.AppUserLinkshells
            .Where(link => link.AppUserId == user.Id)
            .Select(link => link.Linkshell!)
            .OrderBy(linkshell => linkshell.LinkshellName)
            .ToListAsync();

        return View(new SettingsViewModel
        {
            Linkshells = userLinkshells,
            SelectedLinkshellId = user.PrimaryLinkshellId
        });
    }
}
