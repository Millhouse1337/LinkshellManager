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
    private readonly TimeZoneConversionService _timeZones;
    private readonly TreasuryBalanceService _treasury;
    private readonly TreasuryJournalWriter _journal;
    private readonly LedgerAccountProvisioner _accounts;
    private readonly LedgerPeriodGuard _periods;

    public ManageFinancesController(
        ApplicationDbContext context,
        UserManager<AppUser> userManager,
        TimeZoneConversionService timeZones,
        TreasuryBalanceService treasury,
        TreasuryJournalWriter journal,
        LedgerAccountProvisioner accounts,
        LedgerPeriodGuard periods)
    {
        _context = context;
        _userManager = userManager;
        _timeZones = timeZones;
        _treasury = treasury;
        _journal = journal;
        _accounts = accounts;
        _periods = periods;
    }

    public async Task<IActionResult> Index(int page = 0, string? search = null, string? filter = null)
    {
        var user = await _userManager.GetUserAsync(User);
        if (user is null) return Challenge();

        var linkshellId = user.PrimaryLinkshellId;
        var model = new ManageFinancesViewModel
        {
            LinkshellName = user.PrimaryLinkshellName,
            Search = search,
            Filter = filter,
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

        var (snapshot, owedToMembers) =
            await _treasury.GetBalanceSheetAsync(linkshellId.Value, null, null, cancellationToken);
        model.CashOnHand = snapshot.CashOnHand;
        model.MoneyIn = snapshot.MoneyIn;
        model.MoneyOut = snapshot.MoneyOut;
        model.NetChange = snapshot.NetChange;
        model.OwedToUs = snapshot.OwedToUs;
        model.WeOwe = snapshot.WeOwe;
        model.NetWorth = snapshot.NetWorth;
        model.OwedToMembers = owedToMembers
            .Select(owed => new TreasuryMemberObligationViewModel
            {
                CharacterName = owed.CharacterName ?? TreasuryLabels.UnnamedMember,
                Amount = owed.Amount,
            })
            .ToList();
        model.Balances = snapshot.Balances;
        model.UncategorizedCount = await _treasury.GetUncategorizedCountAsync(linkshellId.Value, cancellationToken);
        model.LockedThrough = await _periods.GetLockedThroughAsync(linkshellId.Value, cancellationToken);

        var query = _context.JournalEntries
            .AsNoTracking()
            .Include(entry => entry.Lines)
            .Where(entry => entry.LinkshellId == linkshellId.Value);
        if (!model.CanManage)
        {
            query = query.Where(entry => entry.Status == JournalEntryStatuses.Confirmed);
        }
        query = ApplyFilter(query, filter);

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
                        && EF.Functions.ILike(line.CounterpartyCharacterName, pattern))));
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
        var reversedIds = ids.Count == 0
            ? new List<int>()
            : await _context.JournalEntries
                .AsNoTracking()
                .Where(entry => entry.ReversesJournalEntryId != null && ids.Contains(entry.ReversesJournalEntryId.Value))
                .Select(entry => entry.ReversesJournalEntryId!.Value)
                .Distinct()
                .ToListAsync(cancellationToken);
        var reversed = reversedIds.ToHashSet();

        model.Entries = entries.Select(entry => MapRow(entry, reversed, user.TimeZone)).ToList();
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

        return View(new RecordTreasuryEntryViewModel
        {
            LinkshellId = linkshellId.Value,
            LinkshellName = user.PrimaryLinkshellName,
            Options = TreasuryTransactionKinds.Pickable().ToList(),
            Roster = await LoadRosterAsync(linkshellId.Value),
            // The picker posts naive wall-clock, so default it to the viewer's local now.
            TransactionDate = _timeZones.ToUserTime(DateTime.UtcNow, user.TimeZone) ?? DateTime.UtcNow,
        });
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
        if (TreasuryTransactionKinds.Find(model.TransactionKind) is null)
        {
            ModelState.AddModelError(nameof(model.TransactionKind), "Pick what happened from the list.");
        }

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
            TransactionKind = entry.TransactionKind ?? TreasuryTransactionKinds.SoldAnItem,
            Amount = AmountOf(entry),
            TransactionDate = _timeZones.ToUserTime(entry.TransactionDate, user?.TimeZone) ?? entry.TransactionDate,
            Memo = entry.Memo,
            Member = MemberOf(entry),
            Confirm = false,
            Options = TreasuryTransactionKinds.Pickable().ToList(),
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
        model.Options = TreasuryTransactionKinds.Pickable().ToList();
        model.Roster = await LoadRosterAsync(entry.LinkshellId);
        if (TreasuryTransactionKinds.Find(model.TransactionKind) is null)
        {
            ModelState.AddModelError(nameof(model.TransactionKind), "Pick what happened from the list.");
        }

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
            Options = TreasuryTransactionKinds.Pickable().ToList(),
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
        model.Options = TreasuryTransactionKinds.Pickable().ToList();
        model.Roster = await LoadRosterAsync(entry.LinkshellId);
        if (TreasuryTransactionKinds.Find(model.TransactionKind) is null)
        {
            ModelState.AddModelError(nameof(model.TransactionKind), "Pick what happened from the list.");
        }

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

    // ---- helpers ----------------------------------------------------------------

    private static IQueryable<JournalEntry> ApplyFilter(IQueryable<JournalEntry> query, string? filter) =>
        filter?.Trim().ToLowerInvariant() switch
        {
            // Direction comes from whether gil on hand moved up or down, never from matching a string.
            "in" => query.Where(entry => entry.Lines.Any(line =>
                line.AccountNumber == TreasuryAccounts.GilOnHand && line.Amount > 0)),
            "out" => query.Where(entry => entry.Lines.Any(line =>
                line.AccountNumber == TreasuryAccounts.GilOnHand && line.Amount < 0)),
            "drafts" => query.Where(entry => entry.Status == JournalEntryStatuses.Draft),
            "reversed" => query.Where(entry =>
                entry.Kind == JournalEntryKinds.Reversal
                || query.Any(other => other.ReversesJournalEntryId == entry.Id)),
            _ => query,
        };

    private TreasuryEntryRowViewModel MapRow(JournalEntry entry, HashSet<int> reversedIds, string? timeZone)
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
            Recipients = RecipientsOf(entry),
            EnteredBy = entry.ConfirmedByCharacterName ?? entry.CreatedByCharacterName,
            IsReversed = reversedIds.Contains(entry.Id),
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

        var (_, owedToMembers) =
            await _treasury.GetBalanceSheetAsync(linkshellId, null, null, HttpContext.RequestAborted);
        var owedByName = owedToMembers
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
                nameof(model.RecipientMembershipIds), "Pick at least one member to share the gil with.");
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

    private static List<string> RecipientsOf(JournalEntry entry) => entry.Lines
        .OrderBy(line => line.LineNumber)
        .Select(line => line.CounterpartyCharacterName)
        .Where(name => !string.IsNullOrWhiteSpace(name))
        .Select(name => name!)
        .ToList();

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
        if (LinkshellRanks.IsLeader(membership.Rank))
        {
            return true;
        }

        var role = await _context.LinkshellRoles
            .AsNoTracking()
            .FirstOrDefaultAsync(item => item.LinkshellId == linkshellId && item.Name == membership.Rank, cancellationToken);
        return role?.CanManageTreasury == true;
    }

    private async Task<bool> IsLeaderAsync(string appUserId, int linkshellId)
    {
        var membership = await _context.AppUserLinkshells
            .FirstOrDefaultAsync(link => link.AppUserId == appUserId && link.LinkshellId == linkshellId);
        return LinkshellRanks.IsLeader(membership?.Rank);
    }
}
