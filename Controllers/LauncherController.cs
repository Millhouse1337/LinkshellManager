using LinkshellManagerDiscordApp.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace LinkshellManagerDiscordApp.Controllers;

// One indirection for the launcher download, so that:
//   - the super-admin toggle is enforced server-side rather than merely hiding buttons, and
//   - every view links to one stable local URL while the real release URL changes each build.
public class LauncherController : Controller
{
    private readonly GlobalSettingsService _globalSettings;
    private readonly ILogger<LauncherController> _logger;

    public LauncherController(GlobalSettingsService globalSettings, ILogger<LauncherController> logger)
    {
        _globalSettings = globalSettings;
        _logger = logger;
    }

    // Anonymous on purpose: a tester needs the launcher before they have a Discord account,
    // and the home page that links here is itself reachable signed-out.
    [AllowAnonymous]
    [HttpGet("/download/launcher")]
    public async Task<IActionResult> Download(CancellationToken cancellationToken)
    {
        if (!await _globalSettings.IsLauncherDownloadEnabledAsync(cancellationToken))
        {
            return NotFound();
        }

        var url = await _globalSettings.GetLauncherDownloadUrlAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(url))
        {
            _logger.LogWarning("Launcher download is enabled but no URL is configured.");
            return NotFound();
        }

        // Off-site redirect to the release asset, so the droplet never serves the installer
        // itself. Not an open redirect: only a super admin can set this value, and
        // AccountController.SetLauncherDownloadUrl rejects anything that is not absolute https.
        return Redirect(url);
    }
}
