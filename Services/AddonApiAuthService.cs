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

    public async Task<bool> RevokeAsync(
        int tokenId,
        int linkshellId,
        CancellationToken cancellationToken = default)
    {
        var record = await _dbContext.AddonApiTokens
            .FirstOrDefaultAsync(t => t.Id == tokenId && t.LinkshellId == linkshellId, cancellationToken);
        if (record is null) return false;
        if (record.RevokedAt is not null) return true;

        record.RevokedAt = DateTime.UtcNow;
        await _dbContext.SaveChangesAsync(cancellationToken);
        return true;
    }

    public Task<List<AddonApiToken>> ListActiveAsync(
        int linkshellId,
        CancellationToken cancellationToken = default)
    {
        return _dbContext.AddonApiTokens
            .Where(t => t.LinkshellId == linkshellId && t.RevokedAt == null)
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
