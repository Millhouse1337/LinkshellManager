using LinkshellManagerDiscordApp.Models;
using LinkshellManagerDiscordApp.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace LinkshellManagerDiscordApp.Controllers;

// The treasury API.
//
// Reads are open to every member: a linkshell treasury that only officers can see is how arguments
// start, and member-visible history is the compensating control for there being no second signature on
// a recording. Writes need CanManageTreasury.
//
// That permission is now the ONLY gate, on both surfaces. The web pages used to check the coarse
// Leader/Officer rank instead, so an officer without the permission could record gil through the
// website that this API would refuse — a privilege escalation available by picking a front-end.
// GrantTreasuryToOfficersWhoUsedIt preserves access for the officers who were actually using it.
public sealed partial class ActivityDataController
{
    private const int TreasuryPageSize = 15;

    [HttpGet("linkshells/{linkshellId:int}/treasury")]
    public async Task<IActionResult> GetTreasuryAsync(
        int linkshellId,
        [FromQuery] int page = 0,
        [FromQuery] string? search = null,
        [FromQuery] string? filter = null,
        CancellationToken cancellationToken = default)
    {
        var appUser = await ResolveAppUserAsync(cancellationToken);
        if (appUser is null)
        {
            return Unauthorized(new { error = "Sign in to see the treasury." });
        }

        var membership = await GetMembershipAsync(appUser.Id, linkshellId, cancellationToken);
        if (membership is null)
        {
            return Forbid();
        }

        var canManage = await CanAsync(membership, role => role.CanManageTreasury, cancellationToken);
        var categories = await _ledgerAccounts.EnsureAccountsAsync(linkshellId, cancellationToken);

        var query = _dbContext.JournalEntries
            .AsNoTracking()
            .Include(entry => entry.Lines)
            .Where(entry => entry.LinkshellId == linkshellId);

        // Drafts are only meaningful to whoever can act on them.
        if (!canManage)
        {
            query = query.Where(entry => entry.Status == JournalEntryStatuses.Confirmed);
        }
        query = TreasuryEntryFilters.Apply(query, filter);

        var term = search?.Trim();
        if (!string.IsNullOrWhiteSpace(term))
        {
            var pattern = $"%{term}%";
            query = query.Where(entry =>
                EF.Functions.ILike(entry.EntryNumber, pattern)
                || (entry.Memo != null && EF.Functions.ILike(entry.Memo, pattern))
                || (entry.CreatedByCharacterName != null && EF.Functions.ILike(entry.CreatedByCharacterName, pattern))
                || entry.Lines.Any(line =>
                    EF.Functions.ILike(line.AccountName, pattern)
                    || (line.CounterpartyCharacterName != null
                        && EF.Functions.ILike(line.CounterpartyCharacterName, pattern))
                    // The mule too, so "Edicius" finds what he is carrying and not only what he was
                    // paid — which is the question the who's-holding-it list makes people ask.
                    || (line.HolderCharacterName != null
                        && EF.Functions.ILike(line.HolderCharacterName, pattern))));
        }

        var total = await query.CountAsync(cancellationToken);
        var pageIndex = Math.Max(0, page);
        var entries = await query
            .OrderByDescending(entry => entry.TransactionDate)
            .ThenByDescending(entry => entry.Sequence)
            .Skip(pageIndex * TreasuryPageSize)
            .Take(TreasuryPageSize)
            .ToListAsync(cancellationToken);

        return Ok(new ActivityTreasuryPageDto(
            await BuildTreasurySnapshotAsync(linkshellId, cancellationToken),
            await MapTreasuryEntriesAsync(entries, cancellationToken),
            total,
            pageIndex,
            TreasuryPageSize,
            categories.Select(MapTreasuryCategory).ToList(),
            // ALL of them, not just the pickable ones. The client resolves the selected kind out of
            // this list and the whole form hangs off the result — the split picker, the member box,
            // the preview, and whether Submit does anything. Send only the pickable ones and a Fix
            // on an app-recorded entry does not just lose its option, it loses the form.
            TreasuryTransactionKinds.All.Select(MapTreasuryKind).ToList(),
            TreasuryTransactionActions.All
                .Select(action => new ActivityTreasuryActionDto(action.Key, action.Label))
                .ToList(),
            canManage ? await LoadTreasuryRosterAsync(linkshellId, cancellationToken) : new(),
            canManage));
    }

    [HttpPost("linkshells/{linkshellId:int}/treasury/entries")]
    public async Task<IActionResult> RecordTreasuryEntryAsync(
        int linkshellId,
        [FromBody] ActivityRecordTreasuryEntryRequest request,
        CancellationToken cancellationToken)
    {
        var gate = await AuthorizeTreasuryWriteAsync(linkshellId, cancellationToken);
        if (gate.Failure is not null)
        {
            return gate.Failure;
        }

        var invalid = ValidateTreasuryRequest(
            request.TransactionKind, request.Amount, request.CounterpartyCharacterName,
            request.HolderCharacterName);
        if (invalid is not null)
        {
            return invalid;
        }

        var shared = await ResolveTreasuryRecipientsAsync(
            linkshellId,
            TreasuryTransactionKinds.Find(request.TransactionKind)!,
            request.Amount,
            request.RecipientMembershipIds,
            cancellationToken);
        if (shared.Failure is not null)
        {
            return shared.Failure;
        }

        try
        {
            var entry = await _treasuryJournal.DraftAsync(
                linkshellId, BuildTreasuryRequest(request, shared.Recipients), gate.Actor, cancellationToken);
            if (request.Confirm)
            {
                await _treasuryJournal.ConfirmAsync(entry, gate.Actor, cancellationToken);
            }
            await _dbContext.SaveChangesAsync(cancellationToken);
            return Ok(new { success = true, id = entry.Id, entryNumber = entry.EntryNumber });
        }
        catch (TreasuryPeriodLockedException locked)
        {
            return LockedResponse(locked);
        }
    }

    [HttpPost("treasury/entries/{entryId:int}/confirm")]
    public async Task<IActionResult> ConfirmTreasuryEntryAsync(int entryId, CancellationToken cancellationToken)
    {
        var found = await LoadTreasuryEntryForWriteAsync(entryId, cancellationToken);
        if (found.Failure is not null)
        {
            return found.Failure;
        }

        try
        {
            await _treasuryJournal.ConfirmAsync(found.Entry!, found.Actor, cancellationToken);
            await _dbContext.SaveChangesAsync(cancellationToken);
            return Ok(new { success = true });
        }
        catch (TreasuryPeriodLockedException locked)
        {
            return LockedResponse(locked);
        }
    }

    // Drafts only. A confirmed entry is fixed or reversed, never edited.
    [HttpPost("treasury/entries/{entryId:int}/update")]
    public async Task<IActionResult> UpdateTreasuryDraftAsync(
        int entryId,
        [FromBody] ActivityRecordTreasuryEntryRequest request,
        CancellationToken cancellationToken)
    {
        var found = await LoadTreasuryEntryForWriteAsync(entryId, cancellationToken);
        if (found.Failure is not null)
        {
            return found.Failure;
        }

        // Retired kinds are allowed HERE and nowhere else: correcting an entry must be able to
        // reproduce the movement it is correcting.
        var invalid = ValidateTreasuryRequest(
            request.TransactionKind, request.Amount, request.CounterpartyCharacterName,
            request.HolderCharacterName, allowRetired: true);
        if (invalid is not null)
        {
            return invalid;
        }

        var shared = await ResolveTreasuryRecipientsAsync(
            found.Entry!.LinkshellId,
            TreasuryTransactionKinds.Find(request.TransactionKind)!,
            request.Amount,
            request.RecipientMembershipIds,
            cancellationToken);
        if (shared.Failure is not null)
        {
            return shared.Failure;
        }

        try
        {
            await _treasuryJournal.UpdateDraftAsync(
                found.Entry!, BuildTreasuryRequest(request, shared.Recipients), cancellationToken);
            if (request.Confirm)
            {
                await _treasuryJournal.ConfirmAsync(found.Entry!, found.Actor, cancellationToken);
            }
            await _dbContext.SaveChangesAsync(cancellationToken);
            return Ok(new { success = true });
        }
        catch (ConfirmedTreasuryEntryException already)
        {
            return Conflict(new { error = already.Message });
        }
        catch (TreasuryPeriodLockedException locked)
        {
            return LockedResponse(locked);
        }
    }

    [HttpPost("treasury/entries/{entryId:int}/discard")]
    public async Task<IActionResult> DiscardTreasuryDraftAsync(int entryId, CancellationToken cancellationToken)
    {
        var found = await LoadTreasuryEntryForWriteAsync(entryId, cancellationToken);
        if (found.Failure is not null)
        {
            return found.Failure;
        }

        try
        {
            _treasuryJournal.DiscardDraft(found.Entry!);
            await _dbContext.SaveChangesAsync(cancellationToken);
            return Ok(new { success = true });
        }
        catch (ConfirmedTreasuryEntryException already)
        {
            return Conflict(new { error = already.Message });
        }
    }

    // Cancel a confirmed entry by recording its opposite. The original stays in the list.
    [HttpPost("treasury/entries/{entryId:int}/reverse")]
    public async Task<IActionResult> ReverseTreasuryEntryAsync(
        int entryId,
        [FromBody] ActivityReverseTreasuryEntryRequest request,
        CancellationToken cancellationToken)
    {
        var found = await LoadTreasuryEntryForWriteAsync(entryId, cancellationToken);
        if (found.Failure is not null)
        {
            return found.Failure;
        }
        if (string.IsNullOrWhiteSpace(request.Reason))
        {
            return BadRequest(new { error = "Say why this entry is being reversed." });
        }

        try
        {
            await _treasuryJournal.ReverseAsync(found.Entry!, request.Reason, found.Actor, cancellationToken);
            await _dbContext.SaveChangesAsync(cancellationToken);
            return Ok(new { success = true });
        }
        catch (ConfirmedTreasuryEntryException notConfirmed)
        {
            return Conflict(new { error = notConfirmed.Message });
        }
        catch (TreasuryPeriodLockedException locked)
        {
            return LockedResponse(locked);
        }
    }

    // "Fix": one reversal plus one replacement, recorded together, so correcting a typo is a single
    // action rather than two.
    [HttpPost("treasury/entries/{entryId:int}/fix")]
    public async Task<IActionResult> FixTreasuryEntryAsync(
        int entryId,
        [FromBody] ActivityFixTreasuryEntryRequest request,
        CancellationToken cancellationToken)
    {
        var found = await LoadTreasuryEntryForWriteAsync(entryId, cancellationToken);
        if (found.Failure is not null)
        {
            return found.Failure;
        }
        if (string.IsNullOrWhiteSpace(request.Reason))
        {
            return BadRequest(new { error = "Say what was wrong with the original entry." });
        }

        var invalid = ValidateTreasuryRequest(
            request.TransactionKind, request.Amount, request.CounterpartyCharacterName,
            request.HolderCharacterName);
        if (invalid is not null)
        {
            return invalid;
        }

        // The replacement is built from this request, not from the original's lines, so a split has to
        // be re-stated in full. Without this, fixing a ten-way payout would reverse the whole amount
        // and re-record it to nobody.
        var shared = await ResolveTreasuryRecipientsAsync(
            found.Entry!.LinkshellId,
            TreasuryTransactionKinds.Find(request.TransactionKind)!,
            request.Amount,
            request.RecipientMembershipIds,
            cancellationToken);
        if (shared.Failure is not null)
        {
            return shared.Failure;
        }

        // Named arguments throughout: the request grew a holder pair in the middle, and a positional
        // call silently re-aims every argument after it at the wrong parameter.
        var corrected = new TreasuryEntryRequest(
            request.TransactionKind,
            request.Amount,
            (request.TransactionDate ?? found.Entry!.TransactionDate).ToUniversalTime(),
            Memo: request.Memo,
            CounterpartyAppUserId: shared.Recipients.Count > 0 ? null : request.CounterpartyAppUserId,
            CounterpartyCharacterName: shared.Recipients.Count > 0 ? null : request.CounterpartyCharacterName,
            HolderAppUserId: request.HolderAppUserId,
            HolderCharacterName: request.HolderCharacterName,
            Source: found.Entry!.Source,
            SourceItemId: found.Entry.SourceItemId,
            SourceAuctionItemId: found.Entry.SourceAuctionItemId,
            SourceAuctionHistoryId: found.Entry.SourceAuctionHistoryId,
            Recipients: shared.Recipients.Count > 0 ? shared.Recipients : null);

        try
        {
            var (_, replacement) = await _treasuryJournal.CorrectAsync(
                found.Entry, corrected, request.Reason, found.Actor, cancellationToken);
            await _dbContext.SaveChangesAsync(cancellationToken);
            return Ok(new { success = true, id = replacement.Id, entryNumber = replacement.EntryNumber });
        }
        catch (ConfirmedTreasuryEntryException notConfirmed)
        {
            return Conflict(new { error = notConfirmed.Message });
        }
        catch (TreasuryPeriodLockedException locked)
        {
            return LockedResponse(locked);
        }
    }

    // Pay the ticked members straight off the balance sheet's "who we owe" list, in full.
    //
    // One ordinary "We paid a member what we owed" transaction each, in a single save, so a payout
    // run lands whole or not at all. Same service the website's SettleOwed uses, so the two surfaces
    // cannot pay differently.
    [HttpPost("linkshells/{linkshellId:int}/treasury/settle")]
    public Task<IActionResult> SettleOwedAsync(
        int linkshellId,
        [FromBody] ActivitySettleOwedRequest request,
        CancellationToken cancellationToken) =>
        SettleAsync(
            linkshellId, request, TreasurySettlementDirection.WePaidThem,
            "Tick who was paid first.", cancellationToken);

    // The mirror on the other half of the sheet: tick whoever has now paid the LINKSHELL. Same
    // service, same rules, opposite direction — clearing an owed-to-us debt has no other route, and
    // the Record form deliberately offers none.
    [HttpPost("linkshells/{linkshellId:int}/treasury/settle-owed-to-us")]
    public Task<IActionResult> SettleOwedToUsAsync(
        int linkshellId,
        [FromBody] ActivitySettleOwedRequest request,
        CancellationToken cancellationToken) =>
        SettleAsync(
            linkshellId, request, TreasurySettlementDirection.TheyPaidUs,
            "Tick who paid first.", cancellationToken);

    private async Task<IActionResult> SettleAsync(
        int linkshellId,
        ActivitySettleOwedRequest request,
        TreasurySettlementDirection direction,
        string nothingTickedMessage,
        CancellationToken cancellationToken)
    {
        var gate = await AuthorizeTreasuryWriteAsync(linkshellId, cancellationToken);
        if (gate.Failure is not null)
        {
            return gate.Failure;
        }

        var picks = (request.Picks ?? Array.Empty<ActivityTreasurySettlePickDto>())
            .Where(pick => !string.IsNullOrWhiteSpace(pick.CharacterName))
            .Select(pick => new TreasurySettlementPick(pick.CharacterName, pick.ExpectedAmount))
            .ToList();
        if (picks.Count == 0)
        {
            return BadRequest(new { error = nothingTickedMessage });
        }
        // Ticking a name here moves real gil, so it answers the same question the record form asks.
        // Without it a payout run would quietly file the whole batch under "nobody named".
        if (string.IsNullOrWhiteSpace(request.HolderCharacterName))
        {
            return BadRequest(new
            {
                error = direction == TreasurySettlementDirection.TheyPaidUs
                    ? "Say who is holding this gil."
                    : "Say whose gil this is coming out of.",
            });
        }

        try
        {
            var result = await _treasurySettlements.SettleAsync(
                linkshellId, picks, direction,
                new TreasuryHolder(request.HolderAppUserId, request.HolderCharacterName.Trim()),
                gate.Actor, cancellationToken);
            await _dbContext.SaveChangesAsync(cancellationToken);

            // 200 even when every tick was skipped: nothing failed, the books had simply moved on.
            // The panel reads Success to decide whether that is a confirmation or a warning.
            return Ok(new ActivitySettleOwedResultDto(
                !result.DidNothing,
                result.Message,
                result.TotalPaid,
                result.Settled.Select(line => line.CharacterName).ToList(),
                result.Skipped.Select(skip => skip.CharacterName).ToList()));
        }
        catch (TreasuryPeriodLockedException locked)
        {
            return LockedResponse(locked);
        }
    }

    // Locking is a leader's call, deliberately a tier above recording: it is the one action here that
    // stops other officers from fixing their own mistakes.
    [HttpPost("linkshells/{linkshellId:int}/treasury/lock")]
    public async Task<IActionResult> LockTreasuryAsync(
        int linkshellId,
        [FromBody] ActivityLockTreasuryRequest request,
        CancellationToken cancellationToken)
    {
        var appUser = await ResolveAppUserAsync(cancellationToken);
        if (appUser is null)
        {
            return Unauthorized(new { error = "Sign in to manage the treasury." });
        }
        var membership = await GetMembershipAsync(appUser.Id, linkshellId, cancellationToken);
        if (membership is null ||
            !(LinkshellRanks.IsLeader(membership.Rank) ||
              await _adminOverride.IsActiveForAsync(appUser, cancellationToken)))
        {
            return Forbid();
        }

        var period = await _dbContext.LedgerPeriods
            .FirstOrDefaultAsync(item => item.LinkshellId == linkshellId, cancellationToken);
        if (period is null)
        {
            period = new LedgerPeriod { LinkshellId = linkshellId };
            _dbContext.LedgerPeriods.Add(period);
        }

        var now = DateTime.UtcNow;
        var characterName = membership.CharacterName ?? appUser.CharacterName;
        if (request.LockedThrough.HasValue)
        {
            period.LockedThroughUtc = request.LockedThrough.Value.ToUniversalTime();
            period.LockedAt = now;
            period.LockedByAppUserId = appUser.Id;
            period.LockedByCharacterName = characterName;
        }
        else
        {
            // Unlocking keeps the row rather than deleting it, so the fact it was ever locked survives.
            period.LockedThroughUtc = null;
            period.UnlockedAt = now;
            period.UnlockedByAppUserId = appUser.Id;
            period.UnlockedByCharacterName = characterName;
            period.UnlockReason = request.Reason;
        }

        await _dbContext.SaveChangesAsync(cancellationToken);
        _ledgerPeriods.Forget(linkshellId);
        return Ok(new { success = true, lockedThroughUtc = period.LockedThroughUtc });
    }

    // ---- helpers ----------------------------------------------------------------

    private async Task<ActivityTreasurySnapshotDto> BuildTreasurySnapshotAsync(
        int linkshellId, CancellationToken cancellationToken)
    {
        var sheet = await _treasury.GetBalanceSheetAsync(linkshellId, null, null, cancellationToken);
        var snapshot = sheet.Snapshot;
        return new ActivityTreasurySnapshotDto(
            snapshot.CashOnHand,
            snapshot.OwedToUs,
            snapshot.WeOwe,
            snapshot.MoneyIn,
            snapshot.MoneyOut,
            snapshot.NetChange,
            snapshot.NetWorth,
            snapshot.StartingBalance,
            snapshot.Balances,
            await _ledgerPeriods.GetLockedThroughAsync(linkshellId, cancellationToken),
            TreasuryLabels.BasisNote,
            // The word for "nobody was named" is decided here, once, so both surfaces say it the same.
            // CanSettle comes off the obligation rather than the displayed name for the same reason:
            // a member could in principle be called whatever UnnamedMember says.
            MapObligations(sheet.OwedToMembers),
            MapObligations(sheet.OwedToUsBy),
            // Sent RAW — the name stays null where nobody was named, and the front-end labels it. The
            // obligation lists resolve their own null to a word here because CanSettle has to be
            // decided beside it; this list is never ticked, so there is nothing to decide.
            sheet.GilHolders
                .Select(holder => new ActivityTreasuryGilHolderDto(holder.CharacterName, holder.Amount))
                .ToList());
    }

    // Both halves of the sheet, mapped the same way — they are the same kind of list and the same
    // kind of tick, so a difference between them here could only be a mistake.
    private static List<ActivityTreasuryMemberObligationDto> MapObligations(
        IReadOnlyList<MemberObligation> obligations) =>
        obligations
            .Select(owed => new ActivityTreasuryMemberObligationDto(
                owed.CharacterName ?? TreasuryLabels.UnnamedMember,
                owed.Amount,
                owed.CharacterName is not null && owed.Amount > 0))
            .ToList();

    private async Task<List<ActivityTreasuryEntryDto>> MapTreasuryEntriesAsync(
        List<JournalEntry> entries, CancellationToken cancellationToken)
    {
        if (entries.Count == 0)
        {
            return new List<ActivityTreasuryEntryDto>();
        }

        var ids = entries.Select(entry => entry.Id).ToList();
        // Which of these have been cancelled out, why, and which entry each reversal points at. Both
        // read as lookups rather than stored flags, because storing them would mean updating a
        // confirmed entry. The WHY matters as much as the fact: a Correction pointing at an entry
        // means it was FIXED, a Reversal alone means it was called off, and the list says so.
        var cancels = await _dbContext.JournalEntries
            .AsNoTracking()
            .Where(entry => entry.ReversesJournalEntryId != null && ids.Contains(entry.ReversesJournalEntryId.Value))
            .Select(entry => new CancelledEntryRow(entry.ReversesJournalEntryId!.Value, entry.Kind))
            .ToListAsync(cancellationToken);

        var targetIds = entries
            .Where(entry => entry.ReversesJournalEntryId.HasValue)
            .Select(entry => entry.ReversesJournalEntryId!.Value)
            .Distinct()
            .ToList();
        var targetNumbers = targetIds.Count == 0
            ? new Dictionary<int, string>()
            : await _dbContext.JournalEntries
                .AsNoTracking()
                .Where(entry => targetIds.Contains(entry.Id))
                .ToDictionaryAsync(entry => entry.Id, entry => entry.EntryNumber, cancellationToken);

        // Members named on these entries who are still in the linkshell, so a split can be re-picked
        // when it is fixed. Anyone who has since left resolves to null and is shown as unpickable
        // rather than silently dropped.
        var linkshellIds = entries.Select(entry => entry.LinkshellId).Distinct().ToList();
        var roster = await _dbContext.AppUserLinkshells
            .AsNoTracking()
            .Where(member => linkshellIds.Contains(member.LinkshellId)
                && member.CharacterName != null
                && member.CharacterName != "")
            .Select(member => new { member.Id, member.LinkshellId, member.AppUserId, member.CharacterName })
            .ToListAsync(cancellationToken);

        var byAppUser = roster
            .Where(member => !string.IsNullOrEmpty(member.AppUserId))
            .GroupBy(member => (member.LinkshellId, member.AppUserId!))
            .ToDictionary(group => group.Key, group => group.First().Id);
        var byName = roster
            .GroupBy(member => (member.LinkshellId, Name: member.CharacterName!.ToLowerInvariant()))
            .ToDictionary(group => group.Key, group => group.First().Id);

        var reversed = cancels.Select(row => row.OriginalId).ToHashSet();
        var corrected = cancels
            .Where(row => string.Equals(row.Kind, JournalEntryKinds.Correction, StringComparison.OrdinalIgnoreCase))
            .Select(row => row.OriginalId)
            .ToHashSet();
        return entries
            .Select(entry => MapTreasuryEntry(entry, reversed, corrected, targetNumbers, byAppUser, byName))
            .ToList();
    }

    private static ActivityTreasuryEntryDto MapTreasuryEntry(
        JournalEntry entry,
        HashSet<int> reversedIds,
        HashSet<int> correctedIds,
        Dictionary<int, string> targetNumbers,
        Dictionary<(int, string), int> membershipByAppUser,
        Dictionary<(int, string), int> membershipByName)
    {
        var lines = entry.Lines.OrderBy(line => line.LineNumber).ToList();
        var cashDelta = lines
            .Where(line => line.AccountNumber == TreasuryAccounts.GilOnHand)
            .Sum(line => line.Amount);
        // Every entry's halves sum to zero, so the positive side IS the amount — however many lines
        // it is spread over. Reading one line would report a split as one member's share.
        var amount = lines.Where(line => line.Amount > 0).Sum(line => line.Amount);

        // One row per person, not per line. A member's name goes onto every half that is not gil on
        // hand, so an entry where NEITHER half is gil on hand — "someone owes us for work", or gil
        // owed to one member — names the same person twice and used to read as "Bob, Bob" and take
        // the who-got-what layout meant for splits. A real split cannot collide: its recipients are
        // distinct membership rows. First line wins, so the amount shown is the first half's.
        var recipients = lines
            .Where(line => !string.IsNullOrWhiteSpace(line.CounterpartyCharacterName))
            .GroupBy(line => line.CounterpartyCharacterName!.Trim(), StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .Select(line => new ActivityTreasuryRecipientDto(
                ResolveMembershipId(entry.LinkshellId, line, membershipByAppUser, membershipByName),
                line.CounterpartyAppUserId,
                line.CounterpartyCharacterName!,
                Math.Abs(line.Amount)))
            .ToList();

        return new ActivityTreasuryEntryDto(
            entry.Id,
            entry.LinkshellId,
            entry.EntryNumber,
            entry.Status,
            TreasuryLabels.StatusLabel(entry.Status),
            entry.Kind,
            entry.Source,
            entry.TransactionKind,
            // Converted history predates the picker, so fall back to the category it landed in.
            TreasuryTransactionKinds.LabelFor(
                entry.TransactionKind,
                lines.FirstOrDefault(line => line.AccountNumber != TreasuryAccounts.GilOnHand)?.AccountName),
            amount,
            cashDelta,
            entry.TransactionDate,
            entry.Memo,
            lines
                .Select(line => line.CounterpartyCharacterName)
                .FirstOrDefault(name => !string.IsNullOrWhiteSpace(name)),
            entry.ConfirmedByCharacterName ?? entry.CreatedByCharacterName,
            entry.ConfirmedAt,
            entry.ReversesJournalEntryId,
            entry.ReversesJournalEntryId.HasValue
                ? targetNumbers.GetValueOrDefault(entry.ReversesJournalEntryId.Value)
                : null,
            reversedIds.Contains(entry.Id),
            correctedIds.Contains(entry.Id),
            entry.CorrectionReason,
            recipients,
            lines.Select(line => new ActivityTreasuryLineDto(
                line.LineNumber,
                line.AccountNumber,
                line.AccountName,
                TreasuryLabels.ClassLabelForNumber(line.AccountNumber),
                line.PresentedAmount,
                line.CounterpartyCharacterName)).ToList(),
            // Only the gil-on-hand line can carry one, so this reads the entry's holder without
            // having to say which line it came from.
            lines
                .Select(line => line.HolderCharacterName)
                .FirstOrDefault(name => !string.IsNullOrWhiteSpace(name)));
    }

    // Account first, because a character rename should not lose the link. Name second, because an
    // unsynced member has no account to match on. Null means they are not in the linkshell any more.
    private static int? ResolveMembershipId(
        int linkshellId,
        JournalEntryLine line,
        Dictionary<(int, string), int> byAppUser,
        Dictionary<(int, string), int> byName)
    {
        if (!string.IsNullOrEmpty(line.CounterpartyAppUserId)
            && byAppUser.TryGetValue((linkshellId, line.CounterpartyAppUserId), out var byAccount))
        {
            return byAccount;
        }
        return byName.TryGetValue(
            (linkshellId, line.CounterpartyCharacterName!.ToLowerInvariant()), out var byCharacter)
            ? byCharacter
            : null;
    }

    private static ActivityTreasuryCategoryDto MapTreasuryCategory(LedgerAccount account) =>
        new(account.Id,
            account.AccountNumber,
            account.Name,
            account.Description,
            TreasuryLabels.ClassLabel(account.AccountClass),
            account.IsCash,
            account.IsPostable,
            account.IsActive,
            account.SortOrder);

    private static ActivityTreasuryKindDto MapTreasuryKind(TreasuryTransactionKind kind) =>
        new(kind.Key, kind.Label, kind.ReasonLabel, kind.Help, kind.Action, kind.ShowsMember, kind.RequiresMember,
            kind.IsSplittable, kind.SettlesMemberDebt, kind.IsPickable, kind.IsRetired,
            kind.PreviewTemplate, kind.CounterpartyLabel, kind.RequiresHolder, kind.HolderLabel,
            kind.BringsCashIn);

    // Who a split can be shared with. Unsynced characters are included: they are real members of the
    // linkshell and get real shares, they just have no account behind the name.
    private async Task<List<ActivityTreasuryMemberDto>> LoadTreasuryRosterAsync(
        int linkshellId, CancellationToken cancellationToken) =>
        await _dbContext.AppUserLinkshells
            .AsNoTracking()
            .Where(member => member.LinkshellId == linkshellId
                && member.CharacterName != null
                && member.CharacterName != "")
            .OrderBy(member => member.CharacterName)
            .Select(member => new ActivityTreasuryMemberDto(
                member.Id, member.AppUserId, member.CharacterName!, member.Rank))
            .ToListAsync(cancellationToken);

    // Membership rows to names, checked against THIS linkshell.
    //
    // This is the security boundary for a split: without it, a request could attribute gil to any
    // string, or to a member of someone else's linkshell. A count mismatch means one of the ids is not
    // ours, and the whole request is refused rather than quietly recording the rest.
    private async Task<(IActionResult? Failure, List<TreasuryRecipient> Recipients)>
        ResolveTreasuryRecipientsAsync(
            int linkshellId,
            TreasuryTransactionKind kind,
            long amount,
            IReadOnlyList<int>? membershipIds,
            CancellationToken cancellationToken)
    {
        var wanted = membershipIds?.Distinct().ToList() ?? new List<int>();

        if (!kind.IsSplittable)
        {
            return wanted.Count > 0
                ? (BadRequest(new { error = "That option only records gil for one member." }), new())
                : (null, new());
        }
        if (wanted.Count == 0)
        {
            return (BadRequest(new { error = "Pick who this is for — one member, or several to split it." }), new());
        }

        var recipients = await _dbContext.AppUserLinkshells
            .AsNoTracking()
            .Where(member => member.LinkshellId == linkshellId
                && wanted.Contains(member.Id)
                && member.CharacterName != null
                && member.CharacterName != "")
            .Select(member => new TreasuryRecipient(member.AppUserId, member.CharacterName!))
            .ToListAsync(cancellationToken);

        if (recipients.Count != wanted.Count)
        {
            return (BadRequest(new { error = "One of those members is not in this linkshell." }), new());
        }
        // Otherwise the people who sort last get nothing, which is not a split.
        if (amount < recipients.Count)
        {
            return (BadRequest(new { error = "That is not enough gil to give everyone at least 1." }), new());
        }

        return (null, recipients);
    }

    private static TreasuryEntryRequest BuildTreasuryRequest(
        ActivityRecordTreasuryEntryRequest request, IReadOnlyList<TreasuryRecipient> recipients) =>
        new(request.TransactionKind,
            request.Amount,
            (request.TransactionDate ?? DateTime.UtcNow).ToUniversalTime(),
            request.Memo,
            // A split names its members on their own lines; a lone counterparty would be a second,
            // conflicting answer to "who was this for".
            recipients.Count > 0 ? null : request.CounterpartyAppUserId,
            recipients.Count > 0 ? null : request.CounterpartyCharacterName,
            // The holder is NOT suppressed for a split, unlike the counterparty above: a split that
            // moves gil on hand moves it off one mule however many people share it, and the writer
            // puts it on the whole half for exactly that reason.
            HolderAppUserId: request.HolderAppUserId,
            HolderCharacterName: request.HolderCharacterName,
            Recipients: recipients.Count > 0 ? recipients : null);

    // allowRetired is true ONLY from Fix. A retired kind may never start a new movement, but an
    // entry already recorded under one has to be correctable without being re-filed onto two
    // different categories — which is what happens when its option is simply missing from the form.
    private IActionResult? ValidateTreasuryRequest(
        string? transactionKind,
        long amount,
        string? counterpartyCharacterName,
        string? holderCharacterName,
        bool allowRetired = false)
    {
        var kind = TreasuryTransactionKinds.Find(transactionKind);
        if (kind is null)
        {
            return BadRequest(new { error = "Pick what happened from the list." });
        }
        if (kind.IsRetired && !allowRetired)
        {
            return BadRequest(new { error = "That option is no longer available. Pick what happened from the list." });
        }
        if (amount <= 0)
        {
            return BadRequest(new { error = "Enter how much gil moved." });
        }
        // Gil owed with nobody attached cannot appear on the "who we still owe" list.
        if (kind.RequiresMember && string.IsNullOrWhiteSpace(counterpartyCharacterName))
        {
            return BadRequest(new { error = "Say which member this is for." });
        }
        // And gil that moves with no mule attached cannot appear on the who's-holding-it list. The
        // same argument as above, applied to the one figure that had no names behind it: a linkshell
        // has no bank, so gil on hand that belongs to nobody in particular cannot be found again.
        if (kind.RequiresHolder && string.IsNullOrWhiteSpace(holderCharacterName))
        {
            return BadRequest(new
            {
                error = kind.BringsCashIn
                    ? "Say who is holding this gil."
                    : "Say whose gil this is coming out of.",
            });
        }
        return null;
    }

    private IActionResult LockedResponse(TreasuryPeriodLockedException locked) =>
        Conflict(new
        {
            error = "Entries on or before the locked date cannot be changed. A leader can unlock them.",
            lockedThroughUtc = locked.LockedThroughUtc,
        });

    private async Task<(IActionResult? Failure, TreasuryActor Actor)> AuthorizeTreasuryWriteAsync(
        int linkshellId, CancellationToken cancellationToken)
    {
        var appUser = await ResolveAppUserAsync(cancellationToken);
        if (appUser is null)
        {
            return (Unauthorized(new { error = "Sign in to record treasury entries." }), default!);
        }

        var membership = await GetMembershipAsync(appUser.Id, linkshellId, cancellationToken);
        if (!await CanAsync(membership, role => role.CanManageTreasury, cancellationToken))
        {
            return (Forbid(), default!);
        }

        return (null, new TreasuryActor(appUser.Id, membership!.CharacterName ?? appUser.CharacterName));
    }

    private async Task<(IActionResult? Failure, JournalEntry? Entry, TreasuryActor Actor)>
        LoadTreasuryEntryForWriteAsync(int entryId, CancellationToken cancellationToken)
    {
        var entry = await _dbContext.JournalEntries
            .Include(item => item.Lines)
            .FirstOrDefaultAsync(item => item.Id == entryId, cancellationToken);
        if (entry is null)
        {
            return (NotFound(new { error = "That entry was not found." }), null, default!);
        }

        var gate = await AuthorizeTreasuryWriteAsync(entry.LinkshellId, cancellationToken);
        return gate.Failure is not null ? (gate.Failure, null, default!) : (null, entry, gate.Actor);
    }
}
