using LinkshellManagerDiscordApp.Models;

namespace LinkshellManagerDiscordApp.Services;

#pragma warning disable CS0618 // Round 5 writer: the account columns are still the written source (R5.5 moves it to Characters).

// The main character's jobs (levels, strong flags, merit notes) are WRITTEN on the account
// (AppUser.JobLevels / StrongJobs / MeritJobs), and the save copies them into the main's Characters
// row (CharacterSync), which is where every screen reads them since Round 5. Both writers (the
// profile and the addon) go through Write. Writer only, so it may touch the [Obsolete] columns.
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
    // The owner's Characters rows are checked, lowest slot first. A name that is not there still
    // counts as the main when it is the account's own CharacterName (a main too long for the table).
    // The membership roster names are not read any more (Round 5): they only ever copied the main.
    public static (short Slot, string Name)? OwnCharacterSlot(
        string? characterName,
        AppUser owner,
        IReadOnlyCollection<Character> ownerCharacters)
    {
        var name = characterName?.Trim();
        if (string.IsNullOrEmpty(name)) return null;

        bool Same(string? stored) => string.Equals(stored?.Trim(), name, StringComparison.OrdinalIgnoreCase);

        var character = ownerCharacters
            .Where(c => c.AppUserId == owner.Id && c.Slot is >= Character.MainSlot and <= Character.Alt2Slot)
            .OrderBy(c => c.Slot)
            .FirstOrDefault(c => Same(c.Name));
        if (character is not null) return (character.Slot, character.Name.Trim());

        return Same(owner.CharacterName) ? (Character.MainSlot, owner.CharacterName!.Trim()) : null;
    }
}
