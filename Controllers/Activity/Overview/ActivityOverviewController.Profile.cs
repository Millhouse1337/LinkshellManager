using System.Globalization;
using System.Net.Http.Headers;
using LinkshellManagerDiscordApp.Data;
using LinkshellManagerDiscordApp.Models;
using LinkshellManagerDiscordApp.Services;
using LinkshellManagerDiscordApp.Utils;
using LinkshellManagerDiscordApp.ViewModels;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NodaTime;

namespace LinkshellManagerDiscordApp.Controllers;

public sealed partial class ActivityOverviewController
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

        // One transaction for the names and the jobs: CharacterWriter takes the account's lock in
        // it, so two saves of one profile at the same moment run one after the other.
        await using var transaction = await CharacterWriter.BeginTransactionAsync(_dbContext, cancellationToken);

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

        // Jobs and crafts belong to a character (its Characters row). An alt whose name was just
        // cleared has no row any more, so its jobs and crafts went with it. Job arrays are kept in
        // the addon's FFXI-job-id format; the client sends catalog-aligned arrays (index 0 = WAR ...
        // 14 = SMN), merged into what the character already holds. Crafts are stored as sent.
        var characters = await CharacterWriter.LoadAsync(_dbContext, appUser.Id, cancellationToken);
        var changed = false;
        changed |= MergeJobs(characters, Character.MainSlot, request.JobLevels, request.StrongJobs, request.MeritJobs);
        changed |= MergeJobs(characters, Character.Alt1Slot, request.Alt1JobLevels, request.Alt1StrongJobs, request.Alt1MeritJobs);
        changed |= MergeJobs(characters, Character.Alt2Slot, request.Alt2JobLevels, request.Alt2StrongJobs, request.Alt2MeritJobs);
        changed |= SetCrafts(characters, Character.MainSlot, request.CraftLevels);
        changed |= SetCrafts(characters, Character.Alt1Slot, request.Alt1CraftLevels);
        changed |= SetCrafts(characters, Character.Alt2Slot, request.Alt2CraftLevels);
        if (changed)
        {
            await _dbContext.SaveChangesAsync(cancellationToken);
        }

        if (transaction is not null)
        {
            await transaction.CommitAsync(cancellationToken);
        }
        return Ok(new { success = true });
    }

    // Merges the submitted (catalog-aligned) levels, strong flags and merit notes into one
    // character's stored jobs. An empty or missing array leaves that list alone.
    private static bool MergeJobs(CharacterSet characters, short slot, int[]? levels, bool[]? strong, string[]? merits)
    {
        var row = characters[slot];
        if (row is null)
        {
            return false;
        }
        return characters.SetJobs(
            slot,
            jobLevels: levels is { Length: > 0 } ? ProfileJobLevels.MergeIntoStored(row.JobLevels, levels) : null,
            // "Strong" flags ride alongside the levels: one character's set of strengths.
            strongJobs: strong is { Length: > 0 } ? ProfileJobLevels.MergeFlagsIntoStored(row.StrongJobs, strong) : null,
            meritJobs: merits is { Length: > 0 } ? ProfileJobLevels.NormalizeMerits(merits) : null);
    }

    private static bool SetCrafts(CharacterSet characters, short slot, int[]? crafts) =>
        crafts is { Length: > 0 } && characters.SetCrafts(slot, crafts);

    public sealed record DetectTimeZoneRequest(string? TimeZone);

    // Auto-fills the user's TimeZone from a client-detected IANA zone when the
    // profile field was never chosen (null/empty). This lets the header clock and
    // event-time conversions match where the user actually is without forcing them
    // through the Profile page. A zone the user picked, UTC included, is never
    // overwritten.
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
        // Only "never chosen" (null/empty) is filled in. "UTC" is a real choice: treating it as
        // unset replaced it with the browser's zone on the member's next visit, so UTC could not
        // be kept. Nothing writes "UTC" as a default any more (only the first weeks of the app
        // did, and those rows were converted on their owners' next visit by this same endpoint).
        var isUnset = ProfileTimeZone.IsUnset(current);

        if (!isUnset)
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
