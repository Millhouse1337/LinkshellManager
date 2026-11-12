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

    // Re-run from the top when one of the memberships was saved by another request (a DKP move)
    // during the claim (Data/ConcurrencyRetry.cs). The claim's transaction rolled back, so the re-run
    // merges from the balances as they are now.
    public Task<ClaimResult> ClaimPlaceholderAsync(
        string placeholderAppUserId,
        string realAppUserId,
        int? linkshellId,
        CancellationToken cancellationToken) =>
        ConcurrencyRetry.RunAsync(_db, () => ClaimPlaceholderOnceAsync(placeholderAppUserId, realAppUserId, linkshellId, cancellationToken));

    private async Task<ClaimResult> ClaimPlaceholderOnceAsync(
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
        await FoldSharedCampPayoutsAsync(ledger, realAppUserId, cancellationToken);
        foreach (var e in ledger) { e.AppUserId = realAppUserId; }

        // Loot is charged and refunded by the winner's account (EventLootDetail.ItemWinnerAppUserId),
        // so loot the placeholder won follows the claim too -- otherwise its close or refund would
        // look for an account that is about to be gone.
        var wonLoot = await _db.EventLootDetails
            .Where(d => d.ItemWinnerAppUserId == placeholderAppUserId
                && ((d.LinkshellId != null && claimedLs.Contains(d.LinkshellId.Value))
                    || (d.Event != null && claimedLs.Contains(d.Event.LinkshellId))
                    || (d.EventHistory != null && claimedLs.Contains(d.EventHistory.LinkshellId))))
            .ToListAsync(cancellationToken);
        foreach (var d in wonLoot) { d.ItemWinnerAppUserId = realAppUserId; }

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

    // A review card pays each account ONE row (unique index IX_DkpLedgerEntries_SourceWindowEventId_
    // AppUserId). One person can still hold two rows from the same card across these two accounts:
    // captured on the main the real account owns and on an alt the DKP import put on the placeholder
    // (dual-boxing, for one). Moving the placeholder's row over as-is would give the real account two
    // and the index would refuse the whole claim.
    //
    // So each such pair becomes one row first, the way the OneCampCreditPerAccount migration folded
    // old duplicates: the older row keeps the SUM, an audit that pointed at the removed row moves to
    // the kept one, and the removed row goes. The real account's ledger total ends up exactly what a
    // plain repoint would have given it. Removes the dropped placeholder row from `placeholderLedger`
    // so the caller does not repoint it.
    private async Task FoldSharedCampPayoutsAsync(
        List<DkpLedgerEntry> placeholderLedger, string realAppUserId, CancellationToken cancellationToken)
    {
        var placeholderPayouts = placeholderLedger
            .Where(e => e.EntryType == "SnapshotEarned" && e.SourceWindowEventId.HasValue)
            .ToList();
        if (placeholderPayouts.Count == 0) return;

        var cardIds = placeholderPayouts.Select(e => e.SourceWindowEventId!.Value).Distinct().ToList();
        var realPayouts = await _db.DkpLedgerEntries
            .Where(e => e.AppUserId == realAppUserId &&
                        e.EntryType == "SnapshotEarned" &&
                        e.SourceWindowEventId.HasValue &&
                        cardIds.Contains(e.SourceWindowEventId.Value))
            .ToListAsync(cancellationToken);
        if (realPayouts.Count == 0) return;

        foreach (var real in realPayouts)
        {
            var mine = placeholderPayouts.FirstOrDefault(e => e.SourceWindowEventId == real.SourceWindowEventId);
            if (mine is null) continue;
            placeholderPayouts.Remove(mine);

            var (keep, drop) = real.Id < mine.Id ? (real, mine) : (mine, real);
            // Straight onto the row, not through DkpLedgerWriter: the money only changes rows, the
            // account's total (and so its balance) is the same as before the fold.
            keep.Amount += drop.Amount;

            var audits = await _db.DkpLedgerEntries
                .Where(e => e.AuditRelatedLedgerEntryId == drop.Id)
                .ToListAsync(cancellationToken);
            foreach (var audit in audits) audit.AuditRelatedLedgerEntryId = keep.Id;

            _db.DkpLedgerEntries.Remove(drop);
            placeholderLedger.Remove(drop);
        }

        // Saved before the repoint, so the removed row is gone before its twin takes the real
        // account's id (the index is checked statement by statement). Still inside the claim's
        // transaction, so a later failure undoes this too.
        await _db.SaveChangesAsync(cancellationToken);
    }
}
