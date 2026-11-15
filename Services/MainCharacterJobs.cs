using LinkshellManagerDiscordApp.Models;

namespace LinkshellManagerDiscordApp.Services;

// The main character's jobs (levels, strong flags, merit notes) live on the account
// (AppUser.JobLevels / StrongJobs / MeritJobs, AccountJobsAndKeyItemAccounts), and every screen
// reads them there. Both writers (the profile and the addon) go through Write.
//
// (They used to have a copy on each membership too. Round4Contract dropped those old columns.)
public static class MainCharacterJobs
{
    // Sets each value that is not null on the account. A null argument leaves that column alone.
    public static void Write(
        AppUser account,
        int[]? jobLevels = null,
        int[]? strongJobs = null,
        string[]? meritJobs = null)
    {
        if (jobLevels is not null) account.JobLevels = jobLevels;
        if (strongJobs is not null) account.StrongJobs = strongJobs;
        if (meritJobs is not null) account.MeritJobs = meritJobs;
    }

    // Replaces the job levels of one character of the account: slot 0 is the main (Write above),
    // 1 and 2 the alts. The account columns are still the written source; the save copies them to
    // that character's Characters row (CharacterSync).
    public static void WriteJobLevels(AppUser account, short slot, int[] jobLevels)
    {
        switch (slot)
        {
            case Character.MainSlot:
                Write(account, jobLevels: jobLevels);
                break;
            case Character.Alt1Slot:
                account.Alt1JobLevels = jobLevels;
                break;
            case Character.Alt2Slot:
                account.Alt2JobLevels = jobLevels;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(slot), slot, "A character slot is 0, 1 or 2.");
        }
    }

    // The addon may publish job levels only for a character of the account its token was issued to.
    // Returns which of the owner's characters the name is (slot 0 main, 1 or 2 an alt) and the stored
    // spelling, or null when it is none of them (another member's main or alt included). Spaces
    // around a name and capitals do not matter.
    //
    // The owner's Characters rows are checked first, lowest slot first. A name that is not there
    // still counts as the main when it is the account's own CharacterName or the name on one of its
    // memberships (the token's linkshell first): the old match, kept until Round 5 stops reading
    // roster names.
    public static (short Slot, string Name)? OwnCharacterSlot(
        string? characterName,
        AppUser owner,
        IReadOnlyCollection<Character> ownerCharacters,
        IReadOnlyCollection<AppUserLinkshell> ownerMemberships,
        int tokenLinkshellId)
    {
        var name = characterName?.Trim();
        if (string.IsNullOrEmpty(name)) return null;

        bool Same(string? stored) => string.Equals(stored?.Trim(), name, StringComparison.OrdinalIgnoreCase);

        var character = ownerCharacters
            .Where(c => c.AppUserId == owner.Id && c.Slot is >= Character.MainSlot and <= Character.Alt2Slot)
            .OrderBy(c => c.Slot)
            .FirstOrDefault(c => Same(c.Name));
        if (character is not null) return (character.Slot, character.Name.Trim());

        var here = ownerMemberships.FirstOrDefault(m => m.LinkshellId == tokenLinkshellId && Same(m.CharacterName));
        if (here is not null) return (Character.MainSlot, here.CharacterName!.Trim());
        if (Same(owner.CharacterName)) return (Character.MainSlot, owner.CharacterName!.Trim());
        var elsewhere = ownerMemberships.FirstOrDefault(m => Same(m.CharacterName));
        return elsewhere is null ? null : (Character.MainSlot, elsewhere.CharacterName!.Trim());
    }
}
