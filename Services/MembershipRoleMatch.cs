using LinkshellManagerDiscordApp.Models;

namespace LinkshellManagerDiscordApp.Services;

// Which role a membership's Rank names. The save hook (ApplicationDbContext.StampMembershipRoles)
// uses it when a writer set Rank but no role id.
//
// It gives the role LinkshellPermissions would give the same Rank, so a member's rights never
// change; only the Rank text may (it becomes the role's exact name):
//   1. the role of the member's own linkshell whose Name equals the trimmed Rank, case mattering
//      (the rule GetEffectiveRoleAsync uses to find a role by name);
//   2. else, a Rank that is "leader" in any capitals: the Leader role. Permissions treat such a
//      Rank as the Leader (LinkshellRanks.IsLeader), so turning it into Member would lock the owner
//      out;
//   3. else -- a blank Rank, a Rank that names no role, one that only matches with different
//      capitals -- the Member role, which is what permissions fall back to for such a Rank.
public static class MembershipRoleMatch
{
    // The role name a Rank asks for: trimmed, or null when it is blank.
    public static string? RoleName(string? rank) =>
        string.IsNullOrWhiteSpace(rank) ? null : rank.Trim();

    // The role this Rank names among ONE linkshell's roles. FellBack is true when the Rank did not
    // name a role exactly (rule 2 or 3 above). The role is null only when the linkshell has no
    // role to fall back to either.
    public static (LinkshellRole? Role, bool FellBack) Pick(string? rank, IReadOnlyCollection<LinkshellRole> linkshellRoles)
    {
        var name = RoleName(rank);
        if (name is not null)
        {
            var named = Named(linkshellRoles, name);
            if (named is not null)
            {
                return (named, false);
            }
        }
        var fallback = LinkshellRanks.IsLeader(rank) ? LinkshellRanks.Leader : LinkshellRanks.Member;
        return (Named(linkshellRoles, fallback), true);
    }

    private static LinkshellRole? Named(IReadOnlyCollection<LinkshellRole> roles, string name) =>
        roles.FirstOrDefault(role => string.Equals(role.Name, name, StringComparison.Ordinal));
}
