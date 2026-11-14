using Microsoft.AspNetCore.Mvc;

namespace LinkshellManagerDiscordApp.Controllers;

// The antiforgery token the app fetches before its first write.
public sealed partial class ActivityOverviewController
{
    [HttpGet("antiforgery")]
    public IActionResult GetAntiforgeryToken(
        [FromServices] Microsoft.AspNetCore.Antiforgery.IAntiforgery antiforgery)
    {
        var tokens = antiforgery.GetAndStoreTokens(HttpContext);
        return Ok(new
        {
            headerName = tokens.HeaderName,
            requestToken = tokens.RequestToken
        });
    }
}
