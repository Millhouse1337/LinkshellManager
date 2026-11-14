using LinkshellManagerDiscordApp.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace LinkshellManagerDiscordApp.Controllers;

// One indirection for the launcher download, so that:
//   - the super-admin on/off switch is enforced server-side rather than merely hiding buttons, and
//   - every view links to one stable local URL, while the real address lives in server config.
public class LauncherController : Controller
{
    private readonly GlobalSettingsService _globalSettings;
    private readonly IConfiguration _configuration;
    private readonly ILogger<LauncherController> _logger;

    public LauncherController(GlobalSettingsService globalSettings, IConfiguration configuration, ILogger<LauncherController> logger)
    {
        _globalSettings = globalSettings;
        _configuration = configuration;
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

        var url = LauncherDownloadPolicy.Validate(_configuration[LauncherDownloadPolicy.ConfigKey]);
        if (url is null)
        {
            _logger.LogWarning("Launcher download is enabled but {Key} is missing or not an https address.", LauncherDownloadPolicy.ConfigKey);
            return NotFound();
        }

        // Off-site redirect to the release asset, so the droplet never serves the installer
        // itself. Not an open redirect: the address is fixed server config, never a request value.
        return Redirect(url);
    }
}
