using LinkshellManagerDiscordApp.Data;
using Microsoft.EntityFrameworkCore;

namespace LinkshellManagerDiscordApp.Services;

// A single LSM user who is eligible to be invited to a linkshell. Front-end
// agnostic: the Activity maps it to ActivityUserSearchResultDto, the web view
// renders it directly. Field order/names mirror ActivityUserSearchResultDto so
// the Activity's JSON contract is unchanged.
public sealed record InviteCandidate(
    string Id,
    string DisplayName,
    string? UserName,
    string? PrimaryLinkshellName);

public sealed record InviteCandidatePage(
    IReadOnlyList<InviteCandidate> Items,
    int Total,
    int Page,
    int PageSize);

// Shared invite-candidate browse used by BOTH the Discord Activity
// (ActivityDataController.Invites) and the website (ManageTeamController), so
// the two front-ends list the same eligible players with the same filtering and
// guild-lock behavior. The caller is responsible for auth (resolving the user
// and checking the manage-members permission); this service only runs the
// query.
public sealed class InviteCandidateService
{
    // Every status that means "this person already has a pending invite or join
    // request for this linkshell" and so must NOT be offered again. The web
    // writes "Pending"; the Activity writes "PendingInvite"/"PendingJoinRequest".
    // We exclude all three so a browse never re-shows someone already invited
    // from either front-end.
    public static readonly string[] PendingInviteStatuses =
        { "Pending", "PendingInvite", "PendingJoinRequest" };

    private readonly ApplicationDbContext _dbContext;
    private readonly DiscordIdentityService _discordIdentityService;

    public InviteCandidateService(
        ApplicationDbContext dbContext,
        DiscordIdentityService discordIdentityService)
    {
        _dbContext = dbContext;
        _discordIdentityService = discordIdentityService;
    }

    // Paginated, searchable, filterable browse of every LSM user eligible to be
    // invited to the given linkshell (anyone who has used the app), minus the
    // caller, current members, and anyone with a pending invite/join request.
    //   query  - case-insensitive substring of character name OR username.
    //   filter - "unaffiliated" (no linkshell), "affiliated" (has one), else all.
    //   discordGuildId - when the linkshell is locked to a server, restrict to
    //                    that server's members (null/unavailable = no restriction).
    public async Task<InviteCandidatePage> BrowseAsync(
        int linkshellId,
        string callerAppUserId,
        string? discordGuildId,
        string? query,
        string? filter,
        int page,
        int pageSize,
        CancellationToken cancellationToken)
    {
        page = page < 1 ? 1 : page;
        pageSize = pageSize is < 1 or > 50 ? 10 : pageSize;

        var existingMemberIds = await _dbContext.AppUserLinkshells
            .Where(link => link.LinkshellId == linkshellId && link.AppUserId != null)
            .Select(link => link.AppUserId!)
            .ToListAsync(cancellationToken);

        var pendingInviteIds = await _dbContext.Invites
            .Where(invite =>
                invite.LinkshellId == linkshellId &&
                PendingInviteStatuses.Contains(invite.Status))
            .Select(invite => invite.AppUserId)
            .ToListAsync(cancellationToken);

        var eligible = _dbContext.Users
            .Where(user =>
                user.Id != callerAppUserId &&
                !existingMemberIds.Contains(user.Id) &&
                !pendingInviteIds.Contains(user.Id));

        var normalizedQuery = query?.Trim();
        if (!string.IsNullOrWhiteSpace(normalizedQuery))
        {
            eligible = eligible.Where(user =>
                (user.CharacterName != null && EF.Functions.ILike(user.CharacterName, $"%{normalizedQuery}%")) ||
                EF.Functions.ILike(user.UserName!, $"%{normalizedQuery}%"));
        }

        var normalizedFilter = (filter ?? "all").Trim().ToLowerInvariant();
        if (normalizedFilter == "unaffiliated")
        {
            eligible = eligible.Where(user => !_dbContext.AppUserLinkshells.Any(link => link.AppUserId == user.Id));
        }
        else if (normalizedFilter == "affiliated")
        {
            eligible = eligible.Where(user => _dbContext.AppUserLinkshells.Any(link => link.AppUserId == user.Id));
        }

        // When the linkshell is locked to a Discord server, only surface people
        // who are actually in that server (null = unlocked or bot unavailable).
        var guildEligibleIds = await TryGetGuildEligibleAppUserIdsAsync(discordGuildId, cancellationToken);
        if (guildEligibleIds is not null)
        {
            eligible = eligible.Where(user => guildEligibleIds.Contains(user.Id));
        }

        var total = await eligible.CountAsync(cancellationToken);

        var items = await eligible
            .OrderBy(user => user.CharacterName ?? user.UserName)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(user => new InviteCandidate(
                user.Id,
                user.CharacterName ?? user.UserName ?? "Unknown member",
                user.UserName,
                user.PrimaryLinkshellName))
            .ToListAsync(cancellationToken);

        return new InviteCandidatePage(items, total, page, pageSize);
    }

    // Resolves which AppUser ids may appear in a guild-locked linkshell's invite
    // search: those whose linked Discord account is a member of the locked
    // server (via the bot roster lookup). Returns null when no filtering should
    // apply — the linkshell isn't locked, or the bot roster is unavailable
    // (fail open; the access-time lock still blocks non-members from any data).
    public async Task<HashSet<string>?> TryGetGuildEligibleAppUserIdsAsync(
        string? discordGuildId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(discordGuildId))
        {
            return null;
        }

        var memberDiscordIds = await _discordIdentityService
            .TryGetGuildMemberDiscordIdsAsync(discordGuildId, cancellationToken);
        if (memberDiscordIds is null)
        {
            return null;
        }

        if (memberDiscordIds.Count == 0)
        {
            return new HashSet<string>(StringComparer.Ordinal);
        }

        var eligible = await _dbContext.DiscordActivityUsers
            .Where(discordUser =>
                discordUser.IdentityUserId != null &&
                memberDiscordIds.Contains(discordUser.DiscordUserId))
            .Select(discordUser => discordUser.IdentityUserId!)
            .Distinct()
            .ToListAsync(cancellationToken);

        return eligible.ToHashSet(StringComparer.Ordinal);
    }
}
