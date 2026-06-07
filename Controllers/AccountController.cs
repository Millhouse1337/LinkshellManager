using LinkshellManagerDiscordApp.Data;
using LinkshellManagerDiscordApp.Models;
using LinkshellManagerDiscordApp.Services;
using LinkshellManagerDiscordApp.Utils;
using LinkshellManagerDiscordApp.ViewModels;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace LinkshellManagerDiscordApp.Controllers;

[Authorize]
public class AccountController : Controller
{
    private readonly UserManager<AppUser> _userManager;
    private readonly SignInManager<AppUser> _signInManager;
    private readonly ApplicationDbContext _context;
    private readonly AppUserProfileService _appUserProfileService;
    private readonly TimeZoneConversionService _timeZones;
    private readonly GlobalSettingsService _globalSettings;

    public AccountController(
        UserManager<AppUser> userManager,
        SignInManager<AppUser> signInManager,
        ApplicationDbContext context,
        AppUserProfileService appUserProfileService,
        TimeZoneConversionService timeZones,
        GlobalSettingsService globalSettings)
    {
        _userManager = userManager;
        _signInManager = signInManager;
        _context = context;
        _appUserProfileService = appUserProfileService;
        _timeZones = timeZones;
        _globalSettings = globalSettings;
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

        var jobLevelRows = await _context.AppUserLinkshells
            .Where(link => link.AppUserId == user.Id)
            .Select(link => new { link.LinkshellId, link.JobLevels })
            .ToListAsync();
        var hasLinkshell = jobLevelRows.Count > 0;
        var addonConfigured = await _context.AddonApiTokens
            .AnyAsync(token => token.IssuedToAppUserId == user.Id && token.RevokedAt == null);

        // Prefer the primary linkshell's stored job levels; otherwise any
        // membership that has them — it's the same character's jobs everywhere.
        var storedJobLevels =
            jobLevelRows.FirstOrDefault(row => row.LinkshellId == user.PrimaryLinkshellId)?.JobLevels
            ?? jobLevelRows.FirstOrDefault(row => row.JobLevels != null)?.JobLevels;

        return View(new ProfileViewModel
        {
            CharacterName = user.CharacterName,
            AltCharacterName1 = user.AltCharacterName1,
            AltCharacterName2 = user.AltCharacterName2,
            TimeZone = user.TimeZone,
            ProfileImageData = user.ProfileImage,
            AvailableTimeZones = CuratedTimeZones.BuildOrderedList(_timeZones.AvailableZoneIds),
            ProfileComplete = !string.IsNullOrWhiteSpace(user.CharacterName)
                && !string.IsNullOrWhiteSpace(user.TimeZone),
            HasLinkshell = hasLinkshell,
            AddonConfigured = addonConfigured,
            JobLevels = ProfileJobLevels.ToCatalogLevels(storedJobLevels),
            Alt1JobLevels = ProfileJobLevels.ToCatalogLevels(user.Alt1JobLevels),
            Alt2JobLevels = ProfileJobLevels.ToCatalogLevels(user.Alt2JobLevels)
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
        if (normalizedTimeZone is not null && !_timeZones.IsKnownZone(normalizedTimeZone))
        {
            ModelState.AddModelError(nameof(model.TimeZone), "Use a valid IANA time zone such as America/New_York.");
            model.ProfileImageData = user.ProfileImage;
            model.AvailableTimeZones = CuratedTimeZones.BuildOrderedList(_timeZones.AvailableZoneIds);
            model.HasLinkshell = await _context.AppUserLinkshells.AnyAsync(link => link.AppUserId == user.Id);
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
            model.AvailableTimeZones = CuratedTimeZones.BuildOrderedList(_timeZones.AvailableZoneIds);
            model.HasLinkshell = await _context.AppUserLinkshells.AnyAsync(link => link.AppUserId == user.Id);
            return View(nameof(Profile), model);
        }

        // Main-character job levels go on every membership (per-character, in the
        // addon's FFXI-job-id format). Alt job levels live on the account (alts
        // have no membership). One SaveChanges flushes both.
        if (model.JobLevels.Count > 0)
        {
            var memberships = await _context.AppUserLinkshells
                .Where(link => link.AppUserId == user.Id)
                .ToListAsync();
            foreach (var membership in memberships)
            {
                membership.JobLevels = ProfileJobLevels.MergeIntoStored(membership.JobLevels, model.JobLevels);
            }
        }

        // Alt job levels: clear when the alt has no name, otherwise save what was
        // posted (the grid only renders when the alt has a name).
        if (string.IsNullOrWhiteSpace(user.AltCharacterName1))
        {
            user.Alt1JobLevels = null;
        }
        else if (model.Alt1JobLevels.Count > 0)
        {
            user.Alt1JobLevels = ProfileJobLevels.MergeIntoStored(user.Alt1JobLevels, model.Alt1JobLevels);
        }

        if (string.IsNullOrWhiteSpace(user.AltCharacterName2))
        {
            user.Alt2JobLevels = null;
        }
        else if (model.Alt2JobLevels.Count > 0)
        {
            user.Alt2JobLevels = ProfileJobLevels.MergeIntoStored(user.Alt2JobLevels, model.Alt2JobLevels);
        }

        await _context.SaveChangesAsync();

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
            SelectedLinkshellId = user.PrimaryLinkshellId,
            IsSuperAdmin = user.IsSuperAdmin,
            AddonGloballyDisabled = user.IsSuperAdmin && await _globalSettings.IsAddonGloballyDisabledAsync()
        });
    }

    // Super-admin-only global kill-switch for the in-game addon. When disabled,
    // every addon API call is rejected and no new pairing codes can be issued
    // or redeemed (see GlobalSettingsService + AddonApiAuthService).
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SetAddonKillSwitch(bool disabled)
    {
        var user = await _userManager.GetUserAsync(User);
        if (user is null)
        {
            return Challenge();
        }

        if (!user.IsSuperAdmin)
        {
            return Forbid();
        }

        await _globalSettings.SetAddonGloballyDisabledAsync(disabled);
        TempData["AddonKillSwitchMessage"] = disabled
            ? "Addon disabled globally. No one can sync the addon until you re-enable it."
            : "Addon re-enabled globally.";
        return RedirectToAction(nameof(Settings));
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

