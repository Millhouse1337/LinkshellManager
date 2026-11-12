using System.Linq.Expressions;
using System.Reflection;
using LinkshellManagerDiscordApp.Data;
using LinkshellManagerDiscordApp.Models;
using Microsoft.EntityFrameworkCore;

namespace LinkshellManagerDiscordApp.Services;

// Moves everything one account owns onto another account, so the first one can be deleted.
// Used by PlaceholderClaimService when a real player claims an imported placeholder.
//
// Why one place: the old claim moved only memberships and DKP rows, then deleted the placeholder.
// A placeholder that had been to events still had attendance rows pointing at it, so Postgres
// refused the delete and the claim failed with a 500. When the delete did go through, the person's
// window credits, ratings and chart ticks were left on a deleted account and vanished from their
// real one. Every column that holds an account id is listed ONCE below (Columns), and the same list
// both moves the rows and counts what is left, so the caller deletes the old account only when
// nothing points at it any more.
//
// When a new table stores an account id, add it to Columns. The Postgres test
// (PlaceholderClaimMergeTests) scans every text column in the database for the old id after a
// claim, so a missed column fails that test.
public sealed class UserMergeService
{
    // The two "earned" row types that exist once per account per payout: per review card
    // (SnapshotEarned) and per past event (EventEarned). See FoldEarnedRowsAsync.
    private static readonly string[] SnapshotAndEventEarned = { "SnapshotEarned", "EventEarned" };

    private readonly ApplicationDbContext _db;

    public UserMergeService(ApplicationDbContext db)
    {
        _db = db;
    }

    // Moves every row of `fromAppUserId` onto `toAppUserId` and returns how many references to
    // `fromAppUserId` are left (0 = safe to delete it).
    //
    // membershipIdMap: old membership id -> the membership id that replaces it. The chart tables
    // point at a MEMBERSHIP by id (not at the account), so when the caller folded one membership
    // into another those ids move too.
    //
    // Memberships themselves must already be dealt with by the caller where both accounts are in
    // the same linkshell: joining two DKP balances is a money decision (PlaceholderClaimService), not
    // a plain repoint. A membership of `from` in a linkshell `to` is NOT in is simply moved.
    //
    // Saves, but does not open a transaction: the caller wraps it in one, so a failure part-way
    // undoes everything.
    public async Task<int> RepointAsync(
        string fromAppUserId,
        string toAppUserId,
        IReadOnlyDictionary<int, int> membershipIdMap,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(fromAppUserId) || string.IsNullOrWhiteSpace(toAppUserId)
            || string.Equals(fromAppUserId, toAppUserId, StringComparison.Ordinal))
        {
            throw new ArgumentException("Repoint needs two different account ids.");
        }

        var toLinkshellIds = await _db.AppUserLinkshells
            .Where(m => m.AppUserId == toAppUserId)
            .Select(m => m.LinkshellId)
            .ToListAsync(cancellationToken);
        if (await _db.AppUserLinkshells.AnyAsync(
                m => m.AppUserId == fromAppUserId && toLinkshellIds.Contains(m.LinkshellId), cancellationToken))
        {
            throw new InvalidOperationException(
                "Both accounts are members of the same linkshell; merge those memberships first.");
        }

        // Step 1: rows both accounts hold that may exist only once per account (a unique index, or
        // code that reads "the one row"). Moving them as they are would break that, so the pair
        // becomes one row first. Saved before step 2, so the removed row is gone before its twin
        // takes the new account id (Postgres checks a unique index row by row).
        await FoldEarnedRowsAsync(fromAppUserId, toAppUserId, cancellationToken);
        await DropDuplicateWindowCreditsAsync(fromAppUserId, toAppUserId, cancellationToken);
        await MergeEventParticipationAsync(fromAppUserId, toAppUserId, cancellationToken);
        await MergeEventHistoryRowsAsync(fromAppUserId, toAppUserId, cancellationToken);
        await DropDuplicateCaptureMembersAsync(fromAppUserId, toAppUserId, cancellationToken);
        await MergeJobRatingsAsync(fromAppUserId, toAppUserId, cancellationToken);
        await MoveChartMembershipsAsync(membershipIdMap, cancellationToken);
        await _db.SaveChangesAsync(cancellationToken);

        // Step 2: everything else is a plain "this id becomes that id".
        foreach (var column in Columns())
        {
            await column.MoveAsync(fromAppUserId, toAppUserId, cancellationToken);
        }
        await _db.SaveChangesAsync(cancellationToken);

        return await CountReferencesAsync(fromAppUserId, cancellationToken);
    }

    // How many rows, across every column that holds an account id, still point at this account.
    public async Task<int> CountReferencesAsync(string appUserId, CancellationToken cancellationToken)
    {
        var total = 0;
        foreach (var column in Columns())
        {
            total += await column.CountAsync(appUserId, cancellationToken);
        }
        return total;
    }

    // Every column that stores an account id. The first group are real foreign keys (Postgres
    // refuses to delete an account they still point at); the rest are plain columns with no FK,
    // which is why the old claim never noticed it left them behind. Actor columns ("created by",
    // "confirmed by") are listed too: a placeholder cannot sign in, so they never hold one today,
    // but a merge must leave no id behind whatever the account did.
    //
    // JournalEntries/JournalEntryLines: a confirmed treasury entry never changes its AMOUNTS
    // (INV-3). Saying a different account id for the same person is not a correction of what was
    // recorded, so moving these is allowed.
    private IEnumerable<UserColumn> Columns()
    {
        // Foreign keys.
        yield return Column(_db.AppUserLinkshells, x => x.AppUserId);
        yield return Column(_db.AppUserEvents, x => x.AppUserId);
        yield return Column(_db.AppUserEventHistories, x => x.AppUserId);
        yield return Column(_db.AppUserEventStatusLedgers, x => x.AppUserId);
        yield return Column(_db.DkpLedgerEntries, x => x.AppUserId);
        yield return Column(_db.Bids, x => x.AppUserId);
        yield return Column(_db.Invites, x => x.AppUserId);
        yield return Column(_db.Linkshells, x => x.AppUserId);
        // PHASE4B-DROP: stays while the Notifications table exists - old rows still hold a foreign
        // key to the account, so Postgres would refuse to delete a placeholder that has one.
        yield return Column(_db.Notifications, x => x.AppUserId);
        yield return Column(_db.AddonApiTokens, x => x.IssuedToAppUserId);
        yield return Column(_db.AddonPairingCodes, x => x.IssuedToAppUserId);
        yield return Column(_db.PendingTodSubmissions, x => x.SubmittedByAppUserId);
        yield return Column(_db.DiscordActivityUsers, x => x.IdentityUserId);

        // Plain columns (no foreign key).
        yield return Column(_db.AppUserEventWindows, x => x.AppUserId);
        yield return Column(_db.AttendanceSnapshotEntries, x => x.AppUserId);
        yield return Column(_db.AttendanceSnapshots, x => x.PostedByAppUserId);
        yield return Column(_db.AttendanceSnapshots, x => x.VerifiedByAppUserId);
        yield return Column(_db.EventWindowRosterSnapshots, x => x.AppUserId);
        yield return Column(_db.ClaimShieldCaptureMembers, x => x.AppUserId);
        yield return Column(_db.LinkshellPresences, x => x.AppUserId);
        yield return Column(_db.EventComments, x => x.AppUserId);
        yield return Column(_db.EventPartySlotSignups, x => x.AppUserId);
        yield return Column(_db.PartySetupSlots, x => x.SignedUpAppUserId);
        yield return Column(_db.PartySetups, x => x.CreatedByAppUserId);
        yield return Column(_db.JobRatings, x => x.TargetAppUserId);
        yield return Column(_db.JobRatings, x => x.RaterAppUserId);
        yield return Column(_db.Auctions, x => x.CreatedByUserId);
        yield return Column(_db.AuctionHistories, x => x.CreatedByUserId);
        yield return Column(_db.AuctionItems, x => x.CurrentHighestBidderAppUserId);
        yield return Column(_db.JournalEntries, x => x.CreatedByAppUserId);
        yield return Column(_db.JournalEntries, x => x.ConfirmedByAppUserId);
        yield return Column(_db.JournalEntryLines, x => x.CounterpartyAppUserId);
        yield return Column(_db.JournalEntryLines, x => x.HolderAppUserId);
        yield return Column(_db.EventLootDetails, x => x.ItemWinnerAppUserId);
        yield return Column(_db.EventLootDetails, x => x.RecordedByAppUserId);
        yield return Column(_db.EventLootDetails, x => x.EditedByAppUserId);
        yield return Column(_db.TodLootDetails, x => x.EditedByAppUserId);
        yield return Column(_db.Events, x => x.CreatorUserId);
        yield return Column(_db.Events, x => x.StarterUserId);
        yield return Column(_db.HnmRecurringBoards, x => x.CreatedByAppUserId);
        yield return Column(_db.Announcements, x => x.CreatedByAppUserId);
        yield return Column(_db.Items, x => x.CreatedByAppUserId);
        // PHASE4B-DROP: stays while the RevenueEntries table exists, so a merge leaves no old id behind.
        yield return Column(_db.RevenueEntries, x => x.CreatedByAppUserId);
        yield return Column(_db.Rules, x => x.CreatedByAppUserId);
        // PHASE4B-DROP: stays while the LedgerPeriods table exists, so a merge leaves no old id behind.
        yield return Column(_db.LedgerPeriods, x => x.LockedByAppUserId);
        yield return Column(_db.LedgerPeriods, x => x.UnlockedByAppUserId);
        // PHASE4B-DROP: stays while the ChartBossProgresses table exists, so a merge leaves no old id behind.
        yield return Column(_db.ChartBossProgresses, x => x.UpdatedByAppUserId);
        yield return Column(_db.ChartMemberKeyItems, x => x.SetByAppUserId);
        yield return Column(_db.ChartPopItems, x => x.CreatedByAppUserId);
        yield return Column(_db.ChartPopItemCredits, x => x.CreditedByAppUserId);
        yield return Column(_db.ChartWishlistRequests, x => x.RequestedByAppUserId);
        yield return Column(_db.ChartWishlistRequests, x => x.FulfilledByAppUserId);
        yield return Column(_db.PlaceholderClaimRequests, x => x.RequesterAppUserId);
        yield return Column(_db.PlaceholderClaimRequests, x => x.ResolvedByAppUserId);
        // Not PlaceholderClaimRequests.PlaceholderAppUserId: that row is the record of which
        // placeholder someone asked for, and it is kept after the placeholder is gone.
    }

    // ---- step 1: rows that may exist only once per account ---------------------------------

    // A review card pays each account ONE row (unique index IX_DkpLedgerEntries_SourceWindowEventId_
    // AppUserId), and a past event has one "earned" row per member (EventHistoryEditService reads it
    // by account). One person can hold such a row on BOTH accounts: their main on the real account
    // and an alt the DKP import put on the placeholder (dual-boxing, for one).
    //
    // Each pair becomes one row, the way the OneCampCreditPerAccount migration folded old duplicates:
    // the older row keeps the SUM, an audit that pointed at the removed row moves to the kept one,
    // and the removed row goes. The new account's ledger total is exactly what a plain move would
    // have given it, so no balance changes.
    private async Task FoldEarnedRowsAsync(string from, string to, CancellationToken cancellationToken)
    {
        var mine = await _db.DkpLedgerEntries
            .Where(e => e.AppUserId == from && SnapshotAndEventEarned.Contains(e.EntryType))
            .ToListAsync(cancellationToken);
        if (mine.Count == 0) return;

        var cardIds = mine.Where(e => e.SourceWindowEventId.HasValue).Select(e => e.SourceWindowEventId!.Value).Distinct().ToList();
        var historyIds = mine.Where(e => e.EventHistoryId.HasValue).Select(e => e.EventHistoryId!.Value).Distinct().ToList();
        var theirs = await _db.DkpLedgerEntries
            .Where(e => e.AppUserId == to && SnapshotAndEventEarned.Contains(e.EntryType)
                && ((e.SourceWindowEventId.HasValue && cardIds.Contains(e.SourceWindowEventId.Value))
                    || (e.EventHistoryId.HasValue && historyIds.Contains(e.EventHistoryId.Value))))
            .ToListAsync(cancellationToken);
        if (theirs.Count == 0) return;

        foreach (var real in theirs)
        {
            var twin = mine.FirstOrDefault(e => SameEarnedSlot(e, real));
            if (twin is null) continue;
            mine.Remove(twin);

            var (keep, drop) = real.Id < twin.Id ? (real, twin) : (twin, real);
            // Straight onto the row, not through DkpLedgerWriter: money only moves between two rows
            // that end up on the same account, so that account's total does not change.
            keep.Amount += drop.Amount;

            var audits = await _db.DkpLedgerEntries
                .Where(e => e.AuditRelatedLedgerEntryId == drop.Id)
                .ToListAsync(cancellationToken);
            foreach (var audit in audits) audit.AuditRelatedLedgerEntryId = keep.Id;

            _db.DkpLedgerEntries.Remove(drop);
        }
    }

    // The same payout slot: the same review card for a camp payout, the same past event for a
    // timed event's earned row.
    private static bool SameEarnedSlot(DkpLedgerEntry a, DkpLedgerEntry b) =>
        a.EntryType == b.EntryType && (a.EntryType == "SnapshotEarned"
            ? a.SourceWindowEventId.HasValue && a.SourceWindowEventId == b.SourceWindowEventId
            : a.EventHistoryId.HasValue && a.EventHistoryId == b.EventHistoryId);

    // One window credit per person per window (unique index). Both accounts credited for the same
    // window is one person counted twice; the new account's credit stays. DKP is not touched: it
    // was paid by ledger rows, which move separately.
    private async Task DropDuplicateWindowCreditsAsync(string from, string to, CancellationToken cancellationToken)
    {
        var windowIds = await _db.AppUserEventWindows
            .Where(w => w.AppUserId == to)
            .Select(w => w.EventAttendanceWindowId)
            .ToListAsync(cancellationToken);
        if (windowIds.Count == 0) return;
        var duplicates = await _db.AppUserEventWindows
            .Where(w => w.AppUserId == from && windowIds.Contains(w.EventAttendanceWindowId))
            .ToListAsync(cancellationToken);
        _db.AppUserEventWindows.RemoveRange(duplicates);
    }

    // One participation per account per event (unique index). When both accounts signed up for the
    // same event, the new account's row stays and takes over the old row's window credits and
    // status history (unless it already has that window); then the old row goes.
    private async Task MergeEventParticipationAsync(string from, string to, CancellationToken cancellationToken)
    {
        var mine = await _db.AppUserEvents.Where(p => p.AppUserId == from).ToListAsync(cancellationToken);
        if (mine.Count == 0) return;
        var eventIds = mine.Select(p => p.EventId).Distinct().ToList();
        var theirs = await _db.AppUserEvents
            .Where(p => p.AppUserId == to && eventIds.Contains(p.EventId))
            .ToListAsync(cancellationToken);

        foreach (var kept in theirs)
        {
            var dropped = mine.FirstOrDefault(p => p.EventId == kept.EventId);
            if (dropped is null) continue;

            var keptWindowIds = await _db.AppUserEventWindows
                .Where(w => w.AppUserEventId == kept.Id)
                .Select(w => w.EventAttendanceWindowId)
                .ToListAsync(cancellationToken);
            var windows = await _db.AppUserEventWindows
                .Where(w => w.AppUserEventId == dropped.Id)
                .ToListAsync(cancellationToken);
            foreach (var window in windows)
            {
                // A window credit removed by DropDuplicateWindowCreditsAsync is already Deleted.
                if (_db.Entry(window).State == EntityState.Deleted) continue;
                if (keptWindowIds.Contains(window.EventAttendanceWindowId))
                {
                    _db.AppUserEventWindows.Remove(window);
                    continue;
                }
                window.AppUserEventId = kept.Id;
            }

            var statusRows = await _db.AppUserEventStatusLedgers
                .Where(s => s.AppUserEventId == dropped.Id)
                .ToListAsync(cancellationToken);
            foreach (var status in statusRows) status.AppUserEventId = kept.Id;

            _db.AppUserEvents.Remove(dropped);
        }
    }

    // One past-event attendance row per account (unique index). The pair becomes the new account's
    // row carrying BOTH rows' DKP, because the event's total is the sum of its rows' EventDkp and
    // both earned-ledger rows were folded into one above.
    private async Task MergeEventHistoryRowsAsync(string from, string to, CancellationToken cancellationToken)
    {
        var mine = await _db.AppUserEventHistories.Where(h => h.AppUserId == from).ToListAsync(cancellationToken);
        if (mine.Count == 0) return;
        var historyIds = mine.Select(h => h.EventHistoryId).Distinct().ToList();
        var theirs = await _db.AppUserEventHistories
            .Where(h => h.AppUserId == to && historyIds.Contains(h.EventHistoryId))
            .ToListAsync(cancellationToken);

        foreach (var kept in theirs)
        {
            var dropped = mine.FirstOrDefault(h => h.EventHistoryId == kept.EventHistoryId);
            if (dropped is null) continue;

            if (kept.EventDkp.HasValue || dropped.EventDkp.HasValue)
            {
                kept.EventDkp = (kept.EventDkp ?? 0) + (dropped.EventDkp ?? 0);
            }
            if (dropped.WindowsAttended.HasValue && dropped.WindowsAttended > (kept.WindowsAttended ?? 0))
            {
                kept.WindowsAttended = dropped.WindowsAttended;
            }
            kept.ActiveCredit = kept.ActiveCredit || dropped.ActiveCredit;
            _db.AppUserEventHistories.Remove(dropped);
        }
    }

    // One row per person per Claim Shield capture (unique index). The new account's row stays.
    private async Task DropDuplicateCaptureMembersAsync(string from, string to, CancellationToken cancellationToken)
    {
        var captureIds = await _db.ClaimShieldCaptureMembers
            .Where(m => m.AppUserId == to)
            .Select(m => m.CaptureId)
            .ToListAsync(cancellationToken);
        if (captureIds.Count == 0) return;
        var duplicates = await _db.ClaimShieldCaptureMembers
            .Where(m => m.AppUserId == from && captureIds.Contains(m.CaptureId))
            .ToListAsync(cancellationToken);
        _db.ClaimShieldCaptureMembers.RemoveRange(duplicates);
    }

    // One rating per rater per target job (unique index over linkshell, target, rater, character
    // slot and job). After the move two rows can land on the same key: the same officer rated both
    // accounts' WAR, say. The most recently updated rating stays.
    private async Task MergeJobRatingsAsync(string from, string to, CancellationToken cancellationToken)
    {
        var rows = await _db.JobRatings
            .Where(r => r.TargetAppUserId == from || r.TargetAppUserId == to
                || r.RaterAppUserId == from || r.RaterAppUserId == to)
            .ToListAsync(cancellationToken);
        if (!rows.Any(r => r.TargetAppUserId == from || r.RaterAppUserId == from)) return;

        string After(string id) => id == from ? to : id;
        var groups = rows.GroupBy(r => (r.LinkshellId, Target: After(r.TargetAppUserId), Rater: After(r.RaterAppUserId), r.CharacterSlot, r.JobIndex));
        foreach (var group in groups)
        {
            if (group.Count() < 2) continue;
            var keep = group.OrderByDescending(r => r.UpdatedAt).ThenBy(r => r.Id).First();
            _db.JobRatings.RemoveRange(group.Where(r => r.Id != keep.Id));
        }
    }

    // The chart tables point at a membership by id, with no foreign key. Rows of a membership the
    // caller folded into another move to that one. A key item ticked on both is one tick (that
    // table allows one per member per key item), so the moving copy is dropped.
    private async Task MoveChartMembershipsAsync(IReadOnlyDictionary<int, int> membershipIdMap, CancellationToken cancellationToken)
    {
        foreach (var (oldId, keptId) in membershipIdMap)
        {
            if (oldId == keptId) continue;

            var keptTicks = await _db.ChartMemberKeyItems
                .Where(k => k.MembershipId == keptId)
                .Select(k => new { k.LinkshellId, k.Board, k.KeyItemName })
                .ToListAsync(cancellationToken);
            var ticks = await _db.ChartMemberKeyItems.Where(k => k.MembershipId == oldId).ToListAsync(cancellationToken);
            foreach (var tick in ticks)
            {
                if (keptTicks.Any(k => k.LinkshellId == tick.LinkshellId && k.Board == tick.Board && k.KeyItemName == tick.KeyItemName))
                {
                    _db.ChartMemberKeyItems.Remove(tick);
                }
                else
                {
                    tick.MembershipId = keptId;
                }
            }

            foreach (var credit in await _db.ChartPopItemCredits.Where(c => c.MembershipId == oldId).ToListAsync(cancellationToken))
            {
                credit.MembershipId = keptId;
            }
            foreach (var item in await _db.ChartPopItems.Where(p => p.HeldByMembershipId == oldId).ToListAsync(cancellationToken))
            {
                item.HeldByMembershipId = keptId;
            }
            foreach (var request in await _db.ChartWishlistRequests.Where(r => r.RequestedByMembershipId == oldId).ToListAsync(cancellationToken))
            {
                request.RequestedByMembershipId = keptId;
            }
        }
    }

    // ---- the column list plumbing --------------------------------------------------------

    private sealed record UserColumn(
        Func<string, string, CancellationToken, Task> MoveAsync,
        Func<string, CancellationToken, Task<int>> CountAsync);

    // One account-id column: loads the rows holding `from` and sets the column to `to` on each
    // (tracked, so it works on the in-memory test database too), or counts the rows holding an id.
    // Rows step 1 already removed are skipped.
    private UserColumn Column<T>(DbSet<T> set, Expression<Func<T, string?>> column) where T : class
    {
        var property = (PropertyInfo)((MemberExpression)column.Body).Member;
        return new UserColumn(
            async (from, to, cancellationToken) =>
            {
                foreach (var row in await set.Where(Holds(column, from)).ToListAsync(cancellationToken))
                {
                    if (_db.Entry(row).State == EntityState.Deleted) continue;
                    property.SetValue(row, to);
                }
            },
            (id, cancellationToken) => set.CountAsync(Holds(column, id), cancellationToken));
    }

    // row => row.Column == id, with the id sent as a query parameter.
    private static Expression<Func<T, bool>> Holds<T>(Expression<Func<T, string?>> column, string id)
    {
        Expression<Func<string>> value = () => id;
        return Expression.Lambda<Func<T, bool>>(Expression.Equal(column.Body, value.Body), column.Parameters);
    }
}
