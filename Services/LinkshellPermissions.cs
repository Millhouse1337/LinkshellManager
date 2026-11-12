using LinkshellManagerDiscordApp.Data;
using LinkshellManagerDiscordApp.Models;
using Microsoft.EntityFrameworkCore;

namespace LinkshellManagerDiscordApp.Services;

// The one place that answers "may this member do X in this linkshell?".
//
// Before this existed the rule was copied into the Activity/web controller, the addon
// controllers and the Discord bot, and the copies drifted apart (some forgot the Leader
// rule, some forgot the super-admin override, some had no Member-role fallback). This
// service holds the rule the Activity/web controller already used, so later steps can
// point the other copies here and they all agree.
//
// Rule order — keep it exactly like this:
//   1. no membership                    -> false  (never grant inside a linkshell the user has not joined)
//   2. super-admin override is active   -> true   (AdminOverrideService; only AFTER the membership check)
//   3. rank is Leader                   -> true   (a stale or mis-edited role row must never lock the owner out)
//   4. the role named by the rank decides; an unknown rank falls back to the Member role
//
// What this does NOT do: the per-linkshell Discord guild lock. That needs the request's
// Discord bearer token and guild header, so it stays in ActivityDataController, which
// filters the membership before asking this service.
//
// Scoped, so it shares the request's DbContext (and AdminOverrideService's per-request memo).
public sealed class LinkshellPermissions
{
    private readonly ApplicationDbContext _dbContext;
    private readonly AdminOverrideService _adminOverride;

    public LinkshellPermissions(ApplicationDbContext dbContext, AdminOverrideService adminOverride)
    {
        _dbContext = dbContext;
        _adminOverride = adminOverride;
    }

    // Editing a live camp's attendance and pay: posting or deleting a window, setting a window's
    // DKP, the open / close / kill bonus, the closing tick, a Misc post's DKP, filing captures.
    // Moderate live events OR Manage events, so whoever may run a camp may also fix its numbers.
    // The app's Window Events routes and the addon controller's management routes both use this,
    // so one camp card never mixes two rules.
    public static bool EditsLiveCamp(LinkshellRole role)
        => role.CanModerateLiveEvent || role.CanManageEvents;

    // The member's row in this linkshell, with the Linkshell loaded (callers read its
    // lock/guild settings). Tracked on purpose: several callers edit the membership after.
    public Task<AppUserLinkshell?> GetMembershipAsync(string appUserId, int linkshellId, CancellationToken ct)
    {
        return _dbContext.AppUserLinkshells
            .Include(link => link.Linkshell)
            .FirstOrDefaultAsync(link => link.AppUserId == appUserId && link.LinkshellId == linkshellId, ct);
    }

    public async Task<bool> CanAsync(
        AppUserLinkshell? membership,
        Func<LinkshellRole, bool> selector,
        CancellationToken ct)
    {
        if (membership is null)
        {
            return false;
        }

        // Super-admin override, when switched on globally. Deliberately placed AFTER the
        // membership null-check so it can only ever elevate inside a linkshell the user
        // has actually joined. See AdminOverrideService.
        if (await _adminOverride.IsActiveForAsync(membership.AppUserId, ct))
        {
            return true;
        }

        // The Leader (linkshell owner) always has every permission — a stale role row
        // (e.g. one seeded before a permission existed) or a mis-edited role must never
        // lock the leader out of their own linkshell. This is why a Leader could hit a
        // 403 on Browse players / Discord roster despite owning the linkshell.
        if (LinkshellRanks.IsLeader(membership.Rank))
        {
            return true;
        }

        var role = await GetEffectiveRoleAsync(membership.Rank, membership.LinkshellId, ct);
        return role is not null && selector(role);
    }

    // Convenience for callers that only have ids (addon, Discord). Same rule as above;
    // a blank user id is "not a member".
    public async Task<bool> CanAsync(
        string? appUserId,
        int linkshellId,
        Func<LinkshellRole, bool> selector,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(appUserId))
        {
            return false;
        }

        var membership = await GetMembershipAsync(appUserId, linkshellId, ct);
        return await CanAsync(membership, selector, ct);
    }

    // The role row a rank maps to. A blank rank means Member; a rank with no matching
    // role (deleted or renamed custom role) falls back to the Member role, so the member
    // keeps member rights instead of getting none — or more.
    public async Task<LinkshellRole?> GetEffectiveRoleAsync(string? rank, int linkshellId, CancellationToken ct)
    {
        await EnsureDefaultRolesAsync(linkshellId, ct);
        var rankName = string.IsNullOrWhiteSpace(rank) ? LinkshellRanks.Member : rank.Trim();
        var role = await _dbContext.LinkshellRoles
            .AsNoTracking()
            .FirstOrDefaultAsync(r => r.LinkshellId == linkshellId && r.Name == rankName, ct);
        if (role is null)
        {
            role = await _dbContext.LinkshellRoles
                .AsNoTracking()
                .FirstOrDefaultAsync(r => r.LinkshellId == linkshellId && r.Name == LinkshellRanks.Member, ct);
        }
        return role;
    }

    public async Task<List<LinkshellRole>> EnsureDefaultRolesAsync(int linkshellId, CancellationToken ct)
    {
        var seeded = await EnsureDefaultRolesForLinkshellsAsync(new[] { linkshellId }, ct);
        return seeded.TryGetValue(linkshellId, out var roles) ? roles : new List<LinkshellRole>();
    }

    // Batch variant: seeds missing default roles for every supplied linkshell in two
    // round-trips total (one SELECT, one INSERT) instead of N pairs from a foreach.
    public async Task<Dictionary<int, List<LinkshellRole>>> EnsureDefaultRolesForLinkshellsAsync(
        IReadOnlyCollection<int> linkshellIds,
        CancellationToken ct)
    {
        var result = new Dictionary<int, List<LinkshellRole>>();
        if (linkshellIds.Count == 0)
        {
            return result;
        }

        var existing = await _dbContext.LinkshellRoles
            .Where(r => linkshellIds.Contains(r.LinkshellId))
            .AsNoTracking()
            .ToListAsync(ct);

        var existingByLinkshell = existing
            .GroupBy(r => r.LinkshellId)
            .ToDictionary(g => g.Key, g => g.ToList());

        var toAdd = new List<LinkshellRole>();
        foreach (var linkshellId in linkshellIds)
        {
            existingByLinkshell.TryGetValue(linkshellId, out var rolesForLinkshell);
            rolesForLinkshell ??= new List<LinkshellRole>();

            var existingNames = new HashSet<string>(rolesForLinkshell.Select(r => r.Name), StringComparer.OrdinalIgnoreCase);
            foreach (var defaultRole in LinkshellRoleDefaults.BuildDefaultRoles(linkshellId))
            {
                if (!existingNames.Contains(defaultRole.Name))
                {
                    toAdd.Add(defaultRole);
                }
            }

            result[linkshellId] = rolesForLinkshell;
        }

        if (toAdd.Count > 0)
        {
            await _dbContext.LinkshellRoles.AddRangeAsync(toAdd, ct);
            await _dbContext.SaveChangesAsync(ct);

            foreach (var added in toAdd)
            {
                if (!result.TryGetValue(added.LinkshellId, out var bucket))
                {
                    bucket = new List<LinkshellRole>();
                    result[added.LinkshellId] = bucket;
                }
                bucket.Add(added);
            }
        }

        foreach (var key in result.Keys.ToList())
        {
            result[key] = result[key].OrderBy(r => r.SortOrder).ThenBy(r => r.Name).ToList();
        }

        return result;
    }
}
