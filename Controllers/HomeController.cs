using System.Diagnostics;
using LinkshellManagerDiscordApp.Models;
using Microsoft.AspNetCore.Mvc;

namespace LinkshellManagerDiscordApp.Controllers
{
    public class HomeController : Controller
    {
        private readonly ILogger<HomeController> _logger;

        public HomeController(ILogger<HomeController> logger)
        {
            _logger = logger;
        }

        public IActionResult Index()
        {
            if (IsDiscordEmbeddedRequest())
            {
                return Redirect("/discord-activity");
            }

            return View();
        }

        public IActionResult Privacy()
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
}
