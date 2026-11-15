using LinkshellManagerDiscordApp.Models;

namespace LinkshellManagerDiscordApp.Services;

// The main character's jobs (levels, strong flags, merit notes) live on the account
// (AppUser.JobLevels / StrongJobs / MeritJobs, AccountJobsAndKeyItemAccounts), and every screen
// reads them there. Both writers (the profile and the addon) go through Write.
//
// The old copies on each membership (AppUserLinkshell.JobLevels / StrongJobs / MeritJobs) are no
// longer written (RosterSwitch). Nothing reads them: the build before this one already read the
// account, so a rollback still shows the jobs saved here. A later deploy drops those columns.
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

    // The addon may publish job levels only for the main character of the account its token was
    // issued to: the account's own CharacterName, or the name on one of that account's memberships
    // (the roster spelling of the same character). Returns the stored spelling it matched, the
    // token's own linkshell first, or null when the name is someone else's (or an alt: alts have
    // their own jobs on the account, which the addon does not write). Spaces around a name and
    // capitals do not matter.
    public static string? MatchOwnMain(
        string? characterName,
        AppUser owner,
        IReadOnlyCollection<AppUserLinkshell> ownerMemberships,
        int tokenLinkshellId)
    {
        var name = characterName?.Trim();
        if (string.IsNullOrEmpty(name)) return null;

        bool Same(string? stored) => string.Equals(stored?.Trim(), name, StringComparison.OrdinalIgnoreCase);

        var here = ownerMemberships.FirstOrDefault(m => m.LinkshellId == tokenLinkshellId && Same(m.CharacterName));
        if (here is not null) return here.CharacterName!.Trim();
        if (Same(owner.CharacterName)) return owner.CharacterName!.Trim();
        var elsewhere = ownerMemberships.FirstOrDefault(m => Same(m.CharacterName));
        return elsewhere?.CharacterName!.Trim();
    }
}
