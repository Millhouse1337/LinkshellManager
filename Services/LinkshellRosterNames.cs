using LinkshellManagerDiscordApp.Data;
using LinkshellManagerDiscordApp.Models;
using Microsoft.EntityFrameworkCore;

namespace LinkshellManagerDiscordApp.Services;

// WHO COUNTS AS REGISTERED, as one set of character names.
//
// A player is registered once they hold an AppUserLinkshell row for the linkshell — which happens
// exactly two ways: they accepted an invite (the LSM app / Discord sign-in path), or they
// self-registered from a sign-up board at least once (ManualMemberService.FindOrCreateForOutside,
// which mints a placeholder account so the AppUserId-keyed DKP system can track them). A PENDING
// invite is not membership: those live in the Invites table and no roster row exists until it is
// accepted.
//
// The set indexes every name that player might be standing in the world as — the membership's own
// CharacterName, the account's CharacterName, and both alts — because the addon captures whatever
// character is in the alliance, which is often not the one on the roster. This is the same
// four-name index PostAttendanceAsync and WindowEventDkpLedgerService already build to decide who
// gets paid; sharing it is what keeps "shown as credited" and "actually credited" from disagreeing.
public sealed class RosterNameSet
{
    private readonly HashSet<string>? _names;
    private readonly HashSet<string>? _appUserIds;

    private RosterNameSet(HashSet<string>? names, HashSet<string>? appUserIds)
    {
        _names = names;
        _appUserIds = appUserIds;
    }

    // "No roster was loaded", which is NOT the same as "the roster is empty". Contains returns true
    // for everything, so a caller that never had a roster to check against tags nobody rather than
    // tagging everybody — a mapper called without one (an old test, a surface that has no linkshell
    // in hand) must not paint a whole camp as unregistered.
    public static RosterNameSet Unknown { get; } = new(null, null);

    public bool IsKnown => _names is not null;

    public static RosterNameSet From(IEnumerable<RosterNameCandidate> members)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var appUserIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var member in members)
        {
            foreach (var candidate in new[]
                     {
                         member.MembershipCharacterName,
                         member.AccountCharacterName,
                         member.AltCharacterName1,
                         member.AltCharacterName2,
                     })
            {
                if (string.IsNullOrWhiteSpace(candidate)) continue;
                names.Add(candidate.Trim());
            }

            if (!string.IsNullOrWhiteSpace(member.AppUserId))
            {
                appUserIds.Add(member.AppUserId.Trim());
            }
        }
        return new RosterNameSet(names, appUserIds);
    }

    // A blank name is never a member: the scan read nothing, and answering "yes" would let an empty
    // row through the one check that exists to catch strangers.
    public bool Contains(string? characterName)
    {
        if (_names is null) return true;
        if (string.IsNullOrWhiteSpace(characterName)) return false;
        return _names.Contains(characterName.Trim());
    }

    // THE ACCOUNT, where a capture carries one.
    //
    // A camp handoff (HnmCampReviewHandoffService) stamps AppUserId straight onto the entry, and
    // WindowEventDkpLedgerService prefers it over the name lookup for a reason it states outright:
    // the roster is keyed on accounts, so a member standing on a character that is none of their
    // four indexed names resolves to nothing by name and would never be credited. Asking by name
    // alone here would mark exactly those people "not registered" on a camp that is about to pay
    // them — the flag has to agree with the payout, not merely approximate it.
    public bool ContainsAppUserId(string? appUserId)
    {
        if (_appUserIds is null) return true;
        if (string.IsNullOrWhiteSpace(appUserId)) return false;
        return _appUserIds.Contains(appUserId.Trim());
    }

    // The whole question in one call, in the same order the ledger resolves it: the account the
    // capture recorded, else the character it read.
    public bool Knows(string? characterName, string? appUserId)
        => ContainsAppUserId(appUserId) || Contains(characterName);

    public int Count => _names?.Count ?? 0;
}

// One roster member's four candidate names. A record rather than the entity so the set can be
// built from a projection, from a test, or from rows already in memory.
public sealed record RosterNameCandidate(
    string? MembershipCharacterName,
    string? AccountCharacterName,
    string? AltCharacterName1,
    string? AltCharacterName2,
    // The membership's account, for captures that recorded one. Optional and last so the
    // name-only callers (tests, anything building this from names alone) are unaffected.
    string? AppUserId = null);

public static class LinkshellRosterNames
{
    // One query, four columns. Memberships with no AppUserId are skipped for the same reason the
    // DKP paths skip them: without an account there is nothing to credit, so such a row cannot make
    // anyone "registered" no matter what name it carries.
    public static async Task<RosterNameSet> LoadAsync(
        ApplicationDbContext db, int linkshellId, CancellationToken cancellationToken)
    {
        var rows = await db.AppUserLinkshells
            .AsNoTracking()
            .Where(link => link.LinkshellId == linkshellId && link.AppUserId != null)
            .Join(db.Users.AsNoTracking(),
                  link => link.AppUserId,
                  user => user.Id,
                  (link, user) => new RosterNameCandidate(
                      link.CharacterName,
                      user.CharacterName,
                      user.AltCharacterName1,
                      user.AltCharacterName2,
                      link.AppUserId))
            .ToListAsync(cancellationToken);

        return RosterNameSet.From(rows);
    }
}
