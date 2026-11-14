using LinkshellManagerDiscordApp.Data;
using LinkshellManagerDiscordApp.Models;
using Microsoft.EntityFrameworkCore;

namespace LinkshellManagerDiscordApp.Services;

// A player asks to join a linkshell; an officer who can manage invites approves or denies it.
// Requests are Invite rows with join-request statuses, so the invite lists and the "already
// invited?" checks see them too.
//
// Callers check WHO may do each thing (the officer's "Manage invites" permission, the linkshell's
// Discord server lock for the player). This service records, lists and answers requests.
//
// A request is never deleted when it is answered: it keeps who answered and when, so the player
// can see the answer where they asked.
public sealed class JoinRequestService
{
    public const string PendingStatus = "PendingJoinRequest";
    public const string ApprovedStatus = "JoinRequestApproved";
    public const string DeniedStatus = "JoinRequestDenied";
    public const string CancelledStatus = "JoinRequestCancelled";

    public static readonly string[] AllStatuses =
        new[] { PendingStatus, ApprovedStatus, DeniedStatus, CancelledStatus };

    public const int MaxMessageLength = 200;
    public const int MinQueryLength = 2;
    public const int SearchLimit = 10;

    // How long an approved or denied request stays on the player's list after it was answered.
    public static readonly TimeSpan AnsweredShownFor = TimeSpan.FromDays(14);

    internal const string Table = "Invites";

    private readonly ApplicationDbContext _db;

    public JoinRequestService(ApplicationDbContext db) => _db = db;

    // What a search shows about a linkshell: its name, its description and how many members it
    // has. Never who the members are or anything about DKP.
    public sealed record SearchResult(int Id, string Name, string? Details, int MemberCount);

    public enum Outcome { Done, NotFound, AlreadyAnswered, AlreadyMember, AlreadyRequested }

    public sealed record Result(Outcome Outcome, string? Error);

    // Linkshells this player could ask to join, by name. Listed: has a name and at least one
    // member. Left out: ones they are in, ones they already asked, and ones locked to a Discord
    // server other than the one this request came from (they could not join those from here).
    public async Task<List<SearchResult>> SearchAsync(
        string appUserId, string? query, string? requestGuildId, CancellationToken cancellationToken)
    {
        var text = query?.Trim() ?? string.Empty;
        if (text.Length < MinQueryLength)
        {
            return new List<SearchResult>();
        }

        var pattern = "%" + EscapeLike(text) + "%";
        var rows = await _db.Linkshells
            .AsNoTracking()
            .Where(linkshell =>
                linkshell.LinkshellName != null &&
                linkshell.LinkshellName.Trim() != "" &&
                EF.Functions.ILike(linkshell.LinkshellName, pattern, "\\") &&
                linkshell.AppUserLinkshells.Any() &&
                !linkshell.AppUserLinkshells.Any(link => link.AppUserId == appUserId) &&
                !_db.Invites.Any(invite =>
                    invite.LinkshellId == linkshell.Id &&
                    invite.AppUserId == appUserId &&
                    invite.Status == PendingStatus) &&
                (!linkshell.LockToDiscordGuild ||
                 linkshell.DiscordGuildId == null ||
                 linkshell.DiscordGuildId == "" ||
                 linkshell.DiscordGuildId == requestGuildId))
            .OrderBy(linkshell => linkshell.LinkshellName)
            .Take(SearchLimit)
            .Select(linkshell => new
            {
                linkshell.Id,
                linkshell.LinkshellName,
                linkshell.Details,
                MemberCount = linkshell.AppUserLinkshells.Count()
            })
            .ToListAsync(cancellationToken);

        return rows
            .Select(row => new SearchResult(row.Id, row.LinkshellName!, row.Details, row.MemberCount))
            .ToList();
    }

    // The player asks to join. One open request per player per linkshell; members cannot ask.
    public async Task<Result> RequestAsync(
        int linkshellId, string appUserId, string? message, CancellationToken cancellationToken)
    {
        var listed = await _db.Linkshells.AnyAsync(
            linkshell => linkshell.Id == linkshellId &&
                         linkshell.LinkshellName != null &&
                         linkshell.LinkshellName.Trim() != "" &&
                         linkshell.AppUserLinkshells.Any(),
            cancellationToken);
        if (!listed)
        {
            return new Result(Outcome.NotFound, "That linkshell was not found.");
        }

        if (await IsMemberAsync(linkshellId, appUserId, cancellationToken))
        {
            return new Result(Outcome.AlreadyMember, "You are already in that linkshell.");
        }

        if (await _db.Invites.AnyAsync(
                invite => invite.LinkshellId == linkshellId &&
                          invite.AppUserId == appUserId &&
                          invite.Status == PendingStatus,
                cancellationToken))
        {
            return AlreadyRequested;
        }

        var note = message?.Trim();
        var request = new Invite
        {
            AppUserId = appUserId,
            LinkshellId = linkshellId,
            Status = PendingStatus,
            Message = string.IsNullOrEmpty(note) ? null : note[..Math.Min(note.Length, MaxMessageLength)],
            CreatedAtUtc = DateTime.UtcNow
        };
        _db.Invites.Add(request);
        try
        {
            await _db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (UniqueRaceRetry.IsUniqueRejectionOn(ex, new[] { Table }))
        {
            // A second click saved the same request a moment earlier.
            _db.Entry(request).State = EntityState.Detached;
            return AlreadyRequested;
        }

        return new Result(Outcome.Done, null);
    }

    // The linkshell an open-or-answered join request belongs to, for the caller's permission
    // check. Null when there is no such request (or the id is an invite).
    public async Task<int?> LinkshellOfAsync(int requestId, CancellationToken cancellationToken)
    {
        var row = await _db.Invites
            .AsNoTracking()
            .Where(invite => invite.Id == requestId && AllStatuses.Contains(invite.Status))
            .Select(invite => new { invite.LinkshellId })
            .FirstOrDefaultAsync(cancellationToken);
        return row?.LinkshellId;
    }

    // Approve: the player joins the same way an accepted invite joins them, and the request is
    // marked approved with who and when. If they joined some other way first, the request is
    // still marked approved and nothing else changes.
    public Task<Result> ApproveAsync(int requestId, string officerAppUserId, CancellationToken cancellationToken) =>
        AnswerAsync(requestId, ApprovedStatus, officerAppUserId, requesterOnly: null, async (request, now) =>
        {
            var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == request.AppUserId, cancellationToken);
            if (user is null)
            {
                return new Result(Outcome.NotFound, "That player's account was not found.");
            }

            if (await IsMemberAsync(request.LinkshellId, user.Id, cancellationToken))
            {
                return new Result(Outcome.AlreadyMember, null);
            }

            var membership = LinkshellMembership.NewMember(user, request.LinkshellId, now);
            _db.AppUserLinkshells.Add(membership);
            user.PrimaryLinkshellId ??= request.LinkshellId;
            return new Result(Outcome.Done, null);
        }, cancellationToken);

    public Task<Result> DenyAsync(int requestId, string officerAppUserId, CancellationToken cancellationToken) =>
        AnswerAsync(requestId, DeniedStatus, officerAppUserId, requesterOnly: null, null, cancellationToken);

    // The player takes back their own open request.
    public Task<Result> CancelAsync(int requestId, string requesterAppUserId, CancellationToken cancellationToken) =>
        AnswerAsync(requestId, CancelledStatus, requesterAppUserId, requesterOnly: requesterAppUserId, null, cancellationToken);

    // Locks the request row, then answers it only if it is still open, so an approve and a deny
    // (or two approves) landing together cannot both win: the second waits for the lock and
    // then finds it answered.
    private async Task<Result> AnswerAsync(
        int requestId,
        string newStatus,
        string answeredByAppUserId,
        string? requesterOnly,
        Func<Invite, DateTime, Task<Result>>? beforeSave,
        CancellationToken cancellationToken)
    {
        var relational = _db.Database.IsRelational();
        await using var transaction = relational
            ? await _db.Database.BeginTransactionAsync(cancellationToken)
            : null;
        if (relational)
        {
            await _db.Database.ExecuteSqlInterpolatedAsync(
                $"""SELECT 1 FROM "Invites" WHERE "Id" = {requestId} FOR UPDATE""",
                cancellationToken);
        }

        // Read the request as it is now, not as an earlier read in this request saw it.
        _db.ChangeTracker.Clear();
        var request = await _db.Invites.FirstOrDefaultAsync(
            invite => invite.Id == requestId && AllStatuses.Contains(invite.Status),
            cancellationToken);
        if (request is null || (requesterOnly is not null && request.AppUserId != requesterOnly))
        {
            return new Result(Outcome.NotFound, "That join request was not found.");
        }
        if (request.Status != PendingStatus)
        {
            return new Result(Outcome.AlreadyAnswered, "That join request was already answered.");
        }

        var now = DateTime.UtcNow;
        var result = new Result(Outcome.Done, null);
        if (beforeSave is not null)
        {
            result = await beforeSave(request, now);
            if (result.Outcome == Outcome.NotFound)
            {
                return result;
            }
        }

        request.Status = newStatus;
        request.ResolvedAtUtc = now;
        request.ResolvedByAppUserId = answeredByAppUserId;
        try
        {
            await _db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (UniqueRaceRetry.IsDuplicateMembership(ex))
        {
            // They joined some other way (an invite, a Discord sign-up) while this ran. Still
            // mark the request approved; they are in, which is what it asked for.
            foreach (var added in _db.ChangeTracker.Entries<AppUserLinkshell>()
                         .Where(entry => entry.State == EntityState.Added).ToList())
            {
                added.State = EntityState.Detached;
            }
            await _db.SaveChangesAsync(cancellationToken);
            result = new Result(Outcome.AlreadyMember, null);
        }

        if (transaction is not null)
        {
            await transaction.CommitAsync(cancellationToken);
        }
        return result;
    }

    private Task<bool> IsMemberAsync(int linkshellId, string appUserId, CancellationToken cancellationToken) =>
        _db.AppUserLinkshells.AnyAsync(
            link => link.LinkshellId == linkshellId && link.AppUserId == appUserId, cancellationToken);

    private static readonly Result AlreadyRequested =
        new(Outcome.AlreadyRequested, "You already asked to join that linkshell.");

    // ILIKE treats % and _ as wildcards; a name search should match them as typed.
    private static string EscapeLike(string text) =>
        text.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");
}
