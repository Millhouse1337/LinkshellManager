using System.Globalization;
using System.Net.Http.Headers;
using LinkshellManagerDiscordApp.Data;
using LinkshellManagerDiscordApp.Models;
using LinkshellManagerDiscordApp.Services;
using LinkshellManagerDiscordApp.ViewModels;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NodaTime;

namespace LinkshellManagerDiscordApp.Controllers;

public sealed partial class ActivityDataController
{
    [HttpPost("profile")]
    public async Task<IActionResult> UpdateProfileAsync(
        [FromBody] ActivityUpdateProfileRequest request,
        CancellationToken cancellationToken)
    {
        var appUser = await ResolveAppUserAsync(cancellationToken);
        if (appUser is null)
        {
            return Unauthorized(new
            {
                error = "Sign in with ASP.NET Identity or provide a Discord bearer token to update the activity profile."
            });
        }

        if (string.IsNullOrWhiteSpace(request.CharacterName))
        {
            return BadRequest(new { error = "Character name is required." });
        }

        var normalizedTimeZone = string.IsNullOrWhiteSpace(request.TimeZone) ? null : request.TimeZone.Trim();
        if (normalizedTimeZone is not null && !_timeZones.IsKnownZone(normalizedTimeZone))
        {
            return BadRequest(new { error = "Use a valid IANA time zone such as America/New_York." });
        }

        var result = await _appUserProfileService.UpdateProfileAsync(
            appUser,
            request.CharacterName,
            normalizedTimeZone,
            request.AltCharacterName1,
            request.AltCharacterName2,
            preserveExistingAlts: false,
            cancellationToken);

        if (!result.Succeeded)
        {
            var errorMessage = result.Errors.FirstOrDefault()?.Description ?? "Updating the activity profile failed.";
            return BadRequest(new { error = errorMessage });
        }

        return Ok(new { success = true });
    }

    public sealed record DetectTimeZoneRequest(string? TimeZone);

    // Auto-fills the user's TimeZone from a client-detected IANA zone when the
    // profile field is still on its server default (null/empty/UTC). This lets
    // the header clock and event-time conversions match where the user actually
    // is without forcing them through the Profile page. Users who explicitly
    // picked a non-UTC zone are never overwritten.
    [HttpPost("profile/detect-time-zone")]
    public async Task<IActionResult> DetectTimeZoneAsync(
        [FromBody] DetectTimeZoneRequest request,
        CancellationToken cancellationToken)
    {
        var appUser = await ResolveAppUserAsync(cancellationToken);
        if (appUser is null)
        {
            return Unauthorized(new { error = "Sign in to detect your time zone." });
        }

        var detected = string.IsNullOrWhiteSpace(request.TimeZone) ? null : request.TimeZone.Trim();
        if (detected is null || !_timeZones.IsKnownZone(detected))
        {
            return BadRequest(new { error = "Provide a valid IANA time zone such as America/New_York." });
        }

        var current = appUser.TimeZone?.Trim();
        var isUnset = string.IsNullOrEmpty(current)
            || string.Equals(current, "UTC", StringComparison.OrdinalIgnoreCase);

        if (!isUnset || string.Equals(current, detected, StringComparison.Ordinal))
        {
            return Ok(new { applied = false, timeZone = current });
        }

        appUser.TimeZone = detected;
        var update = await _userManager.UpdateAsync(appUser);
        if (!update.Succeeded)
        {
            var errorMessage = update.Errors.FirstOrDefault()?.Description ?? "Saving the detected time zone failed.";
            return BadRequest(new { error = errorMessage });
        }

        return Ok(new { applied = true, timeZone = detected });
    }
}
