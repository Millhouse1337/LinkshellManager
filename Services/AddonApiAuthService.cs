using System.Security.Cryptography;
using System.Text;
using LinkshellManagerDiscordApp.Data;
using LinkshellManagerDiscordApp.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace LinkshellManagerDiscordApp.Services;

public sealed class AddonApiAuthService
{
    private const string TokenPrefix = "att_";
    private const int TokenBodyLength = 36;
    private const int PairingCodeLength = 8;
    private const int PairingCodeTtlMinutes = 10;
    // Issued tokens expire after this many days of inactivity. The addon will
    // need to re-pair after a long quiet period.
    private const int TokenInactivityExpiryDays = 90;
    // Issued tokens also expire this many days after they were made, however busy they are.
    // A polling addon refreshes LastUsedAt every minute, so the inactivity rule alone meant a
    // token never died; this caps how long a leaked or forgotten one stays useful.
    internal const int TokenAbsoluteLifetimeDays = 180;
    // How long one token's "is the issuer still a member who may use the addon" answer is
    // reused. Every addon request validates its token, so this keeps the membership check off
    // the database on most calls. A kick or leave revokes the token outright (RevokedAt is read
    // before the cache), so this only delays a role losing "Use the addon" by up to a minute.
    private static readonly TimeSpan IssuerAccessCacheTtl = TimeSpan.FromSeconds(60);
    private const string IssuerAccessCachePrefix = "addon-token-access:";
    // Throttle LastUsedAt writes so a polling addon doesn't issue a DB write per request.
    private const int LastUsedAtThrottleSeconds = 60;

    // base32-style alphabet (no easily-confused chars: I/1/O/0)
    private const string PairingAlphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
    private const string TokenAlphabet =
        "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789";

    private readonly ApplicationDbContext _dbContext;
    private readonly GlobalSettingsService _globalSettings;
    private readonly IMemoryCache _cache;

    public AddonApiAuthService(
        ApplicationDbContext dbContext, GlobalSettingsService globalSettings, IMemoryCache cache)
    {
        _dbContext = dbContext;
        _globalSettings = globalSettings;
        _cache = cache;
    }

    public async Task<string> CreatePairingCodeAsync(
        int linkshellId,
        string appUserId,
        string? label,
        CancellationToken cancellationToken = default)
    {
        var now = DateTime.UtcNow;

        // Best-effort cleanup of expired/consumed codes.
        var stale = await _dbContext.AddonPairingCodes
            .Where(c => c.LinkshellId == linkshellId
                        && (c.ExpiresAt < now || c.ConsumedAt != null))
            .ToListAsync(cancellationToken);
        if (stale.Count > 0)
        {
            _dbContext.AddonPairingCodes.RemoveRange(stale);
        }

        // Retry on the (very unlikely) collision of an 8-char code.
        for (var attempt = 0; attempt < 5; attempt++)
        {
            var code = GenerateRandomString(PairingAlphabet, PairingCodeLength);
            var exists = await _dbContext.AddonPairingCodes
                .AnyAsync(c => c.Code == code, cancellationToken);
            if (exists) continue;

            _dbContext.AddonPairingCodes.Add(new AddonPairingCode
            {
                Code = code,
                LinkshellId = linkshellId,
                IssuedToAppUserId = appUserId,
                Label = string.IsNullOrWhiteSpace(label) ? null : label.Trim(),
                CreatedAt = now,
                ExpiresAt = now.AddMinutes(PairingCodeTtlMinutes)
            });

            await _dbContext.SaveChangesAsync(cancellationToken);
            return code;
        }

        throw new InvalidOperationException("Could not generate a unique pairing code.");
    }

    public sealed record RedeemResult(string RawToken, AddonApiToken Record, Linkshell Linkshell);

    public async Task<RedeemResult?> RedeemPairingCodeAsync(
        string code,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(code)) return null;
        var normalized = code.Trim().ToUpperInvariant();

        var pairing = await _dbContext.AddonPairingCodes
            .Include(c => c.Linkshell)
            .FirstOrDefaultAsync(c => c.Code == normalized, cancellationToken);

        if (pairing is null) return null;

        var now = DateTime.UtcNow;
        if (pairing.ConsumedAt is not null) return null;
        if (pairing.ExpiresAt < now) return null;
        if (pairing.Linkshell is null) return null;

        var rawToken = TokenPrefix + GenerateRandomString(TokenAlphabet, TokenBodyLength);
        var record = new AddonApiToken
        {
            LinkshellId = pairing.LinkshellId,
            IssuedToAppUserId = pairing.IssuedToAppUserId,
            TokenHash = HashToken(rawToken),
            TokenPrefix = rawToken[..Math.Min(rawToken.Length, 12)],
            Label = pairing.Label,
            CreatedAt = now
        };

        await using var tx = await _dbContext.Database.BeginTransactionAsync(cancellationToken);
        _dbContext.AddonApiTokens.Add(record);
        await _dbContext.SaveChangesAsync(cancellationToken);

        pairing.ConsumedAt = now;
        pairing.ConsumedTokenId = record.Id;
        await _dbContext.SaveChangesAsync(cancellationToken);
        await tx.CommitAsync(cancellationToken);

        return new RedeemResult(rawToken, record, pairing.Linkshell);
    }

    // Redeems the code, then mints a token for every OTHER linkshell the SAME
    // user is a member of, so one pairing code sets the addon up for all of
    // them in a single /lsm link. Returns the redeemed linkshell first.
    //
    // Two properties this deliberately preserves:
    //   * Tokens stay PER-LINKSHELL. Only the number issued changes, not the
    //     authorization model -- every request is still authorized against one
    //     linkshell, and a single one can still be revoked on its own.
    //   * It only fans out when the code records who it was issued to. A code
    //     with no IssuedToAppUserId can't prove whose memberships to grant, so
    //     it stays scoped to its own linkshell rather than guessing.
    //
    // SECURITY NOTE: the pairing code is the credential (the addon has no
    // session to present), so widening one code to N linkshells widens what a
    // leaked code exposes -- from one linkshell to all of that user's. The
    // existing mitigations still apply and bound this: codes are single-use
    // (ConsumedAt), expire in PairingCodeTtlMinutes, and the endpoint is
    // per-IP rate limited.
    public async Task<IReadOnlyList<RedeemResult>> RedeemPairingCodeForAllAsync(
        string code,
        CancellationToken cancellationToken = default)
    {
        var primary = await RedeemPairingCodeAsync(code, cancellationToken);
        if (primary is null) return Array.Empty<RedeemResult>();

        var results = new List<RedeemResult> { primary };

        var appUserId = primary.Record.IssuedToAppUserId;
        if (string.IsNullOrEmpty(appUserId)) return results;


        // Every other linkshell this user actually belongs to. Membership is
        // the authority -- we never mint a token for a linkshell they aren't
        // in, so this grants nothing they couldn't already reach on the web.
        var memberships = await _dbContext.AppUserLinkshells
            .AsNoTracking()
            .Include(m => m.Linkshell)
            .Where(m => m.AppUserId == appUserId && m.LinkshellId != primary.Linkshell.Id)
            .ToListAsync(cancellationToken);

        // ...and only the ones whose role may use the addon. Pairing on linkshell A must not
        // hand out a token for linkshell B where this member's role has "Use the addon" off --
        // that is the same check the Pair button runs, applied to every linkshell in the batch.
        var otherMemberships = new List<Linkshell>();
        foreach (var membership in memberships)
        {
            if (membership.Linkshell is null) continue;
            if (await MemberCanUseAddonAsync(membership, cancellationToken))
            {
                otherMemberships.Add(membership.Linkshell);
            }
        }

        // Nothing to fan out to: leave the token unbatched. A "batch" of one
        // behaves identically to the pre-batch single-linkshell pairing.
        if (otherMemberships.Count == 0) return results;

        // Tag every token from this code with one batch id so revoking any of
        // them revokes the lot -- see RevokeAsync. Stamped on the primary too,
        // since the row the user clicks Revoke on is usually that one.
        var batchId = Guid.NewGuid();
        var primaryRecord = await _dbContext.AddonApiTokens
            .FirstOrDefaultAsync(t => t.Id == primary.Record.Id, cancellationToken);
        if (primaryRecord is not null) primaryRecord.PairingBatchId = batchId;

        var now = DateTime.UtcNow;
        foreach (var linkshell in otherMemberships)
        {
            var rawToken = TokenPrefix + GenerateRandomString(TokenAlphabet, TokenBodyLength);
            var record = new AddonApiToken
            {
                LinkshellId = linkshell.Id,
                IssuedToAppUserId = appUserId,
                TokenHash = HashToken(rawToken),
                TokenPrefix = rawToken[..Math.Min(rawToken.Length, 12)],
                Label = primary.Record.Label,
                PairingBatchId = batchId,
                CreatedAt = now
            };
            _dbContext.AddonApiTokens.Add(record);
            results.Add(new RedeemResult(rawToken, record, linkshell));
        }

        await _dbContext.SaveChangesAsync(cancellationToken);
        return results;
    }

    public async Task<AddonApiToken?> ValidateTokenAsync(
        string rawToken,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(rawToken)) return null;
        if (!rawToken.StartsWith(TokenPrefix, StringComparison.Ordinal)) return null;

        var hash = HashToken(rawToken);
        var record = await _dbContext.AddonApiTokens
            .Include(t => t.Linkshell)
            .FirstOrDefaultAsync(t => t.TokenHash == hash, cancellationToken);

        if (record is null) return null;
        if (record.RevokedAt is not null) return null;

        // Global kill-switch: a super admin can disable the addon for everyone.
        // Checked here (the single per-request choke point shared by every
        // [AddonApiAuth] endpoint) so one flag blocks all in-game addon traffic.
        if (await _globalSettings.IsAddonGloballyDisabledAsync(cancellationToken)) return null;

        var now = DateTime.UtcNow;

        // Absolute lifetime: however often the addon polls, a token stops working this long
        // after it was made, and the member pairs again.
        if ((now - record.CreatedAt).TotalDays >= TokenAbsoluteLifetimeDays)
        {
            record.RevokedAt = now;
            await _dbContext.SaveChangesAsync(cancellationToken);
            return null;
        }

        // Inactivity expiry — tokens unused for more than the configured window
        // are treated as revoked. Anchored to LastUsedAt when set, otherwise to
        // CreatedAt so brand-new tokens can still be redeemed before first use.
        var inactivityAnchor = record.LastUsedAt ?? record.CreatedAt;
        if ((now - inactivityAnchor).TotalDays >= TokenInactivityExpiryDays)
        {
            record.RevokedAt = now;
            await _dbContext.SaveChangesAsync(cancellationToken);
            return null;
        }

        // The token is only as good as its owner's membership. Without this, a member who was
        // kicked (or whose role lost "Use the addon") kept reading ToDs, camps and the roster in
        // game for as long as their addon kept polling. Rejected, not revoked: if the role gets
        // the permission back, the same pairing works again without a new code.
        if (!await IssuerMayUseTokenAsync(record, cancellationToken)) return null;

        if (record.LastUsedAt is null
            || (now - record.LastUsedAt.Value).TotalSeconds >= LastUsedAtThrottleSeconds)
        {
            record.LastUsedAt = now;
            await _dbContext.SaveChangesAsync(cancellationToken);
        }

        return record;
    }

    // Is the person this token was issued to still a member of its linkshell, with a role that
    // may use the addon? Cached per token for IssuerAccessCacheTtl because every addon request
    // asks. A token with no issuer (its account was deleted, or it predates IssuedToAppUserId)
    // cannot prove whose membership it rides on, so it is refused outright.
    private async Task<bool> IssuerMayUseTokenAsync(AddonApiToken token, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(token.IssuedToAppUserId)) return false;

        var cacheKey = IssuerAccessCachePrefix + token.Id;
        if (_cache.TryGetValue(cacheKey, out bool cached)) return cached;

        var membership = await _dbContext.AppUserLinkshells
            .AsNoTracking()
            .FirstOrDefaultAsync(
                m => m.LinkshellId == token.LinkshellId && m.AppUserId == token.IssuedToAppUserId,
                cancellationToken);
        var allowed = await MemberCanUseAddonAsync(membership, cancellationToken);

        _cache.Set(cacheKey, allowed, IssuerAccessCacheTtl);
        return allowed;
    }

    // Can this member pair and use the game addon?
    //
    // Its own permission (LinkshellRole.CanUseAddon), granted to every default role. It used to be
    // the Leader/Officer RANK, which meant a linkshell that wanted an ordinary member scanning
    // rosters in game had to promote them -- and in the Activity the same card was gated on
    // "Customize linkshell settings", so handing someone the addon handed them the linkshell's
    // configuration with it.
    //
    // The leader is never locked out by a stale role row, matching ActivityDataController.CanAsync.
    // A linkshell whose default roles were never seeded has no row to read, and falls back to the
    // old rank test -- so this can only widen access, never take it from an officer who had it.
    //
    // Lives here (not in the controller) so the Pair button, the pairing fan-out and every
    // token check read the one rule.
    public async Task<bool> MemberCanUseAddonAsync(
        AppUserLinkshell? membership, CancellationToken cancellationToken = default)
    {
        if (membership is null) return false;
        if (LinkshellRanks.IsLeader(membership.Rank)) return true;

        var rank = string.IsNullOrWhiteSpace(membership.Rank)
            ? LinkshellRanks.Member
            : membership.Rank.Trim();
        var role = await _dbContext.LinkshellRoles
            .AsNoTracking()
            .FirstOrDefaultAsync(
                r => r.LinkshellId == membership.LinkshellId && r.Name == rank, cancellationToken)
            ?? await _dbContext.LinkshellRoles
                .AsNoTracking()
                .FirstOrDefaultAsync(
                    r => r.LinkshellId == membership.LinkshellId && r.Name == LinkshellRanks.Member,
                    cancellationToken);

        return role?.CanUseAddon ?? LinkshellRanks.IsLeaderOrOfficer(membership.Rank);
    }

    // Revokes every addon token one member holds for ONE linkshell, and drops their unused
    // pairing codes there. Called when the member is removed or leaves, so their game client
    // stops reading that linkshell at once instead of whenever the cache next looks.
    //
    // Only this linkshell: tokens from the same pairing for the member's OTHER linkshells keep
    // working, because they are still a member there.
    //
    // Stages the changes and does NOT save: the caller's SaveChangesAsync writes them together
    // with the membership removal, so a member can never end up removed with live tokens (or
    // the other way round). Returns how many tokens it revoked.
    public async Task<int> RevokeAllForMemberAsync(
        string appUserId,
        int linkshellId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(appUserId)) return 0;

        var now = DateTime.UtcNow;
        var tokens = await _dbContext.AddonApiTokens
            .Where(t => t.IssuedToAppUserId == appUserId
                        && t.LinkshellId == linkshellId
                        && t.RevokedAt == null)
            .ToListAsync(cancellationToken);
        foreach (var token in tokens)
        {
            token.RevokedAt = now;
        }

        var unusedCodes = await _dbContext.AddonPairingCodes
            .Where(c => c.IssuedToAppUserId == appUserId
                        && c.LinkshellId == linkshellId
                        && c.ConsumedAt == null)
            .ToListAsync(cancellationToken);
        if (unusedCodes.Count > 0)
        {
            _dbContext.AddonPairingCodes.RemoveRange(unusedCodes);
        }

        return tokens.Count;
    }

    // Revokes a token AND every sibling minted from the same pairing code.
    //
    // One code now pairs all of a user's linkshells, so revoking only the row
    // the page happens to show would leave the addon still authenticated
    // against the others -- the user clicks Revoke, sees the row disappear,
    // and the addon keeps working. Killing the batch makes Revoke mean what it
    // looks like it means.
    //
    // Tokens predating PairingBatchId (null) revoke individually, exactly as
    // they did when each linkshell was paired separately.
    public async Task<bool> RevokeAsync(
        int tokenId,
        int linkshellId,
        CancellationToken cancellationToken = default)
    {
        var record = await _dbContext.AddonApiTokens
            .FirstOrDefaultAsync(t => t.Id == tokenId && t.LinkshellId == linkshellId, cancellationToken);
        if (record is null) return false;
        if (record.RevokedAt is not null) return true;

        var now = DateTime.UtcNow;
        record.RevokedAt = now;

        if (record.PairingBatchId is Guid batchId)
        {
            var siblings = await _dbContext.AddonApiTokens
                .Where(t => t.PairingBatchId == batchId
                            && t.Id != record.Id
                            && t.RevokedAt == null)
                .ToListAsync(cancellationToken);
            foreach (var sibling in siblings)
            {
                sibling.RevokedAt = now;
            }
        }

        await _dbContext.SaveChangesAsync(cancellationToken);
        return true;
    }

    // THE VIEWER'S OWN active pairings, and nobody else's.
    //
    // A pairing is a personal credential: it belongs to one member's game client, and the Game
    // Addon card is where that member manages theirs. This used to also return every token bound
    // to the linkshell being viewed, so a leader could revoke somebody else's pairing from the same
    // table -- which meant opening your own settings page listed other people's devices back at
    // you, tagged "another member". Nobody asked their linkshell's settings page who else has the
    // addon installed.
    //
    // Revoking someone else's is untouched as a CAPABILITY: RevokeTokenAsync still allows it with
    // manage rights on that token's linkshell. It simply is not something this page volunteers.
    //
    // Not scoped to one linkshell, deliberately: one pairing code mints a token for every linkshell
    // the user belongs to, so the card must not read "no active tokens" merely because a different
    // linkshell happens to be selected. ListTokensAsync collapses the batch back into the single
    // pairing the user actually performed.
    public Task<List<AddonApiToken>> ListForUserAsync(
        string appUserId,
        CancellationToken cancellationToken = default)
    {
        return _dbContext.AddonApiTokens
            .Include(t => t.Linkshell)
            .Where(t => t.RevokedAt == null && t.IssuedToAppUserId == appUserId)
            .OrderByDescending(t => t.CreatedAt)
            .ToListAsync(cancellationToken);
    }

    private static string HashToken(string raw)
    {
        var bytes = Encoding.UTF8.GetBytes(raw);
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash);
    }

    private static string GenerateRandomString(string alphabet, int length)
    {
        Span<byte> bytes = stackalloc byte[length];
        RandomNumberGenerator.Fill(bytes);
        var sb = new StringBuilder(length);
        for (var i = 0; i < length; i++)
        {
            sb.Append(alphabet[bytes[i] % alphabet.Length]);
        }
        return sb.ToString();
    }
}
