using LinkshellManagerDiscordApp.Data;
using LinkshellManagerDiscordApp.Models;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace LinkshellManagerDiscordApp.Services;

public sealed class AppUserProfileService
{
    private readonly ApplicationDbContext _dbContext;
    private readonly UserManager<AppUser> _userManager;
    private readonly AltCharacterValidator _altCharacterValidator;

    public AppUserProfileService(
        ApplicationDbContext dbContext,
        UserManager<AppUser> userManager,
        AltCharacterValidator altCharacterValidator)
    {
        _dbContext = dbContext;
        _userManager = userManager;
        _altCharacterValidator = altCharacterValidator;
    }

    public Task<IdentityResult> UpdateProfileAsync(
        AppUser user,
        string? characterName,
        string? timeZone,
        CancellationToken cancellationToken = default)
        => UpdateProfileAsync(user, characterName, timeZone, altCharacterName1: null, altCharacterName2: null, preserveExistingAlts: true, cancellationToken);

    public async Task<IdentityResult> UpdateProfileAsync(
        AppUser user,
        string? characterName,
        string? timeZone,
        string? altCharacterName1,
        string? altCharacterName2,
        bool preserveExistingAlts = false,
        CancellationToken cancellationToken = default)
    {
        var previousCharacterNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(user.CharacterName))
        {
            previousCharacterNames.Add(user.CharacterName.Trim());
        }

        var normalizedCharacterName = string.IsNullOrWhiteSpace(characterName) ? null : characterName.Trim();
        var normalizedTimeZone = string.IsNullOrWhiteSpace(timeZone) ? null : timeZone.Trim();

        var normalizedAlt1 = preserveExistingAlts
            ? user.AltCharacterName1
            : (string.IsNullOrWhiteSpace(altCharacterName1) ? null : altCharacterName1.Trim());
        var normalizedAlt2 = preserveExistingAlts
            ? user.AltCharacterName2
            : (string.IsNullOrWhiteSpace(altCharacterName2) ? null : altCharacterName2.Trim());

        if (!preserveExistingAlts)
        {
            var (ok, error) = await _altCharacterValidator.ValidateAsync(
                user,
                normalizedCharacterName,
                normalizedAlt1,
                normalizedAlt2,
                cancellationToken);

            if (!ok)
            {
                return IdentityResult.Failed(new IdentityError
                {
                    Code = "AltConflict",
                    Description = error ?? "Alternate character validation failed."
                });
            }
        }

        user.CharacterName = normalizedCharacterName;
        user.TimeZone = normalizedTimeZone;

        // Alts never propagate to AppUserLinkshell.CharacterName or AppUserEvent.CharacterName — actions stay attributed to main.
        user.AltCharacterName1 = normalizedAlt1;
        user.AltCharacterName2 = normalizedAlt2;

        var displayName = user.CharacterName ?? user.UserName ?? "Unknown";

        var memberships = await _dbContext.AppUserLinkshells
            .Where(link => link.AppUserId == user.Id)
            .ToListAsync(cancellationToken);

        foreach (var membership in memberships)
        {
            if (!string.IsNullOrWhiteSpace(membership.CharacterName))
            {
                previousCharacterNames.Add(membership.CharacterName.Trim());
            }

            membership.CharacterName = displayName;
        }

        // Only rename participations on events that are still in progress. Completed
        // events live in AppUserEventHistory and should keep their original character
        // attribution.
        var participations = await _dbContext.AppUserEvents
            .Where(participation => participation.AppUserId == user.Id && participation.Event!.EndTime == null)
            .ToListAsync(cancellationToken);

        foreach (var participation in participations)
        {
            if (!string.IsNullOrWhiteSpace(participation.CharacterName))
            {
                previousCharacterNames.Add(participation.CharacterName.Trim());
            }

            participation.CharacterName = displayName;
        }

        // UserManager.UpdateAsync uses our ApplicationDbContext via Identity's
        // EF stores, so the SaveChangesAsync it calls also flushes the membership /
        // participation mutations above in the same transaction.
        _ = previousCharacterNames; // retained for potential future audit logging
        return await _userManager.UpdateAsync(user);
    }
}
