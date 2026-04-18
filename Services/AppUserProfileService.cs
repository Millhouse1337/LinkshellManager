using LinkshellManagerDiscordApp.Data;
using LinkshellManagerDiscordApp.Models;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace LinkshellManagerDiscordApp.Services;

public sealed class AppUserProfileService
{
    private readonly ApplicationDbContext _dbContext;
    private readonly UserManager<AppUser> _userManager;

    public AppUserProfileService(ApplicationDbContext dbContext, UserManager<AppUser> userManager)
    {
        _dbContext = dbContext;
        _userManager = userManager;
    }

    public async Task<IdentityResult> UpdateProfileAsync(
        AppUser user,
        string? characterName,
        string? timeZone,
        byte[]? profileImage,
        CancellationToken cancellationToken = default)
    {
        var previousCharacterNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(user.CharacterName))
        {
            previousCharacterNames.Add(user.CharacterName.Trim());
        }

        var normalizedCharacterName = string.IsNullOrWhiteSpace(characterName) ? null : characterName.Trim();
        var normalizedTimeZone = string.IsNullOrWhiteSpace(timeZone) ? null : timeZone.Trim();

        user.CharacterName = normalizedCharacterName;
        user.TimeZone = normalizedTimeZone;

        if (profileImage is { Length: > 0 })
        {
            user.ProfileImage = profileImage;
        }

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

        var participations = await _dbContext.AppUserEvents
            .Where(participation => participation.AppUserId == user.Id)
            .ToListAsync(cancellationToken);

        foreach (var participation in participations)
        {
            if (!string.IsNullOrWhiteSpace(participation.CharacterName))
            {
                previousCharacterNames.Add(participation.CharacterName.Trim());
            }

            participation.CharacterName = displayName;
        }

        if (participations.Count > 0)
        {
            var eventIds = participations
                .Select(participation => participation.EventId)
                .Distinct()
                .ToList();

            var jobs = await _dbContext.Jobs
                .Where(job => eventIds.Contains(job.EventId))
                .ToListAsync(cancellationToken);

            foreach (var participation in participations)
            {
                var job = jobs.FirstOrDefault(item =>
                    item.EventId == participation.EventId &&
                    item.JobName == participation.JobName &&
                    item.SubJobName == participation.SubJobName);

                if (job is null)
                {
                    continue;
                }

                foreach (var previousName in previousCharacterNames)
                {
                    job.Enlisted.RemoveAll(name => string.Equals(name, previousName, StringComparison.OrdinalIgnoreCase));
                }

                if (!job.Enlisted.Contains(displayName, StringComparer.OrdinalIgnoreCase))
                {
                    job.Enlisted.Add(displayName);
                }

                job.SignedUp = job.Enlisted.Count;
            }
        }

        var result = await _userManager.UpdateAsync(user);
        if (!result.Succeeded)
        {
            return result;
        }

        await _dbContext.SaveChangesAsync(cancellationToken);
        return result;
    }
}
