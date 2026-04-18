using System.ComponentModel.DataAnnotations;
using LinkshellManagerDiscordApp.Services;
using Microsoft.AspNetCore.Mvc;

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
    public async Task<IActionResult> ExchangeAsync(
        [FromBody] DiscordCodeExchangeRequest request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Code))
        {
            return BadRequest(new { error = "Missing authorization code." });
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
            return StatusCode(
                ex.StatusCode,
                new
                {
                    error = ex.Message,
                    details = _environment.IsDevelopment() ? ex.Details : null
                });
        }
    }

    public sealed record DiscordCodeExchangeRequest([Required] string Code);
}
