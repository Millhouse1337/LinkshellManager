using LinkshellManagerDiscordApp.Data;
using LinkshellManagerDiscordApp.Models;
using Microsoft.EntityFrameworkCore;

namespace LinkshellManagerDiscordApp.Services;

public sealed class AltCharacterValidator
{
    private readonly ApplicationDbContext _dbContext;

    public AltCharacterValidator(ApplicationDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<(bool Ok, string? Error)> ValidateAsync(
        AppUser user,
        string? mainCharacterName,
        string? altName1,
        string? altName2,
        CancellationToken cancellationToken = default)
    {
        var main = Normalize(mainCharacterName);
        var alt1 = Normalize(altName1);
        var alt2 = Normalize(altName2);

        if (alt1 is not null && alt2 is not null && string.Equals(alt1, alt2, StringComparison.OrdinalIgnoreCase))
        {
            return (false, "Both alternate character names cannot be the same.");
        }

        if (alt1 is not null && main is not null && string.Equals(alt1, main, StringComparison.OrdinalIgnoreCase))
        {
            return (false, $"Alternate character \"{alt1}\" matches your main character name.");
        }

        if (alt2 is not null && main is not null && string.Equals(alt2, main, StringComparison.OrdinalIgnoreCase))
        {
            return (false, $"Alternate character \"{alt2}\" matches your main character name.");
        }

        if (alt1 is null && alt2 is null)
        {
            return (true, null);
        }

        var linkshellIds = await _dbContext.AppUserLinkshells
            .Where(link => link.AppUserId == user.Id)
            .Select(link => link.LinkshellId)
            .Distinct()
            .ToListAsync(cancellationToken);

        if (linkshellIds.Count == 0)
        {
            return (true, null);
        }

        var sharedUserIds = await _dbContext.AppUserLinkshells
            .Where(link => link.AppUserId != null
                        && link.AppUserId != user.Id
                        && linkshellIds.Contains(link.LinkshellId))
            .Select(link => link.AppUserId!)
            .Distinct()
            .ToListAsync(cancellationToken);

        if (sharedUserIds.Count == 0)
        {
            return (true, null);
        }

        var candidates = new List<string>(2);
        if (alt1 is not null) candidates.Add(alt1);
        if (alt2 is not null) candidates.Add(alt2);

        // ILike is the PostgreSQL case-insensitive LIKE; passing the literal name as
        // the pattern (no wildcards) makes it an equality test that respects collation
        // and stays index-eligible (unlike .ToLower() which forces a function scan).
        var conflict = await _dbContext.Users
            .Where(other => sharedUserIds.Contains(other.Id))
            .Where(other => candidates.Any(name =>
                (other.CharacterName != null && EF.Functions.ILike(other.CharacterName, name))
                || (other.AltCharacterName1 != null && EF.Functions.ILike(other.AltCharacterName1, name))
                || (other.AltCharacterName2 != null && EF.Functions.ILike(other.AltCharacterName2, name))))
            .Select(other => new
            {
                other.CharacterName,
                other.AltCharacterName1,
                other.AltCharacterName2
            })
            .FirstOrDefaultAsync(cancellationToken);

        if (conflict is null)
        {
            return (true, null);
        }

        var collidingName = candidates.FirstOrDefault(name =>
            string.Equals(name, conflict.CharacterName, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(name, conflict.AltCharacterName1, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(name, conflict.AltCharacterName2, StringComparison.OrdinalIgnoreCase))
            ?? candidates[0];

        return (false, $"Alternate character \"{collidingName}\" is already linked to another linkshell member.");
    }

    private static string? Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return value.Trim();
    }
}
