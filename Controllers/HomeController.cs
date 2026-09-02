using System.Diagnostics;
using LinkshellManagerDiscordApp.Models;
using LinkshellManagerDiscordApp.Services;
using Microsoft.AspNetCore.Mvc;

namespace LinkshellManagerDiscordApp.Controllers;

public class HomeController : Controller
{
    private readonly ILogger<HomeController> _logger;
    private readonly GlobalSettingsService _globalSettings;

    public HomeController(ILogger<HomeController> logger, GlobalSettingsService globalSettings)
    {
        _logger = logger;
        _globalSettings = globalSettings;
    }

    public async Task<IActionResult> Index()
    {
        if (IsDiscordEmbeddedRequest())
        {
            return Redirect("/discord-activity");
        }

        // Set here as well as in _Layout: a view body renders before its layout, so Index.cshtml
        // cannot see what _Layout writes. This is the only anonymous-reachable surface, so it is
        // where a tester without a Discord account finds the launcher.
        ViewData["ShowLauncherDownload"] = await _globalSettings.IsLauncherDownloadEnabledAsync(HttpContext.RequestAborted);
        return View();
    }

    [Route("privacy")]
    public IActionResult Privacy()
    {
        return View();
    }

    [Route("terms")]
    public IActionResult Terms()
    {
        return View();
    }

    [ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
    public IActionResult Error()
    {
        return View(new ErrorViewModel { RequestId = Activity.Current?.Id ?? HttpContext.TraceIdentifier });
    }

    private bool IsDiscordEmbeddedRequest()
    {
        var headers = Request.Headers;
        var fetchDest = headers["Sec-Fetch-Dest"].ToString();
        var userAgent = headers.UserAgent.ToString();

        if (IsDiscordHost(headers.Referer.ToString()) || IsDiscordHost(headers.Origin.ToString()))
        {
            return true;
        }

        if ("iframe".Equals(fetchDest, StringComparison.OrdinalIgnoreCase) &&
            userAgent.Contains("Discord", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return false;
    }

    private static bool IsDiscordHost(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || !Uri.TryCreate(value, UriKind.Absolute, out var uri))
        {
            return false;
        }

        return uri.Host.Equals("discord.com", StringComparison.OrdinalIgnoreCase) ||
               uri.Host.EndsWith(".discord.com", StringComparison.OrdinalIgnoreCase) ||
               uri.Host.EndsWith(".discordsays.com", StringComparison.OrdinalIgnoreCase);
    }
}
