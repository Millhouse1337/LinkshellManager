using LinkshellManagerDiscordApp.Data;
using LinkshellManagerDiscordApp.Models;
using Microsoft.EntityFrameworkCore;

namespace LinkshellManagerDiscordApp.Services;

// By-name claims of imported (placeholder) members wait for an officer. See PlaceholderClaimRequest
// for why: a character name is public, so typing one must not hand over that member's DKP.
//
// Callers check WHO may do each thing (the requester's name match and guild lock, the officer's
// "Manage members" permission); this service only records, lists and resolves requests.
public sealed class PlaceholderClaimRequestService
{
    internal const string Table = "PlaceholderClaimRequests";

    private readonly ApplicationDbContext _db;
    private readonly PlaceholderClaimService _claims;

    public PlaceholderClaimRequestService(ApplicationDbContext db, PlaceholderClaimService claims)
    {
        _db = db;
        _claims = claims;
    }

    // One open request for an officer to see. RequesterKind: "account" (a signed-in LSM account) or
    // "discord" (the Discord sign-up form, no account yet).
    public sealed record PendingRequest(
        int Id, int MembershipId, string PlaceholderName, double PlaceholderDkp,
        string RequesterName, string RequesterKind, DateTime CreatedAtUtc);

    public sealed record ResolveResult(bool Success, string? Error);

    // A signed-in account asks to take over a placeholder. Returns false when the same account
    // already has an open request for it (nothing new is added).
    public Task<bool> RequestForAccountAsync(
        int linkshellId, string placeholderAppUserId, string requesterAppUserId, string? requesterName,
        CancellationToken cancellationToken) =>
        AddAsync(new PlaceholderClaimRequest
        {
            LinkshellId = linkshellId,
            PlaceholderAppUserId = placeholderAppUserId,
            RequesterAppUserId = requesterAppUserId,
            RequesterName = Trim(requesterName),
        }, cancellationToken);

    // A Discord user without an account typed a placeholder's name in the sign-up form.
    public Task<bool> RequestForDiscordAsync(
        int linkshellId, string placeholderAppUserId, string discordUserId, string? requesterName,
        CancellationToken cancellationToken) =>
        AddAsync(new PlaceholderClaimRequest
        {
            LinkshellId = linkshellId,
            PlaceholderAppUserId = placeholderAppUserId,
            RequesterDiscordUserId = discordUserId,
            RequesterName = Trim(requesterName),
        }, cancellationToken);

    // Open requests by this account, for marking "waiting for an officer" on its claim prompt.
    public async Task<HashSet<(string PlaceholderAppUserId, int LinkshellId)>> PendingForAccountAsync(
        string requesterAppUserId, CancellationToken cancellationToken)
    {
        var rows = await _db.PlaceholderClaimRequests
            .AsNoTracking()
            .Where(r => r.RequesterAppUserId == requesterAppUserId && r.ResolvedAtUtc == null)
            .Select(r => new { r.PlaceholderAppUserId, r.LinkshellId })
            .ToListAsync(cancellationToken);
        return rows.Select(r => (r.PlaceholderAppUserId, r.LinkshellId)).ToHashSet();
    }

    // This linkshell's open requests whose placeholder is still unclaimed here. A request whose
    // placeholder was claimed some other way (another request, the officer's Link action, the
    // player's own launch) has nothing left to approve and is not shown.
    public async Task<List<PendingRequest>> ListPendingAsync(int linkshellId, CancellationToken cancellationToken)
    {
        var rows = await (
                from request in _db.PlaceholderClaimRequests.AsNoTracking()
                join member in _db.AppUserLinkshells.AsNoTracking()
                    on new { request.LinkshellId, AppUserId = (string?)request.PlaceholderAppUserId }
                    equals new { member.LinkshellId, member.AppUserId }
                where request.LinkshellId == linkshellId
                      && request.ResolvedAtUtc == null
                      && member.AppUser != null
                      && member.AppUser.IsPlaceholder
                orderby request.CreatedAtUtc
                select new
                {
                    request.Id,
                    MembershipId = member.Id,
                    PlaceholderName = member.CharacterName ?? member.AppUser!.CharacterName,
                    member.LinkshellDkp,
                    request.RequesterName,
                    request.RequesterAppUserId,
                    request.CreatedAtUtc,
                })
            .ToListAsync(cancellationToken);

        return rows
            .Select(r => new PendingRequest(
                r.Id,
                r.MembershipId,
                r.PlaceholderName ?? "Unknown",
                r.LinkshellDkp ?? 0,
                r.RequesterName ?? "Unknown",
                r.RequesterAppUserId is null ? "discord" : "account",
                r.CreatedAtUtc))
            .ToList();
    }

    // Approve: an account request runs the claim onto that account; a Discord request links the
    // placeholder to that Discord user (confirmed, so their first launch takes it over). Other open
    // requests for the same placeholder are closed as denied: it is no longer there to claim.
    public async Task<ResolveResult> ApproveAsync(
        int linkshellId, int requestId, string officerAppUserId, CancellationToken cancellationToken)
    {
        var request = await LoadOpenAsync(linkshellId, requestId, cancellationToken);
        if (request is null)
        {
            return new ResolveResult(false, "That request was already handled.");
        }

        if (request.RequesterAppUserId is not null)
        {
            var claim = await _claims.ClaimPlaceholderAsync(
                request.PlaceholderAppUserId, request.RequesterAppUserId, linkshellId, cancellationToken);
            if (!claim.Success)
            {
                return new ResolveResult(false, claim.Error);
            }
        }
        else
        {
            var membership = await _db.AppUserLinkshells
                .Include(m => m.AppUser)
                .FirstOrDefaultAsync(
                    m => m.LinkshellId == linkshellId && m.AppUserId == request.PlaceholderAppUserId,
                    cancellationToken);
            if (membership?.AppUser is null || !membership.AppUser.IsPlaceholder)
            {
                return new ResolveResult(false, "That member is not an unclaimed placeholder any more.");
            }
            if (!string.IsNullOrEmpty(membership.DiscordUserId)
                && !string.Equals(membership.DiscordUserId, request.RequesterDiscordUserId, StringComparison.Ordinal))
            {
                return new ResolveResult(false, "That member is already linked to another Discord user.");
            }
            // Sign Up finds a form-registered player by their Discord id, so one id may sit on only
            // one roster entry here.
            if (await _db.AppUserLinkshells.AnyAsync(
                    m => m.LinkshellId == linkshellId && m.Id != membership.Id
                         && m.DiscordUserId == request.RequesterDiscordUserId,
                    cancellationToken))
            {
                return new ResolveResult(false, "That Discord user is already linked to another member of this roster.");
            }
            membership.DiscordUserId = request.RequesterDiscordUserId;
            membership.DiscordIdConfirmed = true;
        }

        var now = DateTime.UtcNow;
        // Re-read after the claim: it may have cleared the tracker (Data/ConcurrencyRetry.cs).
        var open = await _db.PlaceholderClaimRequests
            .Where(r => r.LinkshellId == linkshellId
                        && r.PlaceholderAppUserId == request.PlaceholderAppUserId
                        && r.ResolvedAtUtc == null)
            .ToListAsync(cancellationToken);
        foreach (var row in open)
        {
            row.ResolvedAtUtc = now;
            row.Approved = row.Id == requestId;
            row.ResolvedByAppUserId = officerAppUserId;
        }
        await _db.SaveChangesAsync(cancellationToken);
        return new ResolveResult(true, null);
    }

    public async Task<ResolveResult> DenyAsync(
        int linkshellId, int requestId, string officerAppUserId, CancellationToken cancellationToken)
    {
        var request = await LoadOpenAsync(linkshellId, requestId, cancellationToken);
        if (request is null)
        {
            return new ResolveResult(false, "That request was already handled.");
        }
        request.ResolvedAtUtc = DateTime.UtcNow;
        request.Approved = false;
        request.ResolvedByAppUserId = officerAppUserId;
        await _db.SaveChangesAsync(cancellationToken);
        return new ResolveResult(true, null);
    }

    private Task<PlaceholderClaimRequest?> LoadOpenAsync(int linkshellId, int requestId, CancellationToken cancellationToken) =>
        _db.PlaceholderClaimRequests.FirstOrDefaultAsync(
            r => r.Id == requestId && r.LinkshellId == linkshellId && r.ResolvedAtUtc == null, cancellationToken);

    // Adds the request unless this person already has an open one for that placeholder. Two clicks
    // at once both pass the check; the unique index turns the later one away and it is treated the
    // same as "already asked".
    private async Task<bool> AddAsync(PlaceholderClaimRequest request, CancellationToken cancellationToken)
    {
        var exists = await _db.PlaceholderClaimRequests.AnyAsync(
            r => r.PlaceholderAppUserId == request.PlaceholderAppUserId
                 && r.ResolvedAtUtc == null
                 && ((request.RequesterAppUserId != null && r.RequesterAppUserId == request.RequesterAppUserId)
                     || (request.RequesterDiscordUserId != null && r.RequesterDiscordUserId == request.RequesterDiscordUserId)),
            cancellationToken);
        if (exists)
        {
            return false;
        }

        request.CreatedAtUtc = DateTime.UtcNow;
        _db.PlaceholderClaimRequests.Add(request);
        try
        {
            await _db.SaveChangesAsync(cancellationToken);
            return true;
        }
        catch (DbUpdateException ex) when (UniqueRaceRetry.IsUniqueRejectionOn(ex, new[] { Table }))
        {
            _db.Entry(request).State = EntityState.Detached;
            return false;
        }
    }

    private static string? Trim(string? value)
    {
        var trimmed = value?.Trim();
        if (string.IsNullOrEmpty(trimmed)) return null;
        return trimmed.Length > 256 ? trimmed[..256] : trimmed;
    }
}
