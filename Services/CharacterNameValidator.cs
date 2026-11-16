using LinkshellManagerDiscordApp.Data;
using LinkshellManagerDiscordApp.Models;
using Microsoft.EntityFrameworkCore;

namespace LinkshellManagerDiscordApp.Services;

// CAN THIS ACCOUNT USE THESE CHARACTER NAMES? -- checked when a profile is saved.
//
// The name lookups (CharacterResolver) turn a character name into the member who gets paid. Two
// real players carrying the same name in one linkshell make that answer a coin toss for one of
// them, so a NEW name is refused when another real member of a linkshell this account is in
// already uses it, as their main or an alt (their Characters rows). "New" means the name was not on
// this account before: a member who already shares a name with someone (from before this check
// existed) can still save their profile, and swapping a main and an alt is not a new name.
//
// Placeholders (members an officer added who have not signed in) do not count: they often carry a
// real player's name on purpose, and the player claims them later (PlaceholderClaimService).
//
// Always: the two alts must differ and an alt must differ from the main.
// Only for names that changed: no name may be longer than CharacterWriter.MaxNameLength (64 characters
// while the old account columns are mirrored; they are varchar(64)).
//
// Names are compared trimmed and ignoring capitals, the way CharacterResolver compares them.
public sealed class CharacterNameValidator
{
    private readonly ApplicationDbContext _dbContext;

    public CharacterNameValidator(ApplicationDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    // `user` is the account as it is now (before the save); the three names are what it is about
    // to be saved with.
    public async Task<(bool Ok, string? Error)> ValidateAsync(
        AppUser user,
        string? mainCharacterName,
        string? altName1,
        string? altName2,
        CancellationToken cancellationToken = default)
    {
        // The account's names as saved: its Characters rows (slot 0 the main, 1 and 2 the alts).
        var saved = await AccountCharacters.LoadForAccountAsync(_dbContext, user.Id, cancellationToken);
        var current = new[]
        {
            Normalize(user.CharacterName),
            Normalize(AccountCharacters.Alt1(saved)?.Name),
            Normalize(AccountCharacters.Alt2(saved)?.Name),
        };
        var wanted = new[] { Normalize(mainCharacterName), Normalize(altName1), Normalize(altName2) };
        var changed = new bool[wanted.Length];
        for (var slot = 0; slot < wanted.Length; slot++)
        {
            changed[slot] = !string.Equals(wanted[slot], current[slot], StringComparison.OrdinalIgnoreCase);
        }

        // Owner rule: one account never repeats a name between its own main and alts, on EVERY
        // save (an account that already repeats one must fix it before the profile saves).
        var (main, alt1, alt2) = (wanted[0], wanted[1], wanted[2]);
        if (alt1 is not null && Same(alt1, alt2))
        {
            return (false, "Both alternate character names cannot be the same.");
        }
        if (alt1 is not null && Same(alt1, main))
        {
            return (false, $"Alternate character \"{alt1}\" matches your main character name.");
        }
        if (alt2 is not null && Same(alt2, main))
        {
            return (false, $"Alternate character \"{alt2}\" matches your main character name.");
        }

        for (var slot = 0; slot < wanted.Length; slot++)
        {
            if (changed[slot] && wanted[slot] is { } name && CharacterWriter.IsTooLong(name))
            {
                return (false, $"Character names can be at most {CharacterWriter.MaxNameLength} characters long.");
            }
        }

        // Only a name this account did not have before is checked against the others.
        var newNames = wanted
            .Where((name, slot) => name is not null && changed[slot]
                && !current.Any(had => string.Equals(had, name, StringComparison.OrdinalIgnoreCase)))
            .Select(name => name!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (newNames.Count == 0)
        {
            return (true, null);
        }

        var taken = await FindTakenNameAsync(user.Id, newNames, cancellationToken);
        return taken is null
            ? (true, null)
            : (false, $"\"{taken}\" is already used by another member of your linkshell. Please choose a different character name.");
    }

    // The first of `names` that another real (non-placeholder) member of a linkshell this account
    // is in already uses, or null.
    private async Task<string?> FindTakenNameAsync(string userId, List<string> names, CancellationToken cancellationToken)
    {
        var linkshellIds = await _dbContext.AppUserLinkshells
            .Where(link => link.AppUserId == userId)
            .Select(link => link.LinkshellId)
            .Distinct()
            .ToListAsync(cancellationToken);
        if (linkshellIds.Count == 0)
        {
            return null;
        }

        // The other real members of those linkshells.
        var others = await _dbContext.AppUserLinkshells
            .Where(link => linkshellIds.Contains(link.LinkshellId)
                        && link.AppUserId != null
                        && link.AppUserId != userId)
            .Join(_dbContext.Users.Where(other => !other.IsPlaceholder),
                  link => link.AppUserId,
                  other => other.Id,
                  (link, other) => other.Id)
            .ToListAsync(cancellationToken);
        if (others.Count == 0)
        {
            return null;
        }

        var otherIds = others.Distinct(StringComparer.Ordinal).ToList();
        var lowered = names.Select(name => name.ToLowerInvariant()).Distinct(StringComparer.Ordinal).ToList();
        var usedAsCharacter = await _dbContext.Characters
            .AsNoTracking()
            .Where(character => otherIds.Contains(character.AppUserId) && lowered.Contains(character.NormalizedName))
            .Select(character => character.Name)
            .ToListAsync(cancellationToken);

        return names.FirstOrDefault(name =>
            usedAsCharacter.Any(used => Same(used, name)));
    }

    private static bool Same(string? a, string? b) =>
        a is not null && b is not null && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
