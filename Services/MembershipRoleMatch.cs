using LinkshellManagerDiscordApp.Models;

namespace LinkshellManagerDiscordApp.Services;

// Which LinkshellRoles row a membership's Rank names, as an id (AppUserLinkshell.LinkshellRoleId).
//
// The same rule LinkshellPermissions.GetEffectiveRoleAsync uses to find a role by name today: the
// role in the member's own linkshell whose Name equals the trimmed Rank, with case mattering.
// Anything else -- a blank Rank, a Rank that names no role, one that only matches with different
// capitals -- is null, which means "look the role up by name", exactly as before the column
// existed. So a stamped id never gives a member more rights than their Rank already does.
//
// The migration's SQL fill (AddMembershipRoleId.FillRoleIdsSql) gives the same answer; the
// Postgres round-trip test checks the two against each other.
public static class MembershipRoleMatch
{
    // The role name a Rank asks for: trimmed, or null when it is blank.
    public static string? RoleName(string? rank) =>
        string.IsNullOrWhiteSpace(rank) ? null : rank.Trim();

    // The id of the role this Rank names in this linkshell, or null. The roles may include other
    // linkshells' rows; only this linkshell's count.
    public static int? RoleIdFor(string? rank, int linkshellId, IEnumerable<LinkshellRole> roles)
    {
        var name = RoleName(rank);
        if (name is null)
        {
            return null;
        }
        foreach (var role in roles)
        {
            if (role.LinkshellId == linkshellId && string.Equals(role.Name, name, StringComparison.Ordinal))
            {
                return role.Id;
            }
        }
        return null;
    }
}
