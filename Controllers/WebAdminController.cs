using LinkshellManagerDiscordApp.Authorization;
using LinkshellManagerDiscordApp.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace LinkshellManagerDiscordApp.Controllers;

// The server-wide switches a super admin flips, for the Activity's website-only Admin tab. The
// JSON twin of the Razor Settings page (AccountController.Settings + Set*), with the same rules
// and messages; both write through GlobalSettingsService.
//
// Gated structurally on the class ([Authorize] + [SuperAdminOnly], cookie login only), so no
// action can forget it. Every POST also carries [ValidateAntiForgeryToken] on top of the global
// CookieAuthAntiforgeryFilter, matching DataAdminController (WebAdminControllerShapeTests).
[ApiController]
[Authorize]
[SuperAdminOnly]
[Route("api/web/admin")]
public sealed class WebAdminController : ControllerBase
{
    private readonly GlobalSettingsService _globalSettings;
    private readonly ILogger<WebAdminController> _logger;

    public WebAdminController(GlobalSettingsService globalSettings, ILogger<WebAdminController> logger)
    {
        _globalSettings = globalSettings;
        _logger = logger;
    }

    [HttpGet("settings")]
    public async Task<IActionResult> GetSettingsAsync(CancellationToken cancellationToken)
    {
        Response.Headers.CacheControl = "no-store";
        return Ok(await ReadSettingsAsync(null, cancellationToken));
    }

    // Every LinkshellRole permission in every linkshell the super admin is a MEMBER of; the stored
    // rank is untouched and it never reaches a linkshell they have not joined. See AdminOverrideService.
    [HttpPost("settings/admin-override")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SetAdminOverrideAsync([FromBody] WebAdminEnabledRequest request, CancellationToken cancellationToken)
    {
        await _globalSettings.SetAdminOverrideEnabledAsync(request.Enabled, cancellationToken);
        var user = SuperAdminOnlyAttribute.GetUser(HttpContext);
        _logger.LogWarning(
            "Admin override {State} by super admin {AppUserId} ({CharacterName}).",
            request.Enabled ? "ENABLED" : "DISABLED",
            user.Id,
            user.CharacterName);

        return Ok(await ReadSettingsAsync(request.Enabled
            ? "Admin override enabled. You now have every permission in every linkshell you belong to."
            : "Admin override disabled. You are back to your normal rank permissions.", cancellationToken));
    }

    // When disabled, every addon API call is rejected and no pairing codes can be issued or redeemed.
    [HttpPost("settings/addon-kill-switch")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SetAddonKillSwitchAsync([FromBody] WebAdminDisabledRequest request, CancellationToken cancellationToken)
    {
        await _globalSettings.SetAddonGloballyDisabledAsync(request.Disabled, cancellationToken);
        return Ok(await ReadSettingsAsync(request.Disabled
            ? "Addon disabled globally. No one can sync the addon until you re-enable it."
            : "Addon re-enabled globally.", cancellationToken));
    }

    // Narrower than the addon kill-switch: the addon keeps working, it just stops recording
    // lottery windows. Per-monster control lives on each linkshell's Monster setups.
    [HttpPost("settings/claim-shield-kill-switch")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SetClaimShieldKillSwitchAsync([FromBody] WebAdminDisabledRequest request, CancellationToken cancellationToken)
    {
        await _globalSettings.SetClaimShieldGloballyDisabledAsync(request.Disabled, cancellationToken);
        return Ok(await ReadSettingsAsync(request.Disabled
            ? "Claim Shield disabled globally. No addon will record lottery windows until you re-enable it."
            : "Claim Shield re-enabled globally. Each linkshell's per-monster switches apply again.", cancellationToken));
    }

    [HttpPost("settings/launcher-url")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SetLauncherDownloadUrlAsync([FromBody] WebAdminLauncherUrlRequest request, CancellationToken cancellationToken)
    {
        switch (LauncherDownloadPolicy.Check(request.Url, out var url))
        {
            case LauncherUrlOutcome.Clear:
                await _globalSettings.SetLauncherDownloadUrlAsync(null, cancellationToken);
                await _globalSettings.SetLauncherDownloadEnabledAsync(false, cancellationToken);
                return Ok(await ReadSettingsAsync("Launcher download URL cleared, and the link switched off.", cancellationToken));
            case LauncherUrlOutcome.Invalid:
                return BadRequest(new { error = "Launcher URL must be an absolute https:// address. Nothing was changed." });
            default:
                await _globalSettings.SetLauncherDownloadUrlAsync(url, cancellationToken);
                return Ok(await ReadSettingsAsync("Launcher download URL saved.", cancellationToken));
        }
    }

    // LauncherController re-checks this on every download, so off blocks the download itself.
    [HttpPost("settings/launcher-download")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SetLauncherDownloadAsync([FromBody] WebAdminEnabledRequest request, CancellationToken cancellationToken)
    {
        // Refuse to switch the link on with nothing to point at, or every click would 404.
        if (request.Enabled && string.IsNullOrWhiteSpace(await _globalSettings.GetLauncherDownloadUrlAsync(cancellationToken)))
        {
            return BadRequest(new { error = "Save a launcher download URL before turning the link on." });
        }

        await _globalSettings.SetLauncherDownloadEnabledAsync(request.Enabled, cancellationToken);
        return Ok(await ReadSettingsAsync(request.Enabled
            ? "Launcher download link is now visible, including to signed-out visitors."
            : "Launcher download link hidden, and the download URL is blocked until you turn it back on.", cancellationToken));
    }

    private async Task<WebAdminSettingsDto> ReadSettingsAsync(string? message, CancellationToken cancellationToken) => new(
        await _globalSettings.IsAddonGloballyDisabledAsync(cancellationToken),
        await _globalSettings.IsClaimShieldGloballyDisabledAsync(cancellationToken),
        await _globalSettings.IsAdminOverrideEnabledAsync(cancellationToken),
        await _globalSettings.IsLauncherDownloadEnabledAsync(cancellationToken),
        await _globalSettings.GetLauncherDownloadUrlAsync(cancellationToken),
        message);
}

public sealed record WebAdminSettingsDto(
    bool AddonGloballyDisabled,
    bool ClaimShieldGloballyDisabled,
    bool AdminOverrideEnabled,
    bool LauncherDownloadEnabled,
    string? LauncherDownloadUrl,
    // What the last change did, in words; null on a plain read.
    string? Message);

public sealed record WebAdminEnabledRequest(bool Enabled);

public sealed record WebAdminDisabledRequest(bool Disabled);

public sealed record WebAdminLauncherUrlRequest(string? Url);
