using LinkshellManagerDiscordApp.Data;
using LinkshellManagerDiscordApp.Models;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace LinkshellManagerDiscordApp.Services;

public sealed class AppUserProfileService
{
    private readonly ApplicationDbContext _dbContext;
    private readonly UserManager<AppUser> _userManager;
    private readonly CharacterNameValidator _characterNameValidator;

    public AppUserProfileService(
        ApplicationDbContext dbContext,
        UserManager<AppUser> userManager,
        CharacterNameValidator characterNameValidator)
    {
        _dbContext = dbContext;
        _userManager = userManager;
        _characterNameValidator = characterNameValidator;
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
        // The account's names as saved: the main, and the alts from Characters.
        var saved = await AccountCharacters.LoadForAccountAsync(_dbContext, user.Id, cancellationToken);
        var oldDisplayName = AccountCharacters.DisplayName(user);
        var oldNames = new[] { Clean(user.CharacterName), AccountCharacters.Alt1(saved)?.Name, AccountCharacters.Alt2(saved)?.Name };

        var normalizedCharacterName = Clean(characterName);
        var normalizedTimeZone = Clean(timeZone);

        var normalizedAlt1 = preserveExistingAlts ? oldNames[1] : Clean(altCharacterName1);
        var normalizedAlt2 = preserveExistingAlts ? oldNames[2] : Clean(altCharacterName2);

        // Every name that changes is checked, the main too (CharacterNameValidator). Kept alts are
        // passed as they are, so they count as unchanged.
        var (ok, error) = await _characterNameValidator.ValidateAsync(
            user,
            normalizedCharacterName,
            normalizedAlt1,
            normalizedAlt2,
            cancellationToken);

        if (!ok)
        {
            return IdentityResult.Failed(new IdentityError
            {
                Code = "CharacterNameConflict",
                Description = error ?? "Character name validation failed."
            });
        }

        user.CharacterName = normalizedCharacterName;
        user.TimeZone = normalizedTimeZone;

        // The account columns are still where names are written (CharacterSync copies them into
        // Characters on save). Kept alts are not written at all.
        if (!preserveExistingAlts)
        {
#pragma warning disable CS0618 // Round 5 writer: the alt columns are still the written source.
            user.AltCharacterName1 = normalizedAlt1;
            user.AltCharacterName2 = normalizedAlt2;
#pragma warning restore CS0618
        }

        var displayName = AccountCharacters.DisplayName(user);

        // The roster name is still written as a copy of the main (it is no longer read).
        var memberships = await _dbContext.AppUserLinkshells
            .Where(link => link.AppUserId == user.Id)
            .ToListAsync(cancellationToken);
        foreach (var membership in memberships)
        {
#pragma warning disable CS0618 // Round 5 writer: the roster name is a copy of the main.
            membership.CharacterName = displayName;
#pragma warning restore CS0618
        }

        // Open sign-ups follow a RENAMED character, and only that one. A member signed up on an alt
        // stays on that alt when they rename their main; renaming the alt renames the sign-up. (This
        // used to move every open sign-up to the main.) A character whose name was cleared hands its
        // sign-ups to the main. Completed events live in AppUserEventHistory and keep their name.
        var renames = RenamedCharacters(
            oldNames, oldDisplayName, new[] { normalizedCharacterName, normalizedAlt1, normalizedAlt2 }, displayName);

        var participations = await _dbContext.AppUserEvents
            .Where(participation => participation.AppUserId == user.Id && participation.Event!.EndTime == null)
            .ToListAsync(cancellationToken);
        foreach (var participation in participations)
        {
            if (string.IsNullOrWhiteSpace(participation.CharacterName))
            {
                participation.CharacterName = displayName;
            }
            else if (renames.TryGetValue(participation.CharacterName.Trim(), out var renamed))
            {
                participation.CharacterName = renamed;
            }
        }

        var slotSignups = await _dbContext.EventPartySlotSignups
            .Where(signup => signup.AppUserId == user.Id && signup.Event!.EndTime == null)
            .ToListAsync(cancellationToken);
        foreach (var signup in slotSignups)
        {
            if (!string.IsNullOrWhiteSpace(signup.CharacterName)
                && renames.TryGetValue(signup.CharacterName.Trim(), out var renamed))
            {
                signup.CharacterName = renamed;
            }
        }

        // UserManager.UpdateAsync uses our ApplicationDbContext via Identity's
        // EF stores, so the SaveChangesAsync it calls also flushes the membership /
        // participation mutations above in the same transaction.
        return await _userManager.UpdateAsync(user);
    }

    // Old name -> new name for each of the three slots whose name changed (capitals included),
    // compared trimmed and ignoring capitals. Slot 0 is the main: its old name is the account's
    // display name, so a sign-up recorded under the login name of an account with no main follows
    // too. A cleared slot maps to the new display name (the main). The first slot holding a name
    // wins, so a main and an alt swapping names swap their sign-ups.
    internal static Dictionary<string, string> RenamedCharacters(
        string?[] oldNames, string oldDisplayName, string?[] newNames, string newDisplayName)
    {
        var renames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var slot = 0; slot < newNames.Length; slot++)
        {
            var oldName = slot == 0 ? Clean(oldDisplayName) : Clean(oldNames[slot]);
            var newName = slot == 0 ? newDisplayName : newNames[slot] ?? newDisplayName;
            if (oldName is null || string.Equals(oldName, newName, StringComparison.Ordinal))
            {
                continue;
            }
            renames.TryAdd(oldName, newName);
        }
        return renames;
    }

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
