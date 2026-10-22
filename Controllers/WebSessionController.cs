using LinkshellManagerDiscordApp.Models;
using LinkshellManagerDiscordApp.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;

namespace LinkshellManagerDiscordApp.Controllers;

// The website's own session, for the Angular app when it runs in a normal browser (no
// Discord). Cookie only: inside Discord the Activity signs in with a Discord token instead
// (/auth/discord/exchange + /api/me), and never calls these.
//
//   GET  session  — anonymous. Tells the app whether to show its sign-in screen, and which
//                   website-only tabs this account may see (isSuperAdmin).
//   POST sign-out — the JSON twin of AccountController.Logout (a Razor form post). The
//                   global CookieAuthAntiforgeryFilter requires the XSRF header here.
[ApiController]
[Route("api/web")]
public sealed class WebSessionController : ControllerBase
{
    private readonly UserManager<AppUser> _userManager;
    private readonly SignInManager<AppUser> _signInManager;
    private readonly GlobalSettingsService _globalSettings;

    public WebSessionController(
        UserManager<AppUser> userManager,
        SignInManager<AppUser> signInManager,
        GlobalSettingsService globalSettings)
    {
        _userManager = userManager;
        _signInManager = signInManager;
        _globalSettings = globalSettings;
    }

    [AllowAnonymous]
    [HttpGet("session")]
    public async Task<IActionResult> GetSessionAsync(CancellationToken cancellationToken)
    {
        Response.Headers.CacheControl = "no-store";
        Response.Headers.Pragma = "no-cache";

        var launcherDownloadEnabled = await _globalSettings.IsLauncherDownloadEnabledAsync(cancellationToken);

        // GetUserAsync, not just IsAuthenticated: a cookie for a deleted user reads as signed out.
        var user = User.Identity?.IsAuthenticated == true ? await _userManager.GetUserAsync(User) : null;
        if (user is null)
        {
            return Ok(new WebSessionDto(false, null, false, launcherDownloadEnabled));
        }

        var displayName = string.IsNullOrWhiteSpace(user.CharacterName) ? user.UserName : user.CharacterName;
        return Ok(new WebSessionDto(true, displayName, user.IsSuperAdmin, launcherDownloadEnabled));
    }

    [Authorize]
    [HttpPost("sign-out")]
    public async Task<IActionResult> SignOutAsync()
    {
        await _signInManager.SignOutAsync();
        return NoContent();
    }
}

public sealed record WebSessionDto(
    bool SignedIn,
    string? DisplayName,
    bool IsSuperAdmin,
    bool LauncherDownloadEnabled);
