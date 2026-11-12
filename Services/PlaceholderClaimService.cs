using LinkshellManagerDiscordApp.Data;
using LinkshellManagerDiscordApp.Models;
using Microsoft.EntityFrameworkCore;

namespace LinkshellManagerDiscordApp.Services;

// Associates an imported PLACEHOLDER member (AppUser.IsPlaceholder = true, created by the DKP import
// or the Discord sign-up form) to a REAL account. Two shapes per linkshell:
//   * the real user is NOT yet a member there  -> the placeholder's membership becomes the real
//     account's (DKP and lifetime seed ride along untouched).
//   * the real user IS already a member there  -> the two memberships become one, holding BOTH
//     balances: DKP earned on the real account before the claim is kept, and the imported DKP is
//     added on top (see MergeMembershipAsync).
// Then UserMergeService moves EVERYTHING else that pointed at the placeholder (DKP history, event
// attendance, window credits, loot, ratings, chart ticks...) and the placeholder account is
// deleted once nothing points at it. One transaction: a failure part-way changes nothing.
//
// Each claimed linkshell gets one 0-DKP "PlaceholderMerge" ledger row saying what was claimed,
// so the member's DKP history explains why their balance jumped. It moves no DKP itself: the
// placeholder's own ledger rows (the import's row included) move to the real account with its
// balance, so the ledger still adds up to the balance without it.
//
// NOTE: the Discord-id auto-claim path (DiscordIdentityService) does NOT use this service: it
// PROMOTES the placeholder in place (the AppUserId never changes), which needs no repointing.
public sealed class PlaceholderClaimService
{
    public const string MergeEntryType = "PlaceholderMerge";

    // The import's "set the balance" rows. Lifetime totals skip them (DkpSheetService).
    private static readonly HashSet<string> ReconciliationEntryTypes =
        new(StringComparer.OrdinalIgnoreCase) { "TemplateImport", "SheetImport" };

    private readonly ApplicationDbContext _db;
    private readonly UserMergeService _merge;

    public PlaceholderClaimService(ApplicationDbContext db, UserMergeService merge)
    {
        _db = db;
        _merge = merge;
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

        var placeholderMemberships = await _db.AppUserLinkshells
            .Where(m => m.AppUserId == placeholderAppUserId)
            .ToListAsync(cancellationToken);
        if (placeholderMemberships.Count == 0
            || (linkshellId.HasValue && placeholderMemberships.All(m => m.LinkshellId != linkshellId.Value)))
        {
            return new ClaimResult(false, "Nothing to claim.", Array.Empty<int>());
        }
        // The whole placeholder account moves (its history is not split by linkshell), so a claim
        // made for ONE linkshell may not carry another linkshell's roster spot with it: that
        // linkshell's officers never agreed. The import and the sign-up form make one placeholder
        // per linkshell, so this only stops hand-edited data.
        if (linkshellId.HasValue && placeholderMemberships.Any(m => m.LinkshellId != linkshellId.Value))
        {
            return new ClaimResult(false,
                "That imported member is on more than one linkshell's roster, so it can't be claimed from one of them.",
                Array.Empty<int>());
        }

        var claimedLs = new List<int>();
        // Placeholder membership id -> the real membership it was folded into (the chart tables
        // point at memberships by id).
        var membershipIdMap = new Dictionary<int, int>();
        var now = DateTime.UtcNow;

        await using var tx = await _db.Database.BeginTransactionAsync(cancellationToken);

        foreach (var ph in placeholderMemberships)
        {
            var existing = await _db.AppUserLinkshells
                .FirstOrDefaultAsync(m => m.AppUserId == realAppUserId && m.LinkshellId == ph.LinkshellId, cancellationToken);
            var phName = ph.CharacterName ?? placeholder.CharacterName ?? "an imported member";
            var phDkp = ph.LinkshellDkp ?? 0;

            if (existing is null)
            {
                // The placeholder's roster spot becomes the real account's (DKP/seeds intact).
                ph.AppUserId = realAppUserId;
                ph.DiscordUserId = null; // the real account now owns identity
                AddMergeRow(realAppUserId, ph.LinkshellId, ph.CharacterName, now,
                    $"Claimed imported member {phName}: their roster spot, {phDkp:0.##} DKP and DKP history moved to this account.");
            }
            else
            {
                await MergeMembershipAsync(existing, ph, cancellationToken);
                _db.AppUserLinkshells.Remove(ph);
                membershipIdMap[ph.Id] = existing.Id;
                AddMergeRow(realAppUserId, ph.LinkshellId, existing.CharacterName, now,
                    $"Claimed imported member {phName}: {phDkp:0.##} DKP added to this account's balance "
                    + $"(lifetime +{ph.SeededDkpEarned:0.##} earned, +{ph.SeededDkpSpent:0.##} spent). "
                    + "Their DKP history moved here too.");
            }
            claimedLs.Add(ph.LinkshellId);
        }
        await _db.SaveChangesAsync(cancellationToken);

        // Everything else: DKP history, attendance, window credits, loot, ratings, chart rows...
        var referencesLeft = await _merge.RepointAsync(placeholderAppUserId, realAppUserId, membershipIdMap, cancellationToken);

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

        // Delete the placeholder only when nothing points at it any more. Something left means a
        // table holds account ids that UserMergeService does not know about: keeping the account is
        // the safe failure (deleting it would fail on a foreign key, or orphan the rows).
        if (referencesLeft == 0)
        {
            _db.Users.Remove(placeholder);
            await _db.SaveChangesAsync(cancellationToken);
        }

        await tx.CommitAsync(cancellationToken);
        return new ClaimResult(true, null, claimedLs);
    }

    // The real account already has a membership in this linkshell: it keeps that row and ADDS the
    // placeholder's DKP. (The old merge copied the imported figures over the real ones, so DKP the
    // member earned before claiming, and loot they paid for, silently vanished.)
    //
    // The placeholder's ledger rows move to the real account in the same claim, so the ledger still
    // adds up to the summed balance: no new DKP is written.
    //
    // The two ledger watermarks keep each account's history counted exactly once:
    //   * Lifetime totals = seed + rows after DkpSeedLedgerId. The merged row starts counting at the
    //     EARLIER of the two watermarks; rows of the other account between the two were already in
    //     that account's seed, so they come off the merged seed instead of being counted twice.
    //   * Pool split = rows after DkpPoolLedgerFromId (stamped when each joined). The EARLIER value
    //     keeps typed-pool DKP (HNM, say) the earlier member earned in its pool. If the later member
    //     has rows between the two (left over from an earlier time in this linkshell, which are not
    //     part of its balance), the later value is used instead and the earlier member's rows in
    //     that gap count as default-pool DKP. Either way the TOTAL is exact; only the split differs.
    private async Task MergeMembershipAsync(AppUserLinkshell existing, AppUserLinkshell ph, CancellationToken cancellationToken)
    {
        var lsId = existing.LinkshellId;
        var realRows = await LedgerRowsAsync(existing.AppUserId!, lsId, cancellationToken);
        var phRows = await LedgerRowsAsync(ph.AppUserId!, lsId, cancellationToken);

        existing.LinkshellDkp = (existing.LinkshellDkp ?? 0) + (ph.LinkshellDkp ?? 0);

        var seedFrom = Math.Min(existing.DkpSeedLedgerId, ph.DkpSeedLedgerId);
        var seedTo = Math.Max(existing.DkpSeedLedgerId, ph.DkpSeedLedgerId);
        var laterSeedRows = existing.DkpSeedLedgerId > ph.DkpSeedLedgerId ? realRows : phRows;
        var seededGap = laterSeedRows
            .Where(r => r.Id > seedFrom && r.Id <= seedTo && !ReconciliationEntryTypes.Contains(r.EntryType ?? string.Empty))
            .ToList();
        existing.SeededDkpEarned = existing.SeededDkpEarned + ph.SeededDkpEarned
            - seededGap.Where(r => r.Amount > 0).Sum(r => r.Amount);
        existing.SeededDkpSpent = existing.SeededDkpSpent + ph.SeededDkpSpent
            - seededGap.Where(r => r.Amount < 0).Sum(r => -r.Amount);
        existing.DkpSeedLedgerId = seedFrom;

        var poolFrom = Math.Min(existing.DkpPoolLedgerFromId, ph.DkpPoolLedgerFromId);
        var poolTo = Math.Max(existing.DkpPoolLedgerFromId, ph.DkpPoolLedgerFromId);
        var laterPoolRows = existing.DkpPoolLedgerFromId > ph.DkpPoolLedgerFromId ? realRows : phRows;
        existing.DkpPoolLedgerFromId = laterPoolRows.Any(r => r.Id > poolFrom && r.Id <= poolTo) ? poolTo : poolFrom;

        // INV-1 (ApplicationDbContext) checks that a balance change is matched by ledger rows in the
        // same save. Here the matching rows are the placeholder's, which change OWNER (not amount) in
        // UserMergeService's later saves, so the check cannot see them. Marking the pool epoch as
        // written is how a deliberate balance restatement tells that check so.
        _db.Entry(existing).Property(m => m.DkpPoolLedgerFromId).IsModified = true;
    }

    private Task<List<LedgerRow>> LedgerRowsAsync(string appUserId, int linkshellId, CancellationToken cancellationToken) =>
        _db.DkpLedgerEntries
            .Where(e => e.AppUserId == appUserId && e.LinkshellId == linkshellId)
            .Select(e => new LedgerRow(e.Id, e.Amount, e.EntryType))
            .ToListAsync(cancellationToken);

    private sealed record LedgerRow(int Id, double Amount, string? EntryType);

    // The 0-DKP history line that explains a claim (see the class comment).
    private void AddMergeRow(string appUserId, int linkshellId, string? characterName, DateTime now, string details)
    {
        _db.DkpLedgerEntries.Add(new DkpLedgerEntry
        {
            AppUserId = appUserId,
            LinkshellId = linkshellId,
            EntryType = MergeEntryType,
            EventName = "Imported DKP claimed",
            Amount = 0,
            Sequence = 1,
            OccurredAt = now,
            CharacterName = characterName,
            Details = details,
        });
    }
}
