using System.Security.Cryptography;
using System.Text;
using LinkshellManagerDiscordApp.Data;
using LinkshellManagerDiscordApp.Models;
using Microsoft.EntityFrameworkCore;

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
    // Throttle LastUsedAt writes so a polling addon doesn't issue a DB write per request.
    private const int LastUsedAtThrottleSeconds = 60;

    // base32-style alphabet (no easily-confused chars: I/1/O/0)
    private const string PairingAlphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
    private const string TokenAlphabet =
        "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789";

    private readonly ApplicationDbContext _dbContext;
    private readonly GlobalSettingsService _globalSettings;

    public AddonApiAuthService(ApplicationDbContext dbContext, GlobalSettingsService globalSettings)
    {
        _dbContext = dbContext;
        _globalSettings = globalSettings;
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
        var otherMemberships = await _dbContext.AppUserLinkshells
            .AsNoTracking()
            .Where(m => m.AppUserId == appUserId && m.LinkshellId != primary.Linkshell.Id)
            .Join(_dbContext.Linkshells.AsNoTracking(),
                  m => m.LinkshellId,
                  ls => ls.Id,
                  (m, ls) => ls)
            .ToListAsync(cancellationToken);

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

        if (record.LastUsedAt is null
            || (now - record.LastUsedAt.Value).TotalSeconds >= LastUsedAtThrottleSeconds)
        {
            record.LastUsedAt = now;
            await _dbContext.SaveChangesAsync(cancellationToken);
        }

        return record;
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

    // Every active token the Game Addon page should show a viewer: the ones
    // bound to the linkshell they're looking at (so a leader can still see and
    // revoke someone else's pairing on their own linkshell) plus every token
    // issued to the viewer, whichever linkshell it landed on.
    //
    // That second half is the point: one pairing code mints a token for every
    // linkshell the user belongs to, so the card must not read "no active
    // tokens" just because a different linkshell happens to be selected.
    public Task<List<AddonApiToken>> ListVisibleAsync(
        int linkshellId,
        string appUserId,
        CancellationToken cancellationToken = default)
    {
        return _dbContext.AddonApiTokens
            .Include(t => t.Linkshell)
            .Where(t => t.RevokedAt == null
                        && (t.LinkshellId == linkshellId || t.IssuedToAppUserId == appUserId))
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
