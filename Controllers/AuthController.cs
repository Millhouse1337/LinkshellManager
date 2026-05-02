using System.ComponentModel.DataAnnotations;
using LinkshellManagerDiscordApp.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace LinkshellManagerDiscordApp.Controllers;

[ApiController]
[Route("auth/discord")]
public sealed class AuthController : ControllerBase
{
    private readonly DiscordIdentityService _discordIdentityService;
    private readonly ILogger<AuthController> _logger;
    private readonly IHostEnvironment _environment;

    public AuthController(
        DiscordIdentityService discordIdentityService,
        ILogger<AuthController> logger,
        IHostEnvironment environment)
    {
        _discordIdentityService = discordIdentityService;
        _logger = logger;
        _environment = environment;
    }

    [HttpPost("exchange")]
    [EnableRateLimiting("oauth-exchange")]
    public async Task<IActionResult> ExchangeAsync(
        [FromBody] DiscordCodeExchangeRequest request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Code))
        {
            return BadRequest(new { error = "Missing authorization code." });
        }

        if (request.Code.Length > 256)
        {
            return BadRequest(new { error = "Authorization code is malformed." });
        }

        if (!_discordIdentityService.IsConfigured(out var configIssues))
        {
            _logger.LogError("Discord OAuth configuration is invalid: {Issues}", configIssues);
            return StatusCode(500, new { error = "Discord OAuth configuration is incomplete." });
        }

        Response.Headers.CacheControl = "no-store";
        Response.Headers.Pragma = "no-cache";

        try
        {
            var result = await _discordIdentityService.ExchangeCodeAsync(request.Code, cancellationToken);

            return Ok(new
            {
                result.AccessToken,
                result.ExpiresIn,
                result.Scope,
                result.TokenType,
                LocalUser = result.LocalUser
            });
        }
        catch (DiscordApiException ex)
        {
            // Don't surface upstream Discord error bodies to the client even in
            // development — they may leak the submitted authorization code.
            // Clamp upstream StatusCode to a sane HTTP range so transport-level
            // failures (StatusCode == 0) don't blow up the response pipeline.
            var status = ex.StatusCode is >= 400 and <= 599 ? ex.StatusCode : 502;
            return StatusCode(
                status,
                new { error = ex.Message });
        }
    }

    public sealed record DiscordCodeExchangeRequest([Required] string Code);
}
