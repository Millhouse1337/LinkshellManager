using LinkshellManagerDiscordApp.Data;
using LinkshellManagerDiscordApp.Models;
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

// One member of a linkshell's Discord server who can be added straight from the
// roster. HasAccount = already an LSM user (added straight to the roster) vs.
// not (a Discord-keyed invite that auto-joins on their first sign-in).
public sealed record DiscordRosterCandidate(
    string DiscordUserId,
    string DisplayName,
    string? AvatarUrl,
    bool HasAccount);

public sealed record AddDiscordMemberResult(bool Success, string? Error);

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
                !user.IsPlaceholder && // linkshell-only placeholders aren't real, invitable accounts
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

    // Members of the linkshell's Discord server who can be added straight from
    // the roster — INCLUDING people who have never used LSM. Excludes current
    // members and anyone already holding a pending invite/join request. Returns
    // empty when the linkshell isn't tied to a server or the bot roster is
    // unavailable. Caller handles auth + the manage-invites permission.
    public async Task<IReadOnlyList<DiscordRosterCandidate>> GetDiscordRosterCandidatesAsync(
        int linkshellId,
        string? discordGuildId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(discordGuildId))
        {
            return Array.Empty<DiscordRosterCandidate>();
        }

        var members = await _discordIdentityService.TryGetGuildMembersAsync(discordGuildId, cancellationToken);
        if (members is null || members.Count == 0)
        {
            return Array.Empty<DiscordRosterCandidate>();
        }

        var rosterIds = members.Select(member => member.Id).ToList();

        var memberAppUserIds = await _dbContext.AppUserLinkshells
            .Where(link => link.LinkshellId == linkshellId && link.AppUserId != null)
            .Select(link => link.AppUserId!)
            .ToListAsync(cancellationToken);

        var pendingInvites = await _dbContext.Invites
            .Where(invite => invite.LinkshellId == linkshellId &&
                             PendingInviteStatuses.Contains(invite.Status))
            .Select(invite => new { invite.AppUserId, invite.DiscordUserId })
            .ToListAsync(cancellationToken);

        var excludedAppUserIds = memberAppUserIds
            .Concat(pendingInvites.Where(i => i.AppUserId != null).Select(i => i.AppUserId!))
            .Distinct()
            .ToList();

        var excludedDiscordIds = await _dbContext.DiscordActivityUsers
            .Where(discordUser => discordUser.IdentityUserId != null &&
                                  excludedAppUserIds.Contains(discordUser.IdentityUserId))
            .Select(discordUser => discordUser.DiscordUserId)
            .ToListAsync(cancellationToken);

        var excluded = new HashSet<string>(excludedDiscordIds, StringComparer.Ordinal);
        foreach (var pending in pendingInvites)
        {
            if (!string.IsNullOrWhiteSpace(pending.DiscordUserId))
            {
                excluded.Add(pending.DiscordUserId);
            }
        }

        // Which roster members already have an LSM account (drives add-straight
        // vs Discord-keyed invite, and a small badge in the UI).
        var existingAccountIds = await _dbContext.DiscordActivityUsers
            .Where(discordUser => discordUser.IdentityUserId != null && rosterIds.Contains(discordUser.DiscordUserId))
            .Select(discordUser => discordUser.DiscordUserId)
            .ToListAsync(cancellationToken);
        var hasAccount = new HashSet<string>(existingAccountIds, StringComparer.Ordinal);

        return members
            .Where(member => !excluded.Contains(member.Id))
            .OrderBy(member => member.GlobalName ?? member.Username)
            .Take(500)
            .Select(member => new DiscordRosterCandidate(
                member.Id,
                string.IsNullOrWhiteSpace(member.GlobalName) ? member.Username : member.GlobalName!,
                DiscordIdentityService.BuildAvatarUrl(member.Id, member.Avatar),
                hasAccount.Contains(member.Id)))
            .ToList();
    }

    // Adds a member of the linkshell's Discord server. If they already have an
    // LSM account they're added straight to the roster (auto-join); otherwise a
    // Discord-keyed invite is stored that auto-joins them on first sign-in.
    // Verifies the target is really in the server (never trust a raw id). Does
    // NOT save when it returns an error. Caller handles auth + permission.
    public async Task<AddDiscordMemberResult> AddDiscordMemberAsync(
        int linkshellId,
        string? discordGuildId,
        string? discordUserId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(discordGuildId))
        {
            return new AddDiscordMemberResult(false, "This linkshell isn't tied to a Discord server.");
        }

        var trimmedId = discordUserId?.Trim() ?? string.Empty;
        if (trimmedId.Length == 0)
        {
            return new AddDiscordMemberResult(false, "A Discord user is required.");
        }

        var members = await _discordIdentityService.TryGetGuildMembersAsync(discordGuildId, cancellationToken);
        if (members is null)
        {
            return new AddDiscordMemberResult(false, "Couldn't read the linkshell's Discord server. Make sure the bot is in it.");
        }

        var target = members.FirstOrDefault(member => member.Id == trimmedId);
        if (target is null)
        {
            return new AddDiscordMemberResult(false, "That person isn't a member of the linkshell's Discord server.");
        }

        var displayName = string.IsNullOrWhiteSpace(target.GlobalName) ? target.Username : target.GlobalName!;
        var linkshell = await _dbContext.Linkshells.FirstOrDefaultAsync(item => item.Id == linkshellId, cancellationToken);

        var existingDiscordUser = await _dbContext.DiscordActivityUsers
            .FirstOrDefaultAsync(
                discordUser => discordUser.DiscordUserId == trimmedId && discordUser.IdentityUserId != null,
                cancellationToken);

        if (existingDiscordUser is not null)
        {
            var targetAppUserId = existingDiscordUser.IdentityUserId!;

            var alreadyMember = await _dbContext.AppUserLinkshells
                .AnyAsync(link => link.LinkshellId == linkshellId && link.AppUserId == targetAppUserId, cancellationToken);
            if (alreadyMember)
            {
                return new AddDiscordMemberResult(false, "That player is already a member of this linkshell.");
            }

            var existingInvite = await _dbContext.Invites
                .AnyAsync(invite => invite.LinkshellId == linkshellId &&
                                    invite.AppUserId == targetAppUserId &&
                                    PendingInviteStatuses.Contains(invite.Status),
                    cancellationToken);
            if (existingInvite)
            {
                return new AddDiscordMemberResult(false, "A pending invite or join request already exists for that player.");
            }

            // Auto-join the existing LSM user straight onto the roster.
            var targetUser = await _dbContext.Users.FindAsync(new object?[] { targetAppUserId }, cancellationToken);
            _dbContext.AppUserLinkshells.Add(new AppUserLinkshell
            {
                AppUserId = targetAppUserId,
                LinkshellId = linkshellId,
                LinkshellDkp = 0,
                DateJoined = DateTime.UtcNow,
                CharacterName = targetUser?.CharacterName ?? targetUser?.UserName ?? displayName,
                Rank = LinkshellRanks.Member,
                Status = "Active"
            });
            if (targetUser is not null)
            {
                targetUser.PrimaryLinkshellId ??= linkshellId;
                targetUser.PrimaryLinkshellName ??= linkshell?.LinkshellName;
            }
        }
        else
        {
            var existingDiscordInvite = await _dbContext.Invites
                .AnyAsync(invite => invite.LinkshellId == linkshellId &&
                                    invite.DiscordUserId == trimmedId &&
                                    invite.Status == "PendingInvite",
                    cancellationToken);
            if (existingDiscordInvite)
            {
                return new AddDiscordMemberResult(false, "That person has already been invited.");
            }

            // No LSM account yet — keep a Discord-keyed pending invite; it
            // auto-joins them on their first sign-in (DiscordIdentityService).
            _dbContext.Invites.Add(new Invite
            {
                AppUserId = null,
                LinkshellId = linkshellId,
                Status = "PendingInvite",
                DiscordUserId = trimmedId,
                DiscordDisplayName = displayName
            });
        }

        await _dbContext.SaveChangesAsync(cancellationToken);
        return new AddDiscordMemberResult(true, null);
    }
}
