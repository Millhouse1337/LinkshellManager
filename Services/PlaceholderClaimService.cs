using LinkshellManagerDiscordApp.Data;
using LinkshellManagerDiscordApp.Models;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace LinkshellManagerDiscordApp.Services;

// Associates an imported PLACEHOLDER member (AppUser.IsPlaceholder = true, created
// by the DKP import carrying a balance + lifetime seed) to a REAL account when
// that person first appears in the app. Two shapes per linkshell:
//   * the real user is NOT yet a member there  -> repoint the placeholder
//     membership to the real account (DKP/seeds ride along untouched).
//   * the real user ALREADY has a membership there -> merge: imported DKP wins
//     (the stock membership likely started at 0, so summing would double-count),
//     the placeholder's ledger rows are repointed for the audit trail, and the
//     watermark is advanced so pre-claim ledger doesn't add on top of the seed.
// Either way every DkpLedgerEntry that pointed at the placeholder (for the claimed
// linkshells) is repointed to the real account so DKP history survives, and the
// placeholder AppUser is deleted once nothing references it. The whole operation
// runs in one transaction.
//
// NOTE: the Discord-id auto-claim path (DiscordIdentityService) does NOT use this
// service — it PROMOTES the placeholder in place (AppUserId never changes), which
// needs no repointing. This service is for the name-matched claim and the officer
// "link placeholder -> account" action, where a separate real AppUser exists.
public sealed class PlaceholderClaimService
{
    private readonly ApplicationDbContext _db;
    private readonly UserManager<AppUser> _userManager;

    public PlaceholderClaimService(ApplicationDbContext db, UserManager<AppUser> userManager)
    {
        _db = db;
        _userManager = userManager;
    }

    public sealed record ClaimResult(bool Success, string? Error, IReadOnlyList<int> ClaimedLinkshellIds);

    public async Task<ClaimResult> ClaimPlaceholderAsync(
        string placeholderAppUserId, string realAppUserId, int? linkshellId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(placeholderAppUserId) || string.IsNullOrWhiteSpace(realAppUserId)
            || placeholderAppUserId == realAppUserId)
        {
            return new ClaimResult(false, "Invalid claim target.", Array.Empty<int>());
        }

        var placeholder = await _db.Users.FirstOrDefaultAsync(u => u.Id == placeholderAppUserId, cancellationToken);
        if (placeholder is null || !placeholder.IsPlaceholder)
        {
            return new ClaimResult(false, "That member is not an unclaimed placeholder.", Array.Empty<int>());
        }
        var realUser = await _db.Users.FirstOrDefaultAsync(u => u.Id == realAppUserId, cancellationToken);
        if (realUser is null || realUser.IsPlaceholder)
        {
            return new ClaimResult(false, "The claiming account is not valid.", Array.Empty<int>());
        }

        var query = _db.AppUserLinkshells.Where(m => m.AppUserId == placeholderAppUserId);
        if (linkshellId.HasValue) { query = query.Where(m => m.LinkshellId == linkshellId.Value); }
        var placeholderMemberships = await query.ToListAsync(cancellationToken);
        if (placeholderMemberships.Count == 0)
        {
            return new ClaimResult(false, "Nothing to claim.", Array.Empty<int>());
        }

        var claimedLs = new List<int>();
        var mergedLs = new List<int>();

        await using var tx = await _db.Database.BeginTransactionAsync(cancellationToken);

        foreach (var ph in placeholderMemberships)
        {
            var existing = await _db.AppUserLinkshells
                .FirstOrDefaultAsync(m => m.AppUserId == realAppUserId && m.LinkshellId == ph.LinkshellId, cancellationToken);

            if (existing is null)
            {
                // Repoint the placeholder membership to the real user (DKP/seeds intact).
                ph.AppUserId = realAppUserId;
                ph.DiscordUserId = null; // the real account now owns identity
            }
            else
            {
                // Merge: imported DKP is authoritative.
                existing.LinkshellDkp = ph.LinkshellDkp;
                existing.SeededDkpEarned = ph.SeededDkpEarned;
                existing.SeededDkpSpent = ph.SeededDkpSpent;
                _db.AppUserLinkshells.Remove(ph);
                mergedLs.Add(ph.LinkshellId);
            }
            claimedLs.Add(ph.LinkshellId);
        }

        // Repoint the placeholder's ledger rows (for the claimed linkshells) so the
        // DKP history / audit trail follows the real account.
        var ledger = await _db.DkpLedgerEntries
            .Where(e => e.AppUserId == placeholderAppUserId && claimedLs.Contains(e.LinkshellId))
            .ToListAsync(cancellationToken);
        foreach (var e in ledger) { e.AppUserId = realAppUserId; }

        await _db.SaveChangesAsync(cancellationToken);

        // For merged linkshells, advance the seed watermark to the real account's
        // current max ledger Id so pre-claim ledger (their stock earns + the
        // repointed import reconciliation row) sits below it and doesn't add on top
        // of the imported lifetime seed. Post-claim activity accrues normally.
        foreach (var lsId in mergedLs)
        {
            var maxId = await _db.DkpLedgerEntries
                .Where(e => e.AppUserId == realAppUserId && e.LinkshellId == lsId)
                .MaxAsync(e => (int?)e.Id, cancellationToken) ?? 0;
            var existing = await _db.AppUserLinkshells
                .FirstAsync(m => m.AppUserId == realAppUserId && m.LinkshellId == lsId, cancellationToken);
            existing.DkpSeedLedgerId = maxId;
            // Same reasoning for the POOL epoch: the merge restated LinkshellDkp from the imported
            // placeholder wholesale, so the pre-claim ledger (both accounts' rows, now all pointing
            // at the real one) no longer explains that balance. Excluding it from pool attribution
            // parks the imported DKP in the default pool — correct, since imported DKP has no
            // event-type provenance — instead of double-counting the old rows into their pools.
            existing.DkpPoolLedgerFromId = maxId;
        }

        // Carry over identity fields the real account is missing (never clobber).
        if (string.IsNullOrWhiteSpace(realUser.CharacterName) && !string.IsNullOrWhiteSpace(placeholder.CharacterName))
        {
            realUser.CharacterName = placeholder.CharacterName;
        }
        if (string.IsNullOrWhiteSpace(realUser.AltCharacterName1) && !string.IsNullOrWhiteSpace(placeholder.AltCharacterName1))
        {
            realUser.AltCharacterName1 = placeholder.AltCharacterName1;
        }
        if (string.IsNullOrWhiteSpace(realUser.AltCharacterName2) && !string.IsNullOrWhiteSpace(placeholder.AltCharacterName2))
        {
            realUser.AltCharacterName2 = placeholder.AltCharacterName2;
        }

        await _db.SaveChangesAsync(cancellationToken);

        // Delete the placeholder AppUser only when nothing references it anymore
        // (a scoped, single-linkshell claim may leave other memberships behind).
        var stillReferenced =
            await _db.AppUserLinkshells.AnyAsync(m => m.AppUserId == placeholderAppUserId, cancellationToken)
            || await _db.DkpLedgerEntries.AnyAsync(e => e.AppUserId == placeholderAppUserId, cancellationToken);
        if (!stillReferenced)
        {
            _db.Users.Remove(placeholder);
            await _db.SaveChangesAsync(cancellationToken);
        }

        await tx.CommitAsync(cancellationToken);
        return new ClaimResult(true, null, claimedLs);
    }
}
