namespace LinkshellManagerDiscordApp.Services;

// Which roster members an officer means by a name they typed, for the board's "Add Member" flow.
//
// That flow's member list is capped at Discord's 25 options, so on a big roster typing a name is
// the only way to reach most people. It only ever FINDS: a typed name used to create a new member
// when it didn't match exactly, which is how a player who had registered as "Comte🧀" got a second
// roster entry called "Comte". A player joins the roster by signing up from the board themselves.
//
// An exact name (roster name, main or either alt, any capitals) wins outright. Otherwise every
// member with a name CONTAINING what was typed is a candidate, so "comte" still finds "Comte🧀".
public static class RosterMemberSearch
{
    public static IReadOnlyList<RosterNameCandidate> Find(IEnumerable<RosterNameCandidate> members, string? typed)
    {
        var query = (typed ?? string.Empty).Trim();
        if (query.Length == 0)
        {
            return Array.Empty<RosterNameCandidate>();
        }

        var roster = members.ToList();
        var exact = roster
            .Where(member => NamesOf(member).Any(name => string.Equals(name, query, StringComparison.OrdinalIgnoreCase)))
            .ToList();
        if (exact.Count > 0)
        {
            return exact;
        }
        return roster
            .Where(member => NamesOf(member).Any(name => name.Contains(query, StringComparison.OrdinalIgnoreCase)))
            .ToList();
    }

    private static IEnumerable<string> NamesOf(RosterNameCandidate member) =>
        new[] { member.MembershipCharacterName, member.AccountCharacterName, member.AltCharacterName1, member.AltCharacterName2 }
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => name!.Trim());
}
