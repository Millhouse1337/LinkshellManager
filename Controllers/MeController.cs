using LinkshellManagerDiscordApp.Services;
using Microsoft.AspNetCore.Mvc;
using System.Net.Http.Headers;

namespace LinkshellManagerDiscordApp.Controllers;

[ApiController]
[Route("api/me")]
public sealed class MeController : ControllerBase
{
    private readonly DiscordIdentityService _discordIdentityService;
    private readonly IHostEnvironment _environment;

    public MeController(
        DiscordIdentityService discordIdentityService,
        IHostEnvironment environment)
    {
        _discordIdentityService = discordIdentityService;
        _environment = environment;
    }

    [HttpGet]
    public async Task<IActionResult> GetAsync(CancellationToken cancellationToken)
    {
        if (!TryGetBearerToken(out var accessToken))
        {
            return Unauthorized(new
            {
                error = "Missing Discord bearer token. Send Authorization: Bearer <access_token>."
            });
        }

        try
        {
            var user = await _discordIdentityService.GetCurrentLocalUserAsync(accessToken, cancellationToken);
            Response.Headers.CacheControl = "no-store";
            Response.Headers.Pragma = "no-cache";
            return Ok(user);
        }
        catch (InvalidOperationException)
        {
            return StatusCode(500, new { error = "Discord OAuth configuration is incomplete." });
        }
        catch (DiscordApiException ex)
        {
            return StatusCode(
                ex.StatusCode,
                new
                {
                    error = ex.Message,
                    details = _environment.IsDevelopment() ? ex.Details : null
                });
        }
    }

    private bool TryGetBearerToken(out string accessToken)
    {
        accessToken = string.Empty;

        if (!AuthenticationHeaderValue.TryParse(Request.Headers.Authorization, out var headerValue))
        {
            return false;
        }

        if (!"Bearer".Equals(headerValue.Scheme, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(headerValue.Parameter))
        {
            return false;
        }

        accessToken = headerValue.Parameter;
        return true;
    }
}
