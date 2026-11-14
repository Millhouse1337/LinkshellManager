using LinkshellManagerDiscordApp.Models;

namespace LinkshellManagerDiscordApp.Services;

// The roster row a player gets when they join a linkshell through an invite or an approved join
// request: Member rank, Active, 0 DKP, their character name. One copy, so the join paths cannot
// drift apart. What the Member role may do (bidding, the addon) comes from the linkshell's roles,
// not from this row.
public static class LinkshellMembership
{
    public static AppUserLinkshell NewMember(AppUser user, int linkshellId, DateTime joinedAtUtc) => new()
    {
        AppUserId = user.Id,
        LinkshellId = linkshellId,
        LinkshellDkp = 0,
        DateJoined = joinedAtUtc,
        CharacterName = user.CharacterName ?? user.UserName,
        Rank = LinkshellRanks.Member,
        Status = "Active"
    };
}
