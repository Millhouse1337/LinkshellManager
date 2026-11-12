using LinkshellManagerDiscordApp.Data;
using LinkshellManagerDiscordApp.Models;
using Microsoft.EntityFrameworkCore;

namespace LinkshellManagerDiscordApp.Services;

// Writes reviewed snapshot/window-event DKP into the ledger when a card is
// posted, so DKP history and audits can see (and correct) each payout row.
public sealed class WindowEventDkpLedgerService
{
    private readonly ApplicationDbContext _db;
    private readonly ILogger<WindowEventDkpLedgerService> _logger;

    private readonly DkpLedgerWriter _dkpLedger;
    private readonly DkpPoolResolver _dkpPools;

    public WindowEventDkpLedgerService(
        ApplicationDbContext db,
        DkpLedgerWriter dkpLedger,
        DkpPoolResolver dkpPools,
        ILogger<WindowEventDkpLedgerService> logger)
    {
        _db = db;
        _dkpLedger = dkpLedger;
        _dkpPools = dkpPools;
        _logger = logger;
    }

    // The event type a card's DKP is filed under, which is what picks its pool.
    //
    // An HNM camp's review card carries the camp's real type (CampEventType, "HNM"), saved at End
    // Camp. Its EntryType ("Kings Camp", "Kill") is a leftover Google Sheets tag nobody can map on
    // the DKP grouping card, so filing by it sent camp DKP to the default pool while that camp's
    // loot was charged from the HNM pool. A "/lsm now" card has no camp type, so its entry type is
    // still what it is filed under.
    public static string? PoolEventTypeFor(WindowEvent windowEvent) =>
        string.IsNullOrWhiteSpace(windowEvent.CampEventType)
            ? windowEvent.EntryType
            : windowEvent.CampEventType.Trim();

    // Post: marks a reviewed card posted and pays it, as ONE step that only one request can win.
    //
    // `windowEvent` is the tracked card with the officer's DKP draft already staged on it; it is
    // saved here together with the posted mark. Returns null when another request posted the card
    // first (nothing is saved, nobody is paid); otherwise how many payout rows were written.
    //
    // Why a claim: Post used to read "not posted yet", save the posted mark, then pay. Two officers
    // pressing Post together (two tabs, or the Activity plus the website) both read "not posted"
    // and both paid. Now the posted mark is set by one UPDATE that only matches while the card is
    // still unposted. Postgres runs the two UPDATEs one after the other: the second waits for the
    // first to finish, then matches no row and stops.
    //
    // Why a transaction: the claim, the draft, the archive sync and the payout commit together.
    // If the payout fails (or the officer's request is cancelled half way), the card goes back to
    // unposted, so Post can simply be pressed again instead of leaving a posted card nobody was
    // paid for. While it runs, the claim also holds the card's row, so anything else that writes
    // the card waits for the payout to finish.
    //
    // In-memory tests have neither transactions nor ExecuteUpdate and run one request at a time,
    // so there the claim is a plain "is it still unposted?" read of the stored row.
    public async Task<int?> PostAndPayAsync(
        WindowEvent windowEvent, DateTime postedAtUtc, CancellationToken cancellationToken)
    {
        var relational = _db.Database.IsRelational();
        await using var transaction = relational
            ? await _db.Database.BeginTransactionAsync(cancellationToken)
            : null;

        var cardId = windowEvent.Id;
        if (relational)
        {
            var claimed = await _db.WindowEvents
                .Where(w => w.Id == cardId && w.PostedToSheetAt == null)
                .ExecuteUpdateAsync(
                    setters => setters.SetProperty(w => w.PostedToSheetAt, postedAtUtc),
                    cancellationToken);
            if (claimed == 0)
            {
                // Disposing the transaction without a commit rolls it back.
                return null;
            }
        }
        else
        {
            var storedPostedAt = await _db.WindowEvents.AsNoTracking()
                .Where(w => w.Id == cardId)
                .Select(w => w.PostedToSheetAt)
                .FirstOrDefaultAsync(cancellationToken);
            if (storedPostedAt.HasValue)
            {
                return null;
            }
        }

        // Saves the draft, and keeps the tracked copy in step with the claim just written.
        windowEvent.PostedToSheetAt = postedAtUtc;
        await _db.SaveChangesAsync(cancellationToken);

        // Publish = credit DKP. Every other payout skips a card that is not posted, and nobody else
        // sees this one as posted until we commit, so a lost race here should not happen; the retry
        // stays as a safety net. Inside the transaction a rejected save rolls back only to the
        // savepoint EF takes before each save, so the re-run still belongs to this Post.
        var written = await RetryIfCampCreditRaceAsync(
            _db, () => EnsurePostedWindowEventLedgerEntriesAsync(cardId, cancellationToken));

        if (transaction is not null)
        {
            await transaction.CommitAsync(cancellationToken);
        }
        return written;
    }

    // Pays a posted card, re-running the payout if it lost a race on an account (see UniqueRaceRetry).
    //
    // The payout reads "who has a row from this card already", then inserts the rest. Two payouts at
    // the same moment both read before either saved, and the one-credit-per-account index rejects
    // the later save as a whole (nobody on it is paid, no balance moves). The re-run reads again,
    // finds the other payout's rows, and pays only who is still missing -- usually nobody.
    //
    // Safe to repeat: before its ledger save the payout only syncs the camp archive to the review,
    // which lands on the same result every time. The ledger writer keeps the failed attempt's staged
    // credits in its per-request memo; that memo only feeds overdraft checks on debits, and a payout
    // writes credits only.
    internal static Task<T> RetryIfCampCreditRaceAsync<T>(
        ApplicationDbContext dbContext, Func<Task<T>> pay) =>
        UniqueRaceRetry.RunAsync(dbContext, pay, "DkpLedgerEntries");

    // "Pay newly registered": pays the people on an ALREADY-posted card who had no account on the
    // roster when it was posted and have registered since. Post pays only who is registered at that
    // moment (nothing re-pays old cards on a page load any more), so this button is how they get
    // their DKP.
    //
    // Returns null when the card does not exist or is not posted (Post pays everyone then);
    // otherwise how many accounts were paid now. A second press pays 0: the payout skips every
    // account that already has a row from this card.
    //
    // Holds the card's row lock for the whole payout, like Post's claim. Two officers pressing it
    // at once take turns and the second finds everyone paid. Without the lock both would also add
    // the new member to the camp's Past Event before the ledger index stopped the second payout,
    // and that second archive row fails its own unique index.
    public async Task<int?> PayNewlyRegisteredAsync(int windowEventId, CancellationToken cancellationToken)
    {
        var relational = _db.Database.IsRelational();
        await using var transaction = relational
            ? await _db.Database.BeginTransactionAsync(cancellationToken)
            : null;
        if (relational)
        {
            await _db.Database.ExecuteSqlInterpolatedAsync(
                $"""SELECT 1 FROM "WindowEvents" WHERE "Id" = {windowEventId} FOR UPDATE""",
                cancellationToken);
        }

        // The caller loaded the card before the lock, to check the officer may manage it. Forget
        // that copy so the payout reads the card as it is now (an Edit Posted may have just
        // committed), not as it was a moment ago.
        _db.ChangeTracker.Clear();

        var postedAt = await _db.WindowEvents.AsNoTracking()
            .Where(w => w.Id == windowEventId)
            .Select(w => w.PostedToSheetAt)
            .FirstOrDefaultAsync(cancellationToken);
        if (!postedAt.HasValue)
        {
            return null;
        }

        var written = await RetryIfCampCreditRaceAsync(
            _db, () => EnsurePostedWindowEventLedgerEntriesAsync(windowEventId, cancellationToken));

        if (transaction is not null)
        {
            await transaction.CommitAsync(cancellationToken);
        }
        return written;
    }

    // Posted cards that still owe someone, and WHO each owes: people on the card who resolve to a
    // member now but have no ledger row from it. That is someone who registered after the card was
    // posted -- exactly who PayNewlyRegisteredAsync would pay. One name per account (the member's
    // character name, else the name captured on the card), sorted. Cards owing nobody are left out.
    //
    // Names rather than a count so the officer sees who the button pays before pressing it: a
    // count alone could not show a name that should not be there.
    //
    // Read-only, and light enough for the polled attendance payload: one roster load, the names on
    // the cards (not their whole capture tree) and who is already paid, for cards posted since
    // `postedSinceUtc` only. The names go through the same BuildCombinedMembers and roster lookup
    // the payout uses, so the list and the payout cannot disagree about who is owed.
    public async Task<Dictionary<int, IReadOnlyList<string>>> FindUnpaidAccountsAsync(
        int linkshellId, DateTime postedSinceUtc, CancellationToken cancellationToken)
    {
        var owing = new Dictionary<int, IReadOnlyList<string>>();

        // The same "can this card pay at all" test the payout starts with.
        var cards = await _db.WindowEvents.AsNoTracking()
            .Where(w => w.LinkshellId == linkshellId &&
                        w.PostedToSheetAt != null &&
                        w.PostedToSheetAt >= postedSinceUtc &&
                        w.DkpAmount != null &&
                        w.EntryType != null)
            .Select(w => new { w.Id, w.EntryType })
            .ToListAsync(cancellationToken);
        var cardIds = cards
            .Where(card => WindowEventEntryTypes.IsValid(card.EntryType))
            .Select(card => card.Id)
            .ToList();
        if (cardIds.Count == 0)
        {
            return owing;
        }

        var names = await _db.AttendanceSnapshotEntries.AsNoTracking()
            .Where(entry => entry.Snapshot!.WindowEventId != null &&
                            cardIds.Contains(entry.Snapshot.WindowEventId.Value) &&
                            entry.Snapshot.SnapshotStatus == AttendanceSnapshotStatuses.Active)
            .Select(entry => new
            {
                CardId = entry.Snapshot!.WindowEventId!.Value,
                entry.SnapshotId,
                entry.Snapshot.CapturedAtUtc,
                entry.CharacterName,
                entry.AppUserId,
            })
            .ToListAsync(cancellationToken);
        if (names.Count == 0)
        {
            return owing;
        }

        // Any row from the card counts as paid, the same rule the payout's "already paid" check uses.
        var paidRows = await _db.DkpLedgerEntries.AsNoTracking()
            .Where(entry => entry.SourceWindowEventId != null &&
                            cardIds.Contains(entry.SourceWindowEventId.Value) &&
                            entry.AppUserId != null)
            .Select(entry => new { CardId = entry.SourceWindowEventId!.Value, AppUserId = entry.AppUserId! })
            .Distinct()
            .ToListAsync(cancellationToken);
        var paidByCard = paidRows
            .GroupBy(row => row.CardId)
            .ToDictionary(
                g => g.Key,
                g => g.Select(row => row.AppUserId).ToHashSet(StringComparer.OrdinalIgnoreCase));

        var roster = await LoadPayoutRosterAsync(linkshellId, tracked: false, cancellationToken);

        foreach (var card in names.GroupBy(row => row.CardId))
        {
            // Rebuilt as captures so each name is read from the same capture the payout would pick.
            var snapshots = card
                .GroupBy(row => row.SnapshotId)
                .Select(capture =>
                {
                    var snapshot = new AttendanceSnapshot
                    {
                        Id = capture.Key,
                        CapturedAtUtc = capture.First().CapturedAtUtc,
                        SnapshotStatus = AttendanceSnapshotStatuses.Active,
                    };
                    foreach (var row in capture)
                    {
                        snapshot.Entries.Add(new AttendanceSnapshotEntry
                        {
                            CharacterName = row.CharacterName,
                            AppUserId = row.AppUserId,
                        });
                    }
                    return snapshot;
                })
                .ToList();

            paidByCard.TryGetValue(card.Key, out var paid);
            var unpaid = BuildCombinedMembers(snapshots)
                .Select(item => (item.Entry, Membership: roster.Resolve(item.Entry)))
                .Where(row => row.Membership?.AppUserId is { } appUserId
                              && (paid is null || !paid.Contains(appUserId)))
                .GroupBy(row => row.Membership!.AppUserId!, StringComparer.OrdinalIgnoreCase)
                .Select(account => account.First())
                .Select(row => string.IsNullOrWhiteSpace(row.Membership!.CharacterName)
                    ? row.Entry.CharacterName.Trim()
                    : row.Membership.CharacterName.Trim())
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (unpaid.Count > 0)
            {
                owing[card.Key] = unpaid;
            }
        }

        return owing;
    }

    public async Task<int> EnsurePostedWindowEventLedgerEntriesAsync(
        int windowEventId,
        CancellationToken cancellationToken)
    {
        var windowEvent = await _db.WindowEvents
            .Include(w => w.Snapshots).ThenInclude(s => s.Entries)
            .Include(w => w.MemberDkpOverrides)
            // The camp's Past Event and everyone on it: written at End Camp, reconciled here.
            .Include(w => w.CampEventHistory).ThenInclude(h => h!.AppUserEventHistories)
            .FirstOrDefaultAsync(w => w.Id == windowEventId, cancellationToken);

        if (windowEvent is null ||
            !windowEvent.PostedToSheetAt.HasValue ||
            !windowEvent.DkpAmount.HasValue ||
            !WindowEventEntryTypes.IsValid(windowEvent.EntryType))
        {
            return 0;
        }

        var combined = BuildCombinedMembers(windowEvent.Snapshots);
        if (combined.Count == 0)
        {
            return 0;
        }

        // Who gets paid, and how much, worked out ONCE per account (see BuildCardPayoutAsync).
        var payout = await BuildCardPayoutAsync(windowEvent, combined, cancellationToken);

        // A camp-sourced review row archives an EventHistory. It is written at END CAMP now (see
        // HnmCampReviewHandoffService) rather than here, because a recycled board otherwise left
        // the camp with no past-event record at all until somebody reviewed it. What is left for
        // Post is to make that archive agree with the review: the officer may have removed
        // someone, added someone, or changed an amount.
        //
        // Deliberately BEFORE the already-credited filter below. That filter exists so a second
        // Post cannot double-credit the ledger, and it short-circuits the whole method — but an
        // edit-then-re-post that credits nobody new must still correct the archive.
        var campHistory = ResolveCampEventHistory(windowEvent);
        if (campHistory is not null)
        {
            SyncCampArchiveRoster(campHistory, windowEvent, payout);
            await _db.SaveChangesAsync(cancellationToken);
        }

        if (payout.Accounts.Count == 0)
        {
            return 0;
        }

        // An account with ANY row from this card has been paid already, so a second Post only ever
        // pays accounts that have none.
        var existingAppUserIds = await _db.DkpLedgerEntries
            .Where(entry => entry.SourceWindowEventId == windowEvent.Id && entry.AppUserId != null)
            .Select(entry => entry.AppUserId!)
            .ToListAsync(cancellationToken);
        var existingSet = existingAppUserIds.ToHashSet(StringComparer.OrdinalIgnoreCase);

        var toPay = payout.Accounts
            .Where(account => !existingSet.Contains(account.Membership.AppUserId!))
            .ToList();
        if (toPay.Count == 0)
        {
            return 0;
        }

        // The ledger's EventType decides the pool (and a remap re-derives it from that column), so
        // it is the camp's real event type -- see PoolEventTypeFor.
        var poolEventType = PoolEventTypeFor(windowEvent);
        var windowEventPool = DkpPoolRef.Derived(poolEventType);

        var written = 0;
        foreach (var account in toPay)
        {
            // ONE row per account, however many characters it was captured under. Two rows was a
            // flat card paying a member twice for being there on their main and their alt.
            await _dkpLedger.AppendAsync(
                account.Membership,
                "SnapshotEarned",
                account.Amount,
                account.Snapshot.CapturedAtUtc,
                windowEventPool,
                new DkpEntryContext(
                    CharacterName: account.Membership.CharacterName,
                    EventName: string.IsNullOrWhiteSpace(windowEvent.Name) ? "Window Event" : windowEvent.Name,
                    EventType: poolEventType,
                    EventLocation: campHistory is not null ? windowEvent.CampEventLocation : account.Entry.Zone,
                    Details: campHistory is not null
                        ? $"HNM camp DKP, reviewed and posted as Window Event #{windowEvent.Id}."
                        : $"DKP earned from posted snapshot Window Event #{windowEvent.Id}.",
                    SourceWindowEventId: windowEvent.Id,
                    EventHistory: campHistory),
                cancellationToken);
            written++;
        }

        await _db.SaveChangesAsync(cancellationToken);

        _logger.LogInformation(
            "Window Event ledger materialized: event {WindowEventId} -> {Count} DKP rows.",
            windowEvent.Id,
            written);
        return written;
    }

    // Reconciles the DKP of an ALREADY-posted window event after its amount /
    // entry type / per-member overrides were edited. EnsurePosted... only inserts
    // missing rows, so it can't correct existing ones — this walks the existing
    // SnapshotEarned ledger entries for the window event, recomputes each
    // ACCOUNT's amount (the same figure Post pays), applies the delta to LinkshellDkp,
    // and rewrites the entry amount + type. Was previously done by the (now
    // removed) AttInput sheet job; this is the DB-only equivalent.
    public async Task<int> ReconcilePostedWindowEventLedgerAsync(int windowEventId, CancellationToken cancellationToken)
    {
        var windowEvent = await _db.WindowEvents
            .Include(w => w.MemberDkpOverrides)
            // The captures carry the money on a priced camp, so an edit to one of them is exactly
            // what this method exists to reconcile.
            .Include(w => w.Snapshots).ThenInclude(s => s.Entries)
            .FirstOrDefaultAsync(w => w.Id == windowEventId, cancellationToken);

        if (windowEvent is null ||
            !windowEvent.PostedToSheetAt.HasValue ||
            !windowEvent.DkpAmount.HasValue ||
            !WindowEventEntryTypes.IsValid(windowEvent.EntryType))
        {
            return 0;
        }

        // The type the rows are filed under, which picks their pool: the camp's event type, or the
        // entry type on a "/lsm now" card (PoolEventTypeFor). Rows posted before this used the
        // entry type even on a camp; an Update moves them to the camp's pool here.
        var newEntryType = PoolEventTypeFor(windowEvent)!;

        // The PAYOUT rows only. An officer's AuditAdjustment carries this card's id too, but it is
        // a correction on top of the payout, not a payout: re-pricing it to the card amount turned
        // "Bob 10, audited down to 8" into Bob 20 the next time anyone pressed Update.
        var ledgerEntries = await _db.DkpLedgerEntries
            .Where(entry => entry.SourceWindowEventId == windowEventId &&
                            entry.AppUserId != null &&
                            entry.EntryType == "SnapshotEarned")
            .ToListAsync(cancellationToken);
        if (ledgerEntries.Count == 0)
        {
            return 0;
        }

        // The same per-account figures Post paid from, so Update lands on exactly what a fresh Post
        // of the edited card would pay.
        var payout = await BuildCardPayoutAsync(
            windowEvent, BuildCombinedMembers(windowEvent.Snapshots), cancellationToken);

        // An account the captures no longer resolve to — an officer added them through the DKP
        // audit's "Add", or struck them off after posting — keeps the rule it always had: their
        // own override (under any of their names) or the card default.
        double FallbackAmount(DkpLedgerEntry entry)
        {
            if (!string.IsNullOrWhiteSpace(entry.CharacterName) &&
                payout.OverridesByName.TryGetValue(entry.CharacterName.Trim(), out var direct))
            {
                return direct;
            }

            if (payout.NamesByAppUserId.TryGetValue(entry.AppUserId!, out var names))
            {
                foreach (var name in names)
                {
                    if (payout.OverridesByName.TryGetValue(name, out var viaAlt)) return viaAlt;
                }
            }

            return payout.DefaultAmount;
        }

        // The edit may have changed the entry type, which changes which pool these rows belong to.
        var newPoolId = await _dkpPools.ResolveAsync(windowEvent.LinkshellId, newEntryType, cancellationToken);

        // A camp-sourced event also wrote an EventHistory at post time. Its per-member EventDkp
        // rows are the Event History / DKP sheet view of this payout, so an edit has to move them
        // too — otherwise history keeps reporting the pre-edit amounts forever.
        var historyIds = ledgerEntries
            .Where(entry => entry.EventHistoryId.HasValue)
            .Select(entry => entry.EventHistoryId!.Value)
            .Distinct()
            .ToList();
        var historyRows = historyIds.Count == 0
            ? new List<AppUserEventHistory>()
            : await _db.AppUserEventHistories
                .Where(row => historyIds.Contains(row.EventHistoryId))
                .ToListAsync(cancellationToken);

        // Keyed on the ACCOUNT. A card posted before one-row-per-account can still hold two rows for
        // one person (main + alt); pricing each row by its own name is how 2.5 + 4 became 4 + 4.
        // The account's whole figure goes on its oldest row and any extra row is set to 0, so the
        // account ends up with exactly the payout, never twice it.
        foreach (var group in ledgerEntries.GroupBy(entry => entry.AppUserId!, StringComparer.OrdinalIgnoreCase))
        {
            var rows = group.OrderBy(entry => entry.Id).ToList();
            var accountAmount = payout.ByAppUserId.TryGetValue(group.Key, out var account)
                ? account.Amount
                : FallbackAmount(rows[0]);
            payout.MembershipByAppUserId.TryGetValue(group.Key, out var membership);

            for (var i = 0; i < rows.Count; i++)
            {
                // Amend moves the balance by (new - old), so a no-op amount is genuinely a no-op.
                _dkpLedger.Amend(rows[i], i == 0 ? accountAmount : 0d, newDetails: null, membership);
                rows[i].EventType = newEntryType;
                _dkpLedger.Repoint(rows[i], newPoolId);
            }

            foreach (var row in historyRows.Where(row =>
                         rows.Any(entry => entry.EventHistoryId == row.EventHistoryId) &&
                         string.Equals(row.AppUserId, group.Key, StringComparison.OrdinalIgnoreCase)))
            {
                row.EventDkp = accountAmount;
            }
        }

        // An audit correction keeps its AMOUNT, but it still follows the row it corrects into the
        // card's new pool on a retype — otherwise the payout and its correction end up in two
        // different wallets. A pinned correction stays put, same rule as Repoint everywhere.
        //
        // Every non-payout row carrying this card's id, not only the ones pointing straight at a
        // payout row: the audit screen also lets an officer correct a correction, and that row
        // points at the first audit row. It is still this card's money and has to move with it.
        var auditRows = await _db.DkpLedgerEntries
            .Where(entry => entry.SourceWindowEventId == windowEventId &&
                            entry.EntryType != "SnapshotEarned")
            .ToListAsync(cancellationToken);
        foreach (var audit in auditRows.Where(audit => !audit.DkpPoolPinned))
        {
            audit.EventType = newEntryType;
            _dkpLedger.Repoint(audit, newPoolId);
        }

        await _db.SaveChangesAsync(cancellationToken);
        _logger.LogInformation(
            "Window Event ledger reconciled after edit: event {WindowEventId} -> {Count} entries.",
            windowEventId, ledgerEntries.Count);
        return ledgerEntries.Count;
    }

    // Linkshell-wide sweep over every posted camp. Nothing calls it on a page load any more (the
    // DKP pages used to, which let a reload race an officer's Post and pay a camp twice). Kept for
    // a deliberate, one-off backfill. Post (PostAndPayAsync) and Pay newly registered
    // (PayNewlyRegisteredAsync) pay one card at a time through EnsurePostedWindowEventLedgerEntriesAsync.
    public async Task<int> EnsurePostedWindowEventLedgerEntriesForLinkshellAsync(int linkshellId, CancellationToken cancellationToken)
    {
        var windowEventIds = await _db.WindowEvents
            .Where(w => w.LinkshellId == linkshellId &&
                        w.PostedToSheetAt.HasValue &&
                        w.DkpAmount.HasValue &&
                        w.EntryType != null)
            .Select(w => w.Id)
            .ToListAsync(cancellationToken);

        var created = 0;
        foreach (var windowEventId in windowEventIds)
        {
            created += await EnsurePostedWindowEventLedgerEntriesAsync(windowEventId, cancellationToken);
        }
        return created;
    }

    // The Past Event this camp was archived as, or null for an ordinary addon snapshot row (those
    // aren't camps and get no history, same as before).
    //
    // Normally it already exists: End Camp writes it (HnmCampReviewHandoffService) so a recycled
    // board still leaves a past-event record behind while its payout waits for review. The build
    // below is the BACKFILL for review rows staged before that was true — without it, every camp
    // sitting unposted at deploy time would post with no archive at all.
    //
    // It is ONLY that. A posted card whose archive an officer deleted must not come back through
    // here, and cannot: EventHistoryEditService.DeleteEventAsync deletes the posted card together
    // with the Past Event, so the sweep never sees it again. (It did once — seven deleted test
    // camps were rebuilt, re-paid, and re-announced to Discord the next time the DKP page loaded.)
    // An unposted card survives the delete and files the camp afresh when Posted, on purpose.
    //
    // Everything comes off the WindowEvent's Camp* columns rather than SourceEvent: the camp row
    // is RECYCLED for the next pop, so by post time its StartTime points at a future repop and
    // CommencementStartTime is null. SourceEventId can also be null already if the camp was
    // deleted — the review row still has to be postable.
    private EventHistory? ResolveCampEventHistory(WindowEvent windowEvent)
    {
        if (windowEvent.CampEventHistory is not null) return windowEvent.CampEventHistory;

        if (windowEvent.SourceEventId is null && windowEvent.CampEndedAtUtc is null)
        {
            return null;
        }

        var history = new EventHistory
        {
            LinkshellId = windowEvent.LinkshellId,
            EventName = windowEvent.Name,
            EventType = windowEvent.CampEventType,
            EventLocation = windowEvent.CampEventLocation,
            StartTime = windowEvent.CampStartedAtUtc,
            EndTime = windowEvent.CampEndedAtUtc,
            CommencementStartTime = windowEvent.CampStartedAtUtc,
            Duration = null,
            CountsTowardActive = true,
            TimeStamp = windowEvent.PostedToSheetAt ?? windowEvent.CampEndedAtUtc,
            AppUserEventHistories = new List<AppUserEventHistory>(),
        };
        _db.EventHistories.Add(history);
        // Linked so a LATER re-post reconciles this same row instead of archiving the camp twice.
        windowEvent.CampEventHistory = history;
        return history;
    }

    // Makes the camp's Past Event roster say exactly what the review says.
    //
    // End Camp stages the archive from the camp's OWN proposal, which is the right thing to show
    // while the payout is pending. Review then edits it: someone gets removed, someone gets added,
    // an amount changes. Without this the archive would keep quoting the proposal forever — a
    // past event crediting a member the officer had struck off.
    //
    // Keyed on ACCOUNT where there is one, because AppUserEventHistory is uniquely indexed on
    // (EventHistoryId, AppUserId): matching by character name would insert a second row for a
    // member who was scanned on their main at End Camp and on an alt afterwards, and the save
    // would throw. Account-less rows (an unsynced placeholder) fall back to the name, which is all
    // they have.
    //
    // An account's row carries the account's WHOLE payout — the same figure as its one ledger row.
    // It used to keep the larger of the per-name amounts, so a member scanned on an alt in window 1
    // and on their main later was paid the sum but Past Events showed only part of it.
    private static void SyncCampArchiveRoster(EventHistory history, WindowEvent windowEvent, CardPayout payout)
    {
        var byAppUserId = new Dictionary<string, AppUserEventHistory>(StringComparer.OrdinalIgnoreCase);
        var byCharacterName = new Dictionary<string, AppUserEventHistory>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in history.AppUserEventHistories)
        {
            if (!string.IsNullOrWhiteSpace(row.AppUserId)) byAppUserId[row.AppUserId] = row;
            else if (!string.IsNullOrWhiteSpace(row.CharacterName)) byCharacterName[row.CharacterName.Trim()] = row;
        }

        var kept = new HashSet<AppUserEventHistory>();
        foreach (var item in payout.Combined)
        {
            var characterName = item.Entry.CharacterName.Trim();
            if (characterName.Length == 0) continue;

            var appUserId = payout.Resolve(item.Entry)?.AppUserId;
            AccountPayout? account = null;
            if (!string.IsNullOrWhiteSpace(appUserId) && payout.ByAppUserId.TryGetValue(appUserId, out account))
            {
                // One row per account, written from the capture the ledger row was written from;
                // the account's other names are already inside account.Amount.
                if (!ReferenceEquals(item.Entry, account.Entry)) continue;
            }
            var amount = account?.Amount ?? payout.AmountForCharacter(characterName);

            AppUserEventHistory? row = null;
            if (!string.IsNullOrWhiteSpace(appUserId)) byAppUserId.TryGetValue(appUserId, out row);
            if (row is null) byCharacterName.TryGetValue(characterName, out row);

            if (row is null)
            {
                row = new AppUserEventHistory
                {
                    CharacterName = characterName,
                    Duration = null,
                    ActiveCredit = true,
                };
                history.AppUserEventHistories.Add(row);
                if (!string.IsNullOrWhiteSpace(appUserId)) byAppUserId[appUserId] = row;
                else byCharacterName[characterName] = row;
            }
            // A row already claimed by an earlier entry stays claimed (defensive: accounts write
            // once above, and the combined roster holds each name once).
            else if (kept.Contains(row))
            {
                continue;
            }

            row.AppUserId = appUserId ?? row.AppUserId;
            row.CharacterName = characterName;
            row.JobName = item.Entry.MainJob ?? row.JobName;
            row.SubJobName = item.Entry.SubJob ?? row.SubJobName;
            row.EventDkp = amount;
            kept.Add(row);
        }

        // Anyone the review struck off. Their attendance was the camp's proposal, and the officer
        // said no -- so the archive must not keep crediting them.
        foreach (var row in history.AppUserEventHistories.Where(r => !kept.Contains(r)).ToList())
        {
            history.AppUserEventHistories.Remove(row);
        }
    }

    // What one account is owed from a card, and the capture its row is written from.
    private sealed record AccountPayout(
        AppUserLinkshell Membership,
        double Amount,
        AttendanceSnapshot Snapshot,
        AttendanceSnapshotEntry Entry);

    // Everything Post, Update and the archive need to agree on who is paid what.
    private sealed class CardPayout
    {
        public required List<(AttendanceSnapshot Snapshot, AttendanceSnapshotEntry Entry)> Combined { get; init; }
        public required CharacterResolver<AppUserLinkshell> MembershipsByCharacterName { get; init; }
        public required Dictionary<string, AppUserLinkshell> MembershipByAppUserId { get; init; }
        // Every name each account could have been captured under (membership name, account name, alts).
        public required Dictionary<string, List<string>> NamesByAppUserId { get; init; }
        public required Dictionary<string, double> OverridesByName { get; init; }
        public required double DefaultAmount { get; init; }
        // In roster order; one entry per account.
        public required List<AccountPayout> Accounts { get; init; }
        public required Dictionary<string, AccountPayout> ByAppUserId { get; init; }

        public double AmountForCharacter(string? characterName)
            => !string.IsNullOrWhiteSpace(characterName) &&
               OverridesByName.TryGetValue(characterName.Trim(), out var v)
                ? v
                : DefaultAmount;

        // See PayoutRoster.Resolve.
        public AppUserLinkshell? Resolve(AttendanceSnapshotEntry entry)
            => PayoutRoster.Resolve(MembershipByAppUserId, MembershipsByCharacterName, entry);
    }

    // The linkshell's members, indexed the way a payout looks people up. Loaded once and shared,
    // so the "who is still unpaid" count (CountUnpaidAccountsAsync) asks exactly the question the
    // payout asks and the two can never disagree.
    private sealed class PayoutRoster
    {
        public required CharacterResolver<AppUserLinkshell> MembershipsByCharacterName { get; init; }
        public required Dictionary<string, AppUserLinkshell> MembershipByAppUserId { get; init; }
        // Every name each account could have been captured under (membership name, account name, alts).
        public required Dictionary<string, List<string>> NamesByAppUserId { get; init; }

        public AppUserLinkshell? Resolve(AttendanceSnapshotEntry entry)
            => Resolve(MembershipByAppUserId, MembershipsByCharacterName, entry);

        // Camp handoffs (HnmCampReviewHandoffService) stamp the account straight onto the entry,
        // so prefer that over the name lookup. Addon "/lsm now" captures leave AppUserId null and
        // fall through to the by-name path.
        //
        // This is what keeps a camp from silently underpaying: the roster is keyed on AppUserId,
        // but a member whose in-game character isn't one of the four indexed names would resolve
        // to nothing by name and simply never be credited — no error, no log.
        public static AppUserLinkshell? Resolve(
            Dictionary<string, AppUserLinkshell> byAppUserId,
            CharacterResolver<AppUserLinkshell> byCharacterName,
            AttendanceSnapshotEntry entry)
        {
            var membership = (!string.IsNullOrWhiteSpace(entry.AppUserId)
                    ? byAppUserId.GetValueOrDefault(entry.AppUserId!)
                    : null)
                ?? byCharacterName.Resolve(entry.CharacterName);
            return membership is not null && !string.IsNullOrWhiteSpace(membership.AppUserId) ? membership : null;
        }
    }

    // Works out each ACCOUNT's payout from a card.
    //
    // The roster is one line per CHARACTER, and a member is often captured on an alt in one
    // capture and on their main in another. Paying per character name paid a flat "/lsm now" card
    // twice to that member, and on a camp spread one person's pay over two ledger rows that Past
    // Events and Update then each priced on their own. So the characters are folded per account:
    //   - a priced camp (PerCaptureDkp) pays the SUM over the account's characters — each capture
    //     pays what that window was worth, whichever character stood in it;
    //   - a flat card pays ONCE: the larger per-name amount, the same fold the review card and
    //     End Camp use when two names land on one person.
    private async Task<CardPayout> BuildCardPayoutAsync(
        WindowEvent windowEvent,
        List<(AttendanceSnapshot Snapshot, AttendanceSnapshotEntry Entry)> combined,
        CancellationToken cancellationToken)
    {
        var roster = await LoadPayoutRosterAsync(windowEvent.LinkshellId, tracked: true, cancellationToken);

        // The per-character amounts the review settled on. A priced camp is paid from its
        // CAPTURES: the sum of what each window a character was scanned in pays, plus the bonus
        // capture that carries what no window does. The per-member override rows are not consulted
        // at all there — the handoff writes none, and the officer edits the captures.
        var overridesByName = windowEvent.PerCaptureDkp
            ? WindowEventCaptureDkp.SumByCharacter(windowEvent.Snapshots)
            : windowEvent.MemberDkpOverrides
                .Where(o => !string.IsNullOrWhiteSpace(o.CharacterName))
                .GroupBy(o => o.CharacterName.Trim(), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First().DkpAmount, StringComparer.OrdinalIgnoreCase);

        var payout = new CardPayout
        {
            Combined = combined,
            MembershipsByCharacterName = roster.MembershipsByCharacterName,
            MembershipByAppUserId = roster.MembershipByAppUserId,
            NamesByAppUserId = roster.NamesByAppUserId,
            OverridesByName = overridesByName,
            DefaultAmount = windowEvent.DkpAmount ?? 0d,
            Accounts = new List<AccountPayout>(),
            ByAppUserId = new Dictionary<string, AccountPayout>(StringComparer.OrdinalIgnoreCase),
        };

        var byAccount = combined
            .Select(item => (item.Snapshot, item.Entry, Membership: payout.Resolve(item.Entry)))
            .Where(item => item.Membership is not null)
            .GroupBy(item => item.Membership!.AppUserId!, StringComparer.OrdinalIgnoreCase);
        foreach (var group in byAccount)
        {
            var membership = group.First().Membership!;
            // The combined roster already holds each name once, so these are distinct characters.
            var amounts = group.Select(item => payout.AmountForCharacter(item.Entry.CharacterName)).ToList();
            var amount = windowEvent.PerCaptureDkp ? amounts.Sum() : amounts.Max();

            // The row is written from the membership's own character when it was captured, so the
            // ledger and Past Events name the member the roster knows.
            var representative = group
                .Where(item => string.Equals(
                    item.Entry.CharacterName.Trim(), membership.CharacterName?.Trim(), StringComparison.OrdinalIgnoreCase))
                .DefaultIfEmpty(group.First())
                .First();

            var account = new AccountPayout(
                membership, amount, representative.Snapshot, representative.Entry);
            payout.Accounts.Add(account);
            payout.ByAppUserId[group.Key] = account;
        }

        return payout;
    }

    // The linkshell's members with an account, indexed by every name they might be captured under.
    // `tracked`: a payout writes through these memberships (their DKP balance), so it needs them
    // tracked; a read-only count does not.
    private async Task<PayoutRoster> LoadPayoutRosterAsync(
        int linkshellId, bool tracked, CancellationToken cancellationToken)
    {
        var memberships = tracked ? _db.AppUserLinkshells : _db.AppUserLinkshells.AsNoTracking();
        var users = tracked ? _db.Users : _db.Users.AsNoTracking();
        var membershipsWithUser = await memberships
            .Where(link => link.LinkshellId == linkshellId && link.AppUserId != null)
            .Join(users,
                  link => link.AppUserId,
                  user => user.Id,
                  (link, user) => new { Membership = link, User = user })
            .ToListAsync(cancellationToken);

        // Every name a member might have been captured under -- the membership's own CharacterName,
        // the account's CharacterName and both alts -- resolved by the one shared rule
        // (CharacterResolver). This lets a snapshot credit a member who showed up on an alt: the
        // addon captures whatever character is standing in the alliance, which is often not the
        // one on the roster.
        var membershipsByCharacterName = CharacterResolver.ForMemberships(
            membershipsWithUser.Select(pair => (pair.Membership, pair.User)));
        var namesByAppUserId = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in membershipsWithUser)
        {
            if (string.IsNullOrWhiteSpace(pair.Membership.AppUserId)) continue;
            if (!namesByAppUserId.TryGetValue(pair.Membership.AppUserId, out var names))
            {
                names = new List<string>();
                namesByAppUserId[pair.Membership.AppUserId] = names;
            }

            foreach (var candidate in new[]
                     {
                         pair.Membership.CharacterName,
                         pair.User.CharacterName,
                         pair.User.AltCharacterName1,
                         pair.User.AltCharacterName2,
                     })
            {
                if (string.IsNullOrWhiteSpace(candidate)) continue;
                var key = candidate.Trim();
                if (!names.Contains(key, StringComparer.OrdinalIgnoreCase))
                {
                    names.Add(key);
                }
            }
        }

        var membershipByAppUserId = membershipsWithUser
            .Where(pair => !string.IsNullOrWhiteSpace(pair.Membership.AppUserId))
            .GroupBy(pair => pair.Membership.AppUserId!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().Membership, StringComparer.OrdinalIgnoreCase);

        return new PayoutRoster
        {
            MembershipsByCharacterName = membershipsByCharacterName,
            MembershipByAppUserId = membershipByAppUserId,
            NamesByAppUserId = namesByAppUserId,
        };
    }

    private static List<(AttendanceSnapshot Snapshot, AttendanceSnapshotEntry Entry)> BuildCombinedMembers(IEnumerable<AttendanceSnapshot> snapshots)
    {
        return snapshots
            .Where(s => s.SnapshotStatus == AttendanceSnapshotStatuses.Active)
            .SelectMany(s => s.Entries.Select(e => new { Snapshot = s, Entry = e }))
            .Where(x => !string.IsNullOrWhiteSpace(x.Entry.CharacterName))
            .GroupBy(x => x.Entry.CharacterName.Trim(), StringComparer.OrdinalIgnoreCase)
            .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
            .Select(g =>
            {
                var latest = g.OrderByDescending(x => x.Snapshot.CapturedAtUtc).First();
                return (latest.Snapshot, latest.Entry);
            })
            .ToList();
    }
}
