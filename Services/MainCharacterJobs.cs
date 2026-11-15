using LinkshellManagerDiscordApp.Models;

namespace LinkshellManagerDiscordApp.Services;

// The main character's jobs (levels, strong flags, merit notes) live on the account
// (AppUser.JobLevels / StrongJobs / MeritJobs, AccountJobsAndKeyItemAccounts), and every screen
// reads them there. Until a later deploy stops it, every writer ALSO copies the same value onto each
// of the account's memberships: the build before this one reads the memberships, so a rollback
// still shows the jobs people saved here. Both writers (the profile and the addon) go through Write,
// so the two copies cannot drift.
public static class MainCharacterJobs
{
    // Sets each value that is not null on the account and on every one of its memberships. A null
    // argument leaves that column alone everywhere. Each membership gets its own copy of the array,
    // so a later change to one row's array cannot reach another row by accident.
    public static void Write(
        AppUser account,
        IEnumerable<AppUserLinkshell> memberships,
        int[]? jobLevels = null,
        int[]? strongJobs = null,
        string[]? meritJobs = null)
    {
        if (jobLevels is not null) account.JobLevels = jobLevels;
        if (strongJobs is not null) account.StrongJobs = strongJobs;
        if (meritJobs is not null) account.MeritJobs = meritJobs;

        foreach (var membership in memberships)
        {
            if (jobLevels is not null) membership.JobLevels = (int[])jobLevels.Clone();
            if (strongJobs is not null) membership.StrongJobs = (int[])strongJobs.Clone();
            if (meritJobs is not null) membership.MeritJobs = (string[])meritJobs.Clone();
        }
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
