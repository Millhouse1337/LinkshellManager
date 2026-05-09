using LinkshellManagerDiscordApp.Data;
using LinkshellManagerDiscordApp.Models;
using LinkshellManagerDiscordApp.Services;
using LinkshellManagerDiscordApp.ViewModels;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NodaTime;

namespace LinkshellManagerDiscordApp.Controllers;

[Authorize]
public class AccountController : Controller
{
    private readonly UserManager<AppUser> _userManager;
    private readonly SignInManager<AppUser> _signInManager;
    private readonly ApplicationDbContext _context;
    private readonly AppUserProfileService _appUserProfileService;
    private readonly IDateTimeZoneProvider _dateTimeZoneProvider;

    public AccountController(
        UserManager<AppUser> userManager,
        SignInManager<AppUser> signInManager,
        ApplicationDbContext context,
        AppUserProfileService appUserProfileService,
        IDateTimeZoneProvider dateTimeZoneProvider)
    {
        _userManager = userManager;
        _signInManager = signInManager;
        _context = context;
        _appUserProfileService = appUserProfileService;
        _dateTimeZoneProvider = dateTimeZoneProvider;
    }

    [AllowAnonymous]
    public IActionResult Login() => Redirect("/Identity/Account/Login");

    [AllowAnonymous]
    public IActionResult Register() => Redirect("/Identity/Account/Login");

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Logout()
    {
        await _signInManager.SignOutAsync();
        return RedirectToAction(nameof(Login));
    }
    public async Task<IActionResult> Profile()
    {
        var user = await _userManager.GetUserAsync(User);
        if (user is null)
        {
            return Challenge();
        }

        var hasLinkshell = await _context.AppUserLinkshells
            .AnyAsync(link => link.AppUserId == user.Id);
        var addonConfigured = await _context.AddonApiTokens
            .AnyAsync(token => token.IssuedToAppUserId == user.Id && token.RevokedAt == null);

        return View(new ProfileViewModel
        {
            CharacterName = user.CharacterName,
            AltCharacterName1 = user.AltCharacterName1,
            AltCharacterName2 = user.AltCharacterName2,
            TimeZone = user.TimeZone,
            ProfileImageData = user.ProfileImage,
            AvailableTimeZones = CuratedTimeZones.BuildOrderedList(_dateTimeZoneProvider.Ids),
            ProfileComplete = !string.IsNullOrWhiteSpace(user.CharacterName)
                && !string.IsNullOrWhiteSpace(user.TimeZone),
            HasLinkshell = hasLinkshell,
            AddonConfigured = addonConfigured
        });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> UpdateProfile(ProfileViewModel model)
    {
        var user = await _userManager.GetUserAsync(User);
        if (user is null)
        {
            return Challenge();
        }

        var normalizedTimeZone = string.IsNullOrWhiteSpace(model.TimeZone) ? null : model.TimeZone.Trim();
        if (normalizedTimeZone is not null && !_dateTimeZoneProvider.Ids.Contains(normalizedTimeZone))
        {
            ModelState.AddModelError(nameof(model.TimeZone), "Use a valid IANA time zone such as America/New_York.");
            model.ProfileImageData = user.ProfileImage;
            model.AvailableTimeZones = CuratedTimeZones.BuildOrderedList(_dateTimeZoneProvider.Ids);
            return View(nameof(Profile), model);
        }

        var result = await _appUserProfileService.UpdateProfileAsync(
            user,
            model.CharacterName,
            normalizedTimeZone,
            model.AltCharacterName1,
            model.AltCharacterName2,
            preserveExistingAlts: false);

        if (!result.Succeeded)
        {
            foreach (var error in result.Errors)
            {
                ModelState.AddModelError(string.Empty, error.Description);
            }

            model.ProfileImageData = user.ProfileImage;
            model.AvailableTimeZones = CuratedTimeZones.BuildOrderedList(_dateTimeZoneProvider.Ids);
            return View(nameof(Profile), model);
        }

        return RedirectToAction(nameof(Profile));
    }
    public async Task<IActionResult> Settings()
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

        return View(new SettingsViewModel
        {
            Linkshells = linkshells,
            SelectedLinkshellId = user.PrimaryLinkshellId
        });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SetPrimaryLinkshell(SettingsViewModel model)
    {
        var user = await _userManager.GetUserAsync(User);
        if (user is null)
        {
            return Challenge();
        }

        if (model.SelectedLinkshellId.HasValue)
        {
            var selectedLinkshell = await _context.AppUserLinkshells
                .Where(link => link.AppUserId == user.Id && link.LinkshellId == model.SelectedLinkshellId.Value)
                .Select(link => link.Linkshell)
                .FirstOrDefaultAsync();

            if (selectedLinkshell is null)
            {
                return Forbid();
            }

            user.PrimaryLinkshellId = selectedLinkshell.Id;
            user.PrimaryLinkshellName = selectedLinkshell.LinkshellName;
        }
        else
        {
            user.PrimaryLinkshellId = null;
            user.PrimaryLinkshellName = null;
        }

        await _userManager.UpdateAsync(user);

        return RedirectToAction(nameof(Settings));
    }
}

