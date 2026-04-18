using System.Security.Claims;
using LinkshellManagerDiscordApp.Models;
using LinkshellManagerDiscordApp.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;

namespace LinkshellManagerDiscordApp.Controllers;

[AllowAnonymous]
[Route("auth/discord")]
public sealed class DiscordAccountController : Controller
{
    private readonly DiscordIdentityService _discordIdentityService;
    private readonly SignInManager<AppUser> _signInManager;
    private readonly UserManager<AppUser> _userManager;

    public DiscordAccountController(
        DiscordIdentityService discordIdentityService,
        SignInManager<AppUser> signInManager,
        UserManager<AppUser> userManager)
    {
        _discordIdentityService = discordIdentityService;
        _signInManager = signInManager;
        _userManager = userManager;
    }

    [HttpGet("login")]
    public IActionResult Login(string? returnUrl = null)
    {
        var redirectUrl = Url.Action(nameof(Callback), new { returnUrl }) ?? "/auth/discord/callback";
        var properties = _signInManager.ConfigureExternalAuthenticationProperties("DiscordWebsite", redirectUrl);
        return Challenge(properties, "DiscordWebsite");
    }

    [HttpGet("callback")]
    public async Task<IActionResult> Callback(string? returnUrl = null, string? remoteError = null, CancellationToken cancellationToken = default)
    {
        returnUrl = string.IsNullOrWhiteSpace(returnUrl) || !Url.IsLocalUrl(returnUrl)
            ? Url.Content("~/")
            : returnUrl;

        if (!string.IsNullOrWhiteSpace(remoteError))
        {
            TempData["Error"] = $"Discord sign-in failed: {remoteError}";
            return LocalRedirect("/Identity/Account/Login");
        }

        var externalResult = await HttpContext.AuthenticateAsync(IdentityConstants.ExternalScheme);
        if (!externalResult.Succeeded || externalResult.Principal is null)
        {
            TempData["Error"] = "Discord sign-in could not be completed.";
            return LocalRedirect("/Identity/Account/Login");
        }

        var principal = externalResult.Principal;
        var discordUserId = principal.FindFirstValue(ClaimTypes.NameIdentifier);
        var username = principal.FindFirstValue("urn:discord:username");

        if (string.IsNullOrWhiteSpace(discordUserId) || string.IsNullOrWhiteSpace(username))
        {
            await HttpContext.SignOutAsync(IdentityConstants.ExternalScheme);
            TempData["Error"] = "Discord did not return a usable user identity.";
            return LocalRedirect("/Identity/Account/Login");
        }

        var currentUser = User.Identity?.IsAuthenticated == true
            ? await _userManager.GetUserAsync(User)
            : null;

        try
        {
            var result = await _discordIdentityService.ResolveWebsiteSignInAsync(
                new DiscordIdentityProfile(
                    discordUserId,
                    username,
                    principal.FindFirstValue("urn:discord:discriminator") ?? "0",
                    principal.FindFirstValue("urn:discord:global_name"),
                    principal.FindFirstValue("urn:discord:avatar")),
                currentUser,
                cancellationToken);

            await HttpContext.SignOutAsync(IdentityConstants.ExternalScheme);

            if (currentUser is not null && !string.Equals(currentUser.Id, result.AppUser.Id, StringComparison.Ordinal))
            {
                await _signInManager.SignOutAsync();
            }

            await _signInManager.SignInAsync(result.AppUser, isPersistent: false);
            return LocalRedirect(returnUrl);
        }
        catch (DiscordLinkConflictException ex)
        {
            await HttpContext.SignOutAsync(IdentityConstants.ExternalScheme);
            TempData["Error"] = ex.Message;
            return LocalRedirect("/Identity/Account/Login");
        }
    }
}
