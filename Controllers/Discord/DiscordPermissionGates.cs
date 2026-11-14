using LinkshellManagerDiscordApp.Data;
using LinkshellManagerDiscordApp.Models;
using LinkshellManagerDiscordApp.Services;
using Microsoft.EntityFrameworkCore;

namespace LinkshellManagerDiscordApp.Controllers.Discord;

// The Discord bot's permission gates, used by its handlers (see the note below).
public sealed class DiscordPermissionGates
{
    private readonly ApplicationDbContext _db;
    private readonly LinkshellPermissions _permissions;

    public DiscordPermissionGates(ApplicationDbContext db, LinkshellPermissions permissions)
    {
        _db = db;
        _permissions = permissions;
    }

    // The Discord bot's permission gates. Each one asks LinkshellPermissions, the same rule the
    // app and the addon use (no membership -> no; admin override -> yes; Leader -> yes; then the
    // role's flag, with an unknown rank falling back to the Member role). They used to test the
    // Leader/Officer RANK, so a custom role with the right permission was refused here while the
    // app let it through, and an Officer whose role had the permission switched off could still
    // use the Discord buttons. Internal so tests can call the real gates.

    // The board's officer controls: Add Member (and its follow-up steps), Move, Set Leader, Lock
    // and Remove. They all change who sits where on the party board, so they need Manage parties,
    // the permission the app's party board checks for the same edits.
    internal Task<bool> CanEditPartyBoardAsync(Event ev, string? appUserId, CancellationToken cancellationToken)
        => _permissions.CanAsync(appUserId, ev.LinkshellId, role => role.CanManageParties, cancellationToken);

    // "🏁 End Camp / Enter ToD" and its modal need Manage events, like the app's live camp card,
    // so a role cannot end a camp on one surface and not the other.
    internal Task<bool> CanEndCampAsync(string? appUserId, int linkshellId, CancellationToken cancellationToken)
        => _permissions.CanAsync(appUserId, linkshellId, role => role.CanManageEvents, cancellationToken);

    // "/lsm" launch card: allowed when ANY linkshell linked to this Discord server gives the user
    // Manage events. Only linkshells they have joined are checked, so the admin override can
    // never reach a server whose linkshell the admin is not in.
    internal async Task<bool> CanPostLaunchCardAsync(string? appUserId, string guildId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(appUserId))
        {
            return false;
        }
        var memberships = await _db.AppUserLinkshells
            .Where(m => m.AppUserId == appUserId && m.Linkshell != null && m.Linkshell.DiscordGuildId == guildId)
            .ToListAsync(cancellationToken);
        foreach (var membership in memberships)
        {
            if (await _permissions.CanAsync(membership, role => role.CanManageEvents, cancellationToken))
            {
                return true;
            }
        }
        return false;
    }
}
