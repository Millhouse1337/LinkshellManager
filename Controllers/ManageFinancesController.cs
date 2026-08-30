using LinkshellManagerDiscordApp.Data;
using LinkshellManagerDiscordApp.Models;
using LinkshellManagerDiscordApp.Services;
using LinkshellManagerDiscordApp.ViewModels;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace LinkshellManagerDiscordApp.Controllers;

// Management → Finances → Treasury, on the website.
//
// Replaces ManageRevenueController, which is now a permanent-redirect shim so existing links and
// bookmarks still land somewhere.
//
// Two behaviours changed on purpose, and both were bugs:
//
//   * The page total was Sum(Value), which ignored outflows entirely, so it disagreed with the dashboard
//     tile and with the gil solvency check for any linkshell that had ever recorded one.
//   * The gate was LinkshellRanks.IsLeaderOrOfficer — a coarse rank — while the API required the
//     granular CanManageTreasury. An officer without the permission could record gil here that the API
//     would refuse. Both surfaces now require the permission, and
//     GrantTreasuryToOfficersWhoUsedIt preserves access for the officers who were actually using it.
[Authorize]
public class ManageFinancesController : Controller
{
    private const int PageSize = 15;

    private readonly ApplicationDbContext _context;
    private readonly UserManager<AppUser> _userManager;
    private readonly AdminOverrideService _adminOverride;
    private readonly TimeZoneConversionService _timeZones;
    private readonly TreasuryBalanceService _treasury;
    private readonly TreasuryJournalWriter _journal;
    private readonly TreasurySettlementService _settlements;
    private readonly LedgerAccountProvisioner _accounts;
    private readonly LedgerPeriodGuard _periods;

    public ManageFinancesController(
        ApplicationDbContext context,
        UserManager<AppUser> userManager,
        AdminOverrideService adminOverride,
        TimeZoneConversionService timeZones,
        TreasuryBalanceService treasury,
        TreasuryJournalWriter journal,
        TreasurySettlementService settlements,
        LedgerAccountProvisioner accounts,
        LedgerPeriodGuard periods)
    {
        _context = context;
        _userManager = userManager;
        _adminOverride = adminOverride;
        _timeZones = timeZones;
        _treasury = treasury;
        _journal = journal;
        _settlements = settlements;
        _accounts = accounts;
        _periods = periods;
    }

    // THE Treasury page: gil and items on one screen, in the order the Discord Activity's Treasury
    // tab puts them — the balance sheet, then the stash, then the transactions list. Items sit in
    // the middle on purpose: selling one is what moves the figures above it, and the transactions
    // list runs long enough to bury anything placed under it.
    //
    // `items` is the stockpile/sold toggle for that middle section. It rides along on this action
    // rather than on a page of its own, because there is no longer a page of its own.
    public async Task<IActionResult> Index(
        int page = 0, string? search = null, string? filter = null, string? items = null)
    {
        var user = await _userManager.GetUserAsync(User);
        if (user is null) return Challenge();

        var linkshellId = user.PrimaryLinkshellId;
        var model = new ManageFinancesViewModel
        {
            LinkshellName = user.PrimaryLinkshellName,
            Search = search,
            Filter = filter,
            ItemView = items,
        };
        if (!linkshellId.HasValue)
        {
            return View(model);
        }

        var cancellationToken = HttpContext.RequestAborted;
        model.LinkshellId = linkshellId.Value;
        model.CanManage = await CanManageAsync(user.Id, linkshellId.Value, cancellationToken);
        model.CanLock = await IsLeaderAsync(user.Id, linkshellId.Value);
        await _accounts.EnsureAccountsAsync(linkshellId.Value, cancellationToken);

        var sheet = await _treasury.GetBalanceSheetAsync(linkshellId.Value, null, null, cancellationToken);
        var snapshot = sheet.Snapshot;
        model.CashOnHand = snapshot.CashOnHand;
        model.MoneyIn = snapshot.MoneyIn;
        model.MoneyOut = snapshot.MoneyOut;
        model.NetChange = snapshot.NetChange;
        model.OwedToUs = snapshot.OwedToUs;
        model.WeOwe = snapshot.WeOwe;
        model.NetWorth = snapshot.NetWorth;
        // Both halves of the sheet, mapped the same way — they are the same kind of list and the
        // same kind of tick.
        model.OwedToMembers = MapObligations(sheet.OwedToMembers);
        model.OwedToUsBy = MapObligations(sheet.OwedToUsBy);
        // And the third figure's names. Projected from the same read as the two above, so all three
        // lists add up to the figures they sit under.
        model.GilHolders = MapHolders(sheet.GilHolders);
        model.HolderOptions = model.CanManage
            ? (await LoadRosterAsync(linkshellId.Value)).Select(option => option.CharacterName).ToList()
            : Array.Empty<string>();
        model.Balances = snapshot.Balances;
        model.LockedThrough = await _periods.GetLockedThroughAsync(linkshellId.Value, cancellationToken);

        var query = _context.JournalEntries
            .AsNoTracking()
            .Include(entry => entry.Lines)
            .Where(entry => entry.LinkshellId == linkshellId.Value);
        if (!model.CanManage)
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

        model.TotalEntries = await query.CountAsync(cancellationToken);
        model.PageCount = Math.Max(1, (int)Math.Ceiling(model.TotalEntries / (double)PageSize));
        model.Page = Math.Clamp(page, 0, model.PageCount - 1);

        var entries = await query
            .OrderByDescending(entry => entry.TransactionDate)
            .ThenByDescending(entry => entry.Sequence)
            .Skip(model.Page * PageSize)
            .Take(PageSize)
            .ToListAsync(cancellationToken);

        var ids = entries.Select(entry => entry.Id).ToList();
        // Which of these entries something later cancelled — and, of those, which were FIXED rather
        // than simply called off. A fix points a Correction at the original; an outright reversal
        // points a Reversal at it. Both are the same EXISTS over the same rows, so it is one query.
        var cancels = ids.Count == 0
            ? new List<CancelledEntryRow>()
            : await _context.JournalEntries
                .AsNoTracking()
                .Where(entry => entry.ReversesJournalEntryId != null && ids.Contains(entry.ReversesJournalEntryId.Value))
                .Select(entry => new CancelledEntryRow(entry.ReversesJournalEntryId!.Value, entry.Kind))
                .ToListAsync(cancellationToken);
        var reversed = cancels.Select(row => row.OriginalId).ToHashSet();
        var corrected = cancels
            .Where(row => string.Equals(row.Kind, JournalEntryKinds.Correction, StringComparison.OrdinalIgnoreCase))
            .Select(row => row.OriginalId)
            .ToHashSet();

        model.Entries = entries.Select(entry => MapRow(entry, reversed, corrected, user.TimeZone)).ToList();
        return View(model);
    }

    [HttpGet]
    public async Task<IActionResult> Record()
    {
        var user = await _userManager.GetUserAsync(User);
        if (user is null) return Challenge();

        var linkshellId = user.PrimaryLinkshellId;
        if (!linkshellId.HasValue
            || !await CanManageAsync(user.Id, linkshellId.Value, HttpContext.RequestAborted))
        {
            return Forbid();
        }

        var model = new RecordTreasuryEntryViewModel
        {
            LinkshellId = linkshellId.Value,
            LinkshellName = user.PrimaryLinkshellName,
            Options = TreasuryTransactionKinds.Pickable().ToList(),
            Roster = await LoadRosterAsync(linkshellId.Value),
            // The picker posts naive wall-clock, so default it to the viewer's local now.
            TransactionDate = _timeZones.ToUserTime(DateTime.UtcNow, user.TimeZone) ?? DateTime.UtcNow,
        };
        return View(model);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Record(RecordTreasuryEntryViewModel model)
    {
        var user = await _userManager.GetUserAsync(User);
        if (user is null) return Challenge();

        var linkshellId = user.PrimaryLinkshellId;
        if (!linkshellId.HasValue
            || !await CanManageAsync(user.Id, linkshellId.Value, HttpContext.RequestAborted))
        {
            return Forbid();
        }

        model.LinkshellId = linkshellId.Value;
        model.LinkshellName = user.PrimaryLinkshellName;
        model.Options = TreasuryTransactionKinds.Pickable().ToList();
        model.Roster = await LoadRosterAsync(linkshellId.Value);
        ValidateKind(model, allowRetired: false);

        var recipients = await ResolveRecipientsAsync(model, linkshellId.Value);
        if (!ModelState.IsValid)
        {
            return View(model);
        }

        var membership = await _context.AppUserLinkshells
            .FirstOrDefaultAsync(link => link.AppUserId == user.Id && link.LinkshellId == linkshellId.Value);
        var actor = new TreasuryActor(user.Id, membership?.CharacterName ?? user.CharacterName);

        try
        {
            var entry = await _journal.DraftAsync(
                linkshellId.Value, BuildRequest(model, user.TimeZone, recipients), actor, HttpContext.RequestAborted);
            if (model.Confirm)
            {
                await _journal.ConfirmAsync(entry, actor, HttpContext.RequestAborted);
            }
            await _context.SaveChangesAsync(HttpContext.RequestAborted);
        }
        catch (TreasuryPeriodLockedException locked)
        {
            ModelState.AddModelError(string.Empty, LockedMessage(locked, user.TimeZone));
            return View(model);
        }

        return RedirectToAction(nameof(Index));
    }

    // Editing a draft. A confirmed entry goes through Fix instead.
    [HttpGet]
    public async Task<IActionResult> EditDraft(int id)
    {
        var (failure, entry, _) = await LoadForWriteAsync(id);
        if (failure is not null) return failure;
        if (!JournalEntryStatuses.IsDraft(entry!.Status))
        {
            return RedirectToAction(nameof(Fix), new { id });
        }

        var user = await _userManager.GetUserAsync(User);
        var roster = await LoadRosterAsync(entry.LinkshellId);
        var model = new RecordTreasuryEntryViewModel
        {
            Id = entry.Id,
            LinkshellId = entry.LinkshellId,
            LinkshellName = entry.LinkshellName,
            // OtherMoneyIn, matching Fix: a row with no kind at all is converted history, and
            // "something else" is the only honest thing to say about it. Defaulting to an item sale
            // — as this did — states a fact nobody recorded.
            TransactionKind = entry.TransactionKind ?? TreasuryTransactionKinds.OtherMoneyIn,
            Amount = AmountOf(entry),
            TransactionDate = _timeZones.ToUserTime(entry.TransactionDate, user?.TimeZone) ?? entry.TransactionDate,
            Memo = entry.Memo,
            Member = MemberOf(entry),
            // Comes back so a rebuild does not silently move the gil onto nobody's mule: the
            // draft is rewritten from this form, not from its existing lines.
            Holder = HolderOf(entry),
            Confirm = false,
            Options = TreasuryTransactionKinds.PickableWith(entry.TransactionKind).ToList(),
            Roster = roster,
        };
        LoadRecipientsInto(model, entry, roster);
        return View("Record", model);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> EditDraft(int id, RecordTreasuryEntryViewModel model)
    {
        var (failure, entry, actor) = await LoadForWriteAsync(id);
        if (failure is not null) return failure;

        var user = await _userManager.GetUserAsync(User);
        model.Id = id;
        model.LinkshellId = entry!.LinkshellId;
        // PickableWith, not Pickable: on a re-render after a validation failure the draft's own kind
        // has to survive in the list, or the officer's second submit silently posts a different one
        // than their first.
        model.Options = TreasuryTransactionKinds.PickableWith(entry.TransactionKind).ToList();
        model.Roster = await LoadRosterAsync(entry.LinkshellId);
        ValidateKind(model, allowRetired: false);

        var recipients = await ResolveRecipientsAsync(model, entry.LinkshellId);
        if (!ModelState.IsValid)
        {
            return View("Record", model);
        }

        try
        {
            await _journal.UpdateDraftAsync(
                entry, BuildRequest(model, user?.TimeZone, recipients), HttpContext.RequestAborted);
            if (model.Confirm)
            {
                await _journal.ConfirmAsync(entry, actor, HttpContext.RequestAborted);
            }
            await _context.SaveChangesAsync(HttpContext.RequestAborted);
        }
        catch (ConfirmedTreasuryEntryException)
        {
            return RedirectToAction(nameof(Fix), new { id });
        }
        catch (TreasuryPeriodLockedException locked)
        {
            ModelState.AddModelError(string.Empty, LockedMessage(locked, user?.TimeZone));
            return View("Record", model);
        }

        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Confirm(int id)
    {
        var (failure, entry, actor) = await LoadForWriteAsync(id);
        if (failure is not null) return failure;

        try
        {
            await _journal.ConfirmAsync(entry!, actor, HttpContext.RequestAborted);
            await _context.SaveChangesAsync(HttpContext.RequestAborted);
        }
        catch (TreasuryPeriodLockedException locked)
        {
            TempData["TreasuryError"] = LockedMessage(locked, (await _userManager.GetUserAsync(User))?.TimeZone);
        }
        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Discard(int id)
    {
        var (failure, entry, _) = await LoadForWriteAsync(id);
        if (failure is not null) return failure;

        try
        {
            _journal.DiscardDraft(entry!);
            await _context.SaveChangesAsync(HttpContext.RequestAborted);
        }
        catch (ConfirmedTreasuryEntryException already)
        {
            TempData["TreasuryError"] = already.Message;
        }
        return RedirectToAction(nameof(Index));
    }

    // Fix: reverse the wrong entry and record a replacement, in one action.
    [HttpGet]
    public async Task<IActionResult> Fix(int id)
    {
        var (failure, entry, _) = await LoadForWriteAsync(id);
        if (failure is not null) return failure;
        if (JournalEntryStatuses.IsDraft(entry!.Status))
        {
            return RedirectToAction(nameof(EditDraft), new { id });
        }

        var user = await _userManager.GetUserAsync(User);
        var roster = await LoadRosterAsync(entry.LinkshellId);
        var model = new FixTreasuryEntryViewModel
        {
            Id = entry.Id,
            EntryNumber = entry.EntryNumber,
            LinkshellId = entry.LinkshellId,
            LinkshellName = entry.LinkshellName,
            TransactionKind = entry.TransactionKind ?? TreasuryTransactionKinds.OtherMoneyIn,
            Amount = AmountOf(entry),
            TransactionDate = _timeZones.ToUserTime(entry.TransactionDate, user?.TimeZone) ?? entry.TransactionDate,
            Memo = entry.Memo,
            Member = MemberOf(entry),
            // Same reason: a fix is built from THIS form, not from the original's lines.
            Holder = HolderOf(entry),
            // The entry's own kind is always offered, even when nothing can be recorded under it
            // any more. A fix has to be able to reproduce the movement it is correcting.
            Options = TreasuryTransactionKinds.PickableWith(entry.TransactionKind).ToList(),
            Roster = roster,
        };
        // A fix rebuilds the entry from this form, so a split has to come back in full.
        LoadRecipientsInto(model, entry, roster);
        return View(model);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Fix(int id, FixTreasuryEntryViewModel model)
    {
        var (failure, entry, actor) = await LoadForWriteAsync(id);
        if (failure is not null) return failure;

        var user = await _userManager.GetUserAsync(User);
        model.Id = id;
        model.EntryNumber = entry!.EntryNumber;
        model.LinkshellId = entry.LinkshellId;
        model.Options = TreasuryTransactionKinds.PickableWith(entry.TransactionKind).ToList();
        model.Roster = await LoadRosterAsync(entry.LinkshellId);
        // Fix is the ONE place a retired kind is allowed: refusing it here would leave every entry
        // recorded under one permanently un-correctable.
        ValidateKind(model, allowRetired: true);

        var recipients = await ResolveRecipientsAsync(model, entry.LinkshellId);
        if (!ModelState.IsValid)
        {
            return View(model);
        }

        try
        {
            await _journal.CorrectAsync(
                entry,
                BuildRequest(model, user?.TimeZone, recipients),
                model.Reason,
                actor,
                HttpContext.RequestAborted);
            await _context.SaveChangesAsync(HttpContext.RequestAborted);
        }
        catch (ConfirmedTreasuryEntryException notConfirmed)
        {
            ModelState.AddModelError(string.Empty, notConfirmed.Message);
            return View(model);
        }
        catch (TreasuryPeriodLockedException locked)
        {
            ModelState.AddModelError(string.Empty, LockedMessage(locked, user?.TimeZone));
            return View(model);
        }

        return RedirectToAction(nameof(Index));
    }

    // Reverse: cancel a confirmed entry by recording its opposite. The original stays in the list.
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Reverse(int id, string? reason)
    {
        var (failure, entry, actor) = await LoadForWriteAsync(id);
        if (failure is not null) return failure;

        try
        {
            await _journal.ReverseAsync(
                entry!,
                string.IsNullOrWhiteSpace(reason) ? "Reversed from the website." : reason,
                actor,
                HttpContext.RequestAborted);
            await _context.SaveChangesAsync(HttpContext.RequestAborted);
        }
        catch (ConfirmedTreasuryEntryException notConfirmed)
        {
            TempData["TreasuryError"] = notConfirmed.Message;
        }
        catch (TreasuryPeriodLockedException locked)
        {
            TempData["TreasuryError"] = LockedMessage(locked, (await _userManager.GetUserAsync(User))?.TimeZone);
        }
        return RedirectToAction(nameof(Index));
    }

    // Pay the ticked members straight off the balance sheet's "who we owe" list, in full.
    //
    // Records one ordinary "We paid a member what we owed" transaction each, in a single save, so a
    // payout run either lands whole or not at all. Nothing here is a new kind of movement — this is
    // the Record form's settle option, reached without typing each name and figure back in.
    [HttpPost]
    [ValidateAntiForgeryToken]
    public Task<IActionResult> SettleOwed(List<SettleOwedPickViewModel> picks, string? holder) =>
        SettleAsync(picks, holder, TreasurySettlementDirection.WePaidThem, "Tick who was paid first.");

    // The mirror on the other half of the sheet: tick whoever has now paid the LINKSHELL.
    //
    // Same panel, same rules, opposite direction — so it is the same endpoint body with the
    // direction flipped rather than a second copy that could drift from it. Clearing an owed-to-us
    // debt has no other route: there is no pickable option for it on the Record form, because
    // ticking a name off a derived list is strictly better than typing a name and a figure that the
    // list already knows.
    [HttpPost]
    [ValidateAntiForgeryToken]
    public Task<IActionResult> SettleOwedToUs(List<SettleOwedPickViewModel> picks, string? holder) =>
        SettleAsync(picks, holder, TreasurySettlementDirection.TheyPaidUs, "Tick who paid first.");

    private async Task<IActionResult> SettleAsync(
        List<SettleOwedPickViewModel> picks,
        // Whose mule the gil leaves from, or arrives on. One for the whole run: a payout is one
        // person sitting at one mule, so asking per ticked name would ask the same thing eight times.
        string? holder,
        TreasurySettlementDirection direction,
        string nothingTickedMessage)
    {
        var user = await _userManager.GetUserAsync(User);
        if (user is null) return Challenge();

        var linkshellId = user.PrimaryLinkshellId;
        if (!linkshellId.HasValue
            || !await CanManageAsync(user.Id, linkshellId.Value, HttpContext.RequestAborted))
        {
            return Forbid();
        }

        var chosen = picks
            .Where(pick => pick.Selected && !string.IsNullOrWhiteSpace(pick.CharacterName))
            .Select(pick => new TreasurySettlementPick(pick.CharacterName, pick.ExpectedAmount))
            .ToList();
        if (chosen.Count == 0)
        {
            TempData["TreasuryError"] = nothingTickedMessage;
            return RedirectToAction(nameof(Index));
        }
        // Ticking a name here moves real gil, so it answers the same question the Record form asks.
        // Without it a whole payout run files itself under "nobody named" on the who-has-what list.
        var holderName = holder?.Trim();
        if (string.IsNullOrWhiteSpace(holderName))
        {
            TempData["TreasuryError"] = direction == TreasurySettlementDirection.TheyPaidUs
                ? "Say who is holding this gil."
                : "Say whose gil this is coming out of.";
            return RedirectToAction(nameof(Index));
        }

        var membership = await _context.AppUserLinkshells
            .FirstOrDefaultAsync(link => link.AppUserId == user.Id && link.LinkshellId == linkshellId.Value);
        var actor = new TreasuryActor(user.Id, membership?.CharacterName ?? user.CharacterName);

        try
        {
            var result = await _settlements.SettleAsync(
                linkshellId.Value, chosen, direction, new TreasuryHolder(null, holderName),
                actor, HttpContext.RequestAborted);
            await _context.SaveChangesAsync(HttpContext.RequestAborted);
            // Nothing recorded is not a success, however ordinary the reason: say so in the banner
            // an officer already reads for problems rather than the one they read for confirmations.
            TempData[result.DidNothing ? "TreasuryError" : "TreasuryMessage"] = result.Message;
        }
        catch (TreasuryPeriodLockedException locked)
        {
            TempData["TreasuryError"] = LockedMessage(locked, user.TimeZone);
        }

        return RedirectToAction(nameof(Index));
    }

    // ---- helpers ----------------------------------------------------------------

    private static List<TreasuryMemberObligationViewModel> MapObligations(
        IReadOnlyList<MemberObligation> obligations) =>
        obligations
            .Select(owed => new TreasuryMemberObligationViewModel
            {
                CharacterName = owed.CharacterName ?? TreasuryLabels.UnnamedMember,
                Amount = owed.Amount,
                // Decided from the obligation, not from the displayed name: a member could in
                // principle be called whatever UnnamedMember says, and a negative row reads as a
                // name too.
                CanSettle = owed.CharacterName is not null && owed.Amount > 0,
            })
            .ToList();

    // Whose mules the gil on hand is sitting on. Same shape as the two obligation lists above, minus
    // the tick: gil leaves a mule by being SPENT, so there is nothing here to settle.
    //
    // SharePercent is against the largest row rather than the total, so a treasury split evenly
    // between four people shows four full bars instead of four quarter-stubs nobody can compare.
    // Magnitudes, because an overspent mule reads as a negative and a bar of negative width is
    // nothing at all.
    private static List<TreasuryGilHolderViewModel> MapHolders(IReadOnlyList<GilHolding> holders)
    {
        var largest = holders.Count == 0 ? 0L : holders.Max(holder => Math.Abs(holder.Amount));
        return holders
            .Select(holder => new TreasuryGilHolderViewModel
            {
                CharacterName = holder.CharacterName ?? TreasuryLabels.UnnamedHolder,
                Amount = holder.Amount,
                IsUnnamed = holder.CharacterName is null,
                SharePercent = largest == 0
                    ? 0
                    : (int)Math.Round(Math.Abs(holder.Amount) * 100d / largest),
            })
            .ToList();
    }

    private TreasuryEntryRowViewModel MapRow(
        JournalEntry entry, HashSet<int> reversedIds, HashSet<int> correctedIds, string? timeZone)
    {
        var lines = entry.Lines.OrderBy(line => line.LineNumber).ToList();
        return new TreasuryEntryRowViewModel
        {
            Id = entry.Id,
            EntryNumber = entry.EntryNumber,
            WhatHappened = TreasuryTransactionKinds.LabelFor(
                entry.TransactionKind,
                lines.FirstOrDefault(line => line.AccountNumber != TreasuryAccounts.GilOnHand)?.AccountName),
            Status = entry.Status,
            StatusLabel = TreasuryLabels.StatusLabel(entry.Status),
            Kind = entry.Kind,
            CashDelta = lines
                .Where(line => line.AccountNumber == TreasuryAccounts.GilOnHand)
                .Sum(line => line.Amount),
            Amount = AmountOf(entry),
            TransactionDate = _timeZones.ToUserTime(entry.TransactionDate, timeZone) ?? entry.TransactionDate,
            Memo = entry.Memo,
            Member = MemberOf(entry),
            Holder = HolderOf(entry),
            Recipients = RecipientsOf(entry),
            EnteredBy = entry.ConfirmedByCharacterName ?? entry.CreatedByCharacterName,
            IsReversed = reversedIds.Contains(entry.Id),
            IsFixed = correctedIds.Contains(entry.Id),
            CorrectionReason = entry.CorrectionReason,
            Halves = lines.Select(line => new TreasuryEntryHalfViewModel
            {
                CategoryName = line.AccountName,
                ClassLabel = TreasuryLabels.ClassLabelForNumber(line.AccountNumber),
                Amount = line.PresentedAmount,
                Member = line.CounterpartyCharacterName,
            }).ToList(),
        };
    }

    private TreasuryEntryRequest BuildRequest(
        RecordTreasuryEntryViewModel model,
        string? timeZone,
        IReadOnlyList<TreasuryRecipient> recipients) =>
        new(model.TransactionKind,
            model.Amount,
            // The date input posts naive wall-clock (Kind=Unspecified); convert through the viewer's zone
            // so it lands as a UTC instant, which the timestamptz column requires.
            model.TransactionDate == default
                ? DateTime.UtcNow
                : _timeZones.ToUtc(model.TransactionDate, timeZone) ?? DateTime.UtcNow,
            model.Memo,
            CounterpartyAppUserId: null,
            // A split names its members on their own lines; a lone counterparty would be a second,
            // conflicting answer to "who was this for".
            CounterpartyCharacterName: recipients.Count > 0 ? null : model.Member,
            // The holder is NOT suppressed for a split, unlike the counterparty above: a split that
            // moves gil on hand moves it off ONE mule however many people share the proceeds.
            HolderAppUserId: null,
            HolderCharacterName: model.Holder,
            Recipients: recipients.Count > 0 ? recipients : null);

    // The roster, annotated with what each member is still owed. Anyone owed gil who has since LEFT
    // the linkshell is added back on, because a departed member can still be owed and still has to
    // be settleable — they just cannot be picked for a split.
    private async Task<List<TreasuryRosterOption>> LoadRosterAsync(int linkshellId)
    {
        var roster = await _context.AppUserLinkshells
            .AsNoTracking()
            .Where(member => member.LinkshellId == linkshellId
                && member.CharacterName != null
                && member.CharacterName != "")
            .OrderBy(member => member.CharacterName)
            .Select(member => new { member.Id, Name = member.CharacterName! , member.Rank })
            .ToListAsync(HttpContext.RequestAborted);

        var sheet = await _treasury.GetBalanceSheetAsync(
            linkshellId, null, null, HttpContext.RequestAborted);
        var owedByName = sheet.OwedToMembers
            .Where(owed => owed.CharacterName is not null)
            .ToDictionary(owed => owed.CharacterName!, owed => owed.Amount, StringComparer.OrdinalIgnoreCase);

        var options = roster
            .Select(member => new TreasuryRosterOption(
                member.Id, member.Name, member.Rank, owedByName.GetValueOrDefault(member.Name)))
            .ToList();

        var onRoster = options.Select(option => option.CharacterName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        options.AddRange(owedByName
            .Where(owed => !onRoster.Contains(owed.Key))
            .Select(owed => new TreasuryRosterOption(0, owed.Key, null, owed.Value)));

        return options.OrderBy(option => option.CharacterName, StringComparer.OrdinalIgnoreCase).ToList();
    }

    // Membership rows to names, checked against THIS linkshell. Without the check a posted id could
    // attribute gil to a member of someone else's linkshell.
    private async Task<List<TreasuryRecipient>> ResolveRecipientsAsync(
        RecordTreasuryEntryViewModel model, int linkshellId)
    {
        var kind = TreasuryTransactionKinds.Find(model.TransactionKind);
        var wanted = model.RecipientMembershipIds.Distinct().ToList();

        if (kind is null || !kind.IsSplittable)
        {
            if (wanted.Count > 0)
            {
                ModelState.AddModelError(
                    nameof(model.RecipientMembershipIds), "That option only records gil for one member.");
            }
            // Gil owed with nobody attached cannot appear on the "who we still owe" list.
            if (kind is not null && kind.RequiresMember && string.IsNullOrWhiteSpace(model.Member))
            {
                ModelState.AddModelError(nameof(model.Member), "Say which member this is for.");
            }
            return new List<TreasuryRecipient>();
        }
        if (model.UnresolvedRecipients.Count > 0)
        {
            ModelState.AddModelError(
                nameof(model.RecipientMembershipIds),
                "Some of these members have left the linkshell. Remove them or pick who replaces them.");
            return new List<TreasuryRecipient>();
        }
        if (wanted.Count == 0)
        {
            ModelState.AddModelError(
                nameof(model.RecipientMembershipIds), "Pick who this is for — one member, or several to split it.");
            return new List<TreasuryRecipient>();
        }

        var recipients = await _context.AppUserLinkshells
            .AsNoTracking()
            .Where(member => member.LinkshellId == linkshellId
                && wanted.Contains(member.Id)
                && member.CharacterName != null
                && member.CharacterName != "")
            .Select(member => new TreasuryRecipient(member.AppUserId, member.CharacterName!))
            .ToListAsync(HttpContext.RequestAborted);

        if (recipients.Count != wanted.Count)
        {
            ModelState.AddModelError(
                nameof(model.RecipientMembershipIds), "One of those members is not in this linkshell.");
            return new List<TreasuryRecipient>();
        }
        // Otherwise the people who sort last get nothing, which is not a split.
        if (model.Amount < recipients.Count)
        {
            ModelState.AddModelError(
                nameof(model.RecipientMembershipIds), "That is not enough gil to give everyone at least 1.");
            return new List<TreasuryRecipient>();
        }

        return recipients;
    }

    // Re-pick a split when its entry is edited or fixed. Anyone who has since left the linkshell has
    // no membership row to point at — name them so the officer decides, rather than dropping them and
    // replacing a ten-way split with a smaller one.
    private static void LoadRecipientsInto(
        RecordTreasuryEntryViewModel model,
        JournalEntry entry,
        IReadOnlyList<TreasuryRosterOption> roster)
    {
        var byName = roster
            .GroupBy(member => member.CharacterName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First().MembershipId, StringComparer.OrdinalIgnoreCase);

        foreach (var line in entry.Lines.OrderBy(line => line.LineNumber))
        {
            if (string.IsNullOrWhiteSpace(line.CounterpartyCharacterName))
            {
                continue;
            }
            if (byName.TryGetValue(line.CounterpartyCharacterName, out var membershipId))
            {
                model.RecipientMembershipIds.Add(membershipId);
            }
            else
            {
                model.UnresolvedRecipients.Add(line.CounterpartyCharacterName);
            }
        }
    }

    // Every entry's halves sum to zero, so the positive side IS the amount — however many lines it is
    // spread over. Reading one line would report a split as one member's share.
    private static long AmountOf(JournalEntry entry) =>
        entry.Lines.Where(line => line.Amount > 0).Sum(line => line.Amount);

    private static string? MemberOf(JournalEntry entry) => entry.Lines
        .Select(line => line.CounterpartyCharacterName)
        .FirstOrDefault(name => !string.IsNullOrWhiteSpace(name));

    // Whose mule the entry's gil is on. Only the gil-on-hand line can carry one, so this reads the
    // entry's holder without having to say which line it came from.
    private static string? HolderOf(JournalEntry entry) => entry.Lines
        .Select(line => line.HolderCharacterName)
        .FirstOrDefault(name => !string.IsNullOrWhiteSpace(name));

    // Who an entry names, each person once.
    //
    // The dedupe is not defensive tidying. A member's name is written onto every half that is not
    // gil on hand, so an entry where NEITHER half is gil on hand — "someone owes us for work", or
    // gil owed to one member — carries the same name on both lines and used to render as
    // "Bob, Bob", which then tipped the row into its who-got-what layout as though it were a split.
    // A real split cannot collide here: its recipients are distinct membership rows.
    internal static List<string> RecipientsOf(JournalEntry entry) => entry.Lines
        .OrderBy(line => line.LineNumber)
        .Select(line => line.CounterpartyCharacterName)
        .Where(name => !string.IsNullOrWhiteSpace(name))
        .Select(name => name!)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToList();

    // The posted kind must exist, and — everywhere except Fix — must not be one that has been
    // superseded. Fix is the exception because an entry recorded under a retired kind still has to
    // be correctable; refusing it there would strand the entry forever.
    private void ValidateKind(RecordTreasuryEntryViewModel model, bool allowRetired)
    {
        var kind = TreasuryTransactionKinds.Find(model.TransactionKind);
        if (kind is null)
        {
            ModelState.AddModelError(nameof(model.TransactionKind), "Pick what happened from the list.");
            return;
        }
        if (kind.IsRetired && !allowRetired)
        {
            ModelState.AddModelError(
                nameof(model.TransactionKind),
                "That option is no longer available. Pick what happened from the list.");
        }
        // Gil that moves with no mule named cannot be found again — a linkshell has no bank, and
        // "gil on hand" is only the sum of what sits on people's characters. Enforced here rather
        // than with a [Required] attribute because whether it applies depends on the option picked.
        if (kind.RequiresHolder && string.IsNullOrWhiteSpace(model.Holder))
        {
            ModelState.AddModelError(
                nameof(model.Holder),
                kind.BringsCashIn
                    ? "Say who is holding this gil."
                    : "Say whose gil this is coming out of.");
        }
    }

    private string LockedMessage(TreasuryPeriodLockedException locked, string? timeZone)
    {
        var local = _timeZones.ToUserTime(locked.LockedThroughUtc, timeZone) ?? locked.LockedThroughUtc;
        return $"Entries dated on or before {local:MMM d, yyyy} are locked. A leader can unlock them.";
    }

    private async Task<(IActionResult? Failure, JournalEntry? Entry, TreasuryActor Actor)> LoadForWriteAsync(int id)
    {
        var user = await _userManager.GetUserAsync(User);
        if (user is null)
        {
            return (Challenge(), null, default!);
        }

        var entry = await _context.JournalEntries
            .Include(item => item.Lines)
            .FirstOrDefaultAsync(item => item.Id == id, HttpContext.RequestAborted);
        if (entry is null)
        {
            return (NotFound(), null, default!);
        }
        if (!await CanManageAsync(user.Id, entry.LinkshellId, HttpContext.RequestAborted))
        {
            return (Forbid(), null, default!);
        }

        var membership = await _context.AppUserLinkshells
            .FirstOrDefaultAsync(link => link.AppUserId == user.Id && link.LinkshellId == entry.LinkshellId);
        return (null, entry, new TreasuryActor(user.Id, membership?.CharacterName ?? user.CharacterName));
    }

    // The granular permission, matching the API exactly. A leader always has it.
    private async Task<bool> CanManageAsync(string appUserId, int linkshellId, CancellationToken cancellationToken)
    {
        var membership = await _context.AppUserLinkshells
            .FirstOrDefaultAsync(link => link.AppUserId == appUserId && link.LinkshellId == linkshellId, cancellationToken);
        if (membership is null)
        {
            return false;
        }
        if (await _adminOverride.IsActiveForAsync(appUserId, cancellationToken))
        {
            return true;
        }
        if (LinkshellRanks.IsLeader(membership.Rank))
        {
            return true;
        }

        var role = await _context.LinkshellRoles
            .AsNoTracking()
            .FirstOrDefaultAsync(item => item.LinkshellId == linkshellId && item.Name == membership.Rank, cancellationToken);
        return role?.CanManageTreasury == true;
    }

    // Leader tier: the stored Leader rank, OR the app-wide admin override. As
    // everywhere else, the override only applies to an existing membership.
    private async Task<bool> IsLeaderAsync(string appUserId, int linkshellId)
    {
        var membership = await _context.AppUserLinkshells
            .FirstOrDefaultAsync(link => link.AppUserId == appUserId && link.LinkshellId == linkshellId);
        if (membership is null) return false;
        return LinkshellRanks.IsLeader(membership.Rank)
               || await _adminOverride.IsActiveForAsync(appUserId, HttpContext.RequestAborted);
    }
}
