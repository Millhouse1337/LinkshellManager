using System.ComponentModel.DataAnnotations;
using LinkshellManagerDiscordApp.Models;
using LinkshellManagerDiscordApp.Services;

namespace LinkshellManagerDiscordApp.ViewModels;

// One row in the treasury list.
public class TreasuryEntryRowViewModel
{
    public int Id { get; set; }
    public string EntryNumber { get; set; } = string.Empty;
    public string WhatHappened { get; set; } = string.Empty;
    public string Status { get; set; } = JournalEntryStatuses.Confirmed;
    public string StatusLabel { get; set; } = string.Empty;
    public string Kind { get; set; } = JournalEntryKinds.Standard;

    // Signed: negative means gil left the treasury.
    public long CashDelta { get; set; }
    public long Amount { get; set; }

    public DateTime TransactionDate { get; set; }
    public string? Memo { get; set; }
    public string? Member { get; set; }
    // Whose mule this entry's gil landed on, or came off. Null when it moved no gil, and for
    // everything recorded before the question was asked. Shown on the row so "where did that 8M go"
    // is answerable without opening the bookkeeping details.
    public string? Holder { get; set; }
    // Everyone who got a share, when this was split. One name means the ordinary single-member case.
    public List<string> Recipients { get; set; } = new();
    public string? EnteredBy { get; set; }
    public bool IsReversed { get; set; }
    // Cancelled by a FIX rather than by an outright reversal: something recorded the right numbers
    // in its place. Both are true at once — a fixed entry is also a reversed one — so the row shows
    // the more specific word and the chips split on it.
    public bool IsFixed { get; set; }
    public string? ReversesEntryNumber { get; set; }
    public string? CorrectionReason { get; set; }

    // Only rendered inside the collapsed "show the bookkeeping details" panel.
    public List<TreasuryEntryHalfViewModel> Halves { get; set; } = new();

    public bool IsDraft => JournalEntryStatuses.IsDraft(Status);
}

// One tick-and-record panel. Both halves of the balance sheet get one, and both are rendered by the
// same partial from one of these — so a rule that makes ticking safe on the "who we owe" side cannot
// go missing on the "owed to us" side.
//
// Only two things genuinely differ: which endpoint the form posts to, and the words. The shortfall
// warning is the exception that proves it — gil LEAVING can overdraw what the linkshell holds, and
// gil arriving cannot, so the collecting panel simply leaves CashOnHand null and the warning with it.
public class TreasurySettlePanelViewModel
{
    public List<TreasuryMemberObligationViewModel> Obligations { get; set; } = new();

    public bool CanManage { get; set; }

    // The action on ManageFinancesController this panel's form posts to.
    public string FormAction { get; set; } = string.Empty;

    public string Hint { get; set; } = string.Empty;
    public string ButtonLabel { get; set; } = string.Empty;
    public string Confirm { get; set; } = string.Empty;

    // Null on the collecting side: there is nothing to be short of when the gil is coming in.
    public long? CashOnHand { get; set; }
    public string? Shortfall { get; set; }

    // What the panel's mule box is called. Ticking a name here writes a real gil movement, so it
    // answers the same question the Record form asks — without it a whole payout run would file
    // itself under "nobody named" on the who's-holding-the-gil list.
    public string HolderLabel { get; set; } = string.Empty;

    // Names to suggest in that box. Suggestions only: gil regularly sits on a mule that is not a
    // roster row.
    public IReadOnlyList<string> Roster { get; set; } = Array.Empty<string>();

    // Anyone actually settleable. A panel of nothing but unsettleable rows is a report, not a form.
    public bool CanSettleAny => CanManage && Obligations.Any(owed => owed.CanSettle);
}

// One member the linkshell still owes.
public class TreasuryMemberObligationViewModel
{
    public string CharacterName { get; set; } = string.Empty;
    public long Amount { get; set; }

    // Whether this row can be ticked and paid. False for the "no member named" bucket — a payment
    // has to name who it went to — and for a row that has gone negative through over-settling,
    // where "pay in full" has nothing to mean.
    public bool CanSettle { get; set; }
}

// One person and the slice of the linkshell's gil sitting on their character.
public class TreasuryGilHolderViewModel
{
    // The word for the nobody-named bucket is resolved by the controller, so the view never has to
    // decide what an empty name means.
    public string CharacterName { get; set; } = string.Empty;
    public long Amount { get; set; }

    // Gil recorded before anyone was asked whose mule it went on, plus gil-auction payouts, which
    // have no answer to give. Kept rather than hidden: dropping it would make the rows visibly fail
    // to add up to the figure above them.
    public bool IsUnnamed { get; set; }

    // Share of the LARGEST row, for the bar behind it — against the largest rather than the total,
    // so a treasury split evenly four ways shows four full bars instead of four quarter-stubs.
    public int SharePercent { get; set; }
}

// One ticked row on the way back in. Posted as an indexed list so the name and the figure the
// screen was showing stay together: parallel arrays would fall out of step the moment a box is left
// unticked, and the figure is what stops a stale page paying out the wrong amount.
public class SettleOwedPickViewModel
{
    public string CharacterName { get; set; } = string.Empty;
    public long ExpectedAmount { get; set; }
    public bool Selected { get; set; }
}

public class TreasuryEntryHalfViewModel
{
    public string CategoryName { get; set; } = string.Empty;
    public string ClassLabel { get; set; } = string.Empty;
    public long Amount { get; set; }
    public string? Member { get; set; }
}

// The Treasury page.
public class ManageFinancesViewModel
{
    public int LinkshellId { get; set; }
    public string? LinkshellName { get; set; }
    public bool CanManage { get; set; }
    public bool CanLock { get; set; }

    public long CashOnHand { get; set; }
    public long MoneyIn { get; set; }
    public long MoneyOut { get; set; }
    public long NetChange { get; set; }
    public long OwedToUs { get; set; }
    public long WeOwe { get; set; }
    public long NetWorth { get; set; }
    // Who WeOwe is owed to, largest first. Always adds up to WeOwe — same lines, one read.
    public List<TreasuryMemberObligationViewModel> OwedToMembers { get; set; } = new();

    // Who still owes the LINKSHELL. The mirror of OwedToMembers, and ticked the same way: both halves
    // of the sheet are derived lists of names, and both are settled by ticking one rather than by
    // typing a name and a figure the list already knows.
    public List<TreasuryMemberObligationViewModel> OwedToUsBy { get; set; } = new();

    // The two tick-and-record panels, described rather than duplicated. One partial renders both, so
    // the rules that make ticking safe — the ExpectedAmount re-check, the running total, settling in
    // full only — cannot come out different on one side of the sheet than the other.
    public TreasurySettlePanelViewModel PayPanel => new()
    {
        Obligations = OwedToMembers,
        CanManage = CanManage,
        FormAction = "SettleOwed",
        Hint = TreasuryLabels.SettleHint,
        ButtonLabel = TreasuryLabels.SettleAction,
        Confirm = TreasuryLabels.SettleConfirm,
        // Gil leaving can overdraw what the linkshell holds; gil arriving cannot.
        CashOnHand = CashOnHand,
        Shortfall = TreasuryLabels.SettleShortfall,
        // Paying out takes gil OFF a mule; the panel opposite puts it on one. Different question,
        // different word — the same flip TreasuryTransactionKind.HolderLabel makes on the form.
        HolderLabel = TreasuryLabels.PayingOutOf,
        Roster = HolderOptions,
    };

    public TreasurySettlePanelViewModel CollectPanel => new()
    {
        Obligations = OwedToUsBy,
        CanManage = CanManage,
        FormAction = "SettleOwedToUs",
        Hint = TreasuryLabels.CollectHint,
        ButtonLabel = TreasuryLabels.CollectAction,
        Confirm = TreasuryLabels.CollectConfirm,
        HolderLabel = TreasuryLabels.ReceivedOnto,
        Roster = HolderOptions,
    };

    // Whose mules the gil on hand is actually sitting on, largest first. The third figure on the
    // sheet to get names behind it, and unlike the two lists above it is never ticked: gil leaves a
    // mule by being SPENT, and every movement now names the mule it moved through, so the list keeps
    // itself current. Always adds up to CashOnHand — same lines, one read.
    public List<TreasuryGilHolderViewModel> GilHolders { get; set; } = new();

    // Names for the two settle panels' mule boxes. Filled by the controller from the same roster the
    // Record form's picker uses.
    public IReadOnlyList<string> HolderOptions { get; set; } = Array.Empty<string>();
    public bool Balances { get; set; }
    public DateTime? LockedThrough { get; set; }

    public List<TreasuryEntryRowViewModel> Entries { get; set; } = new();
    public int TotalEntries { get; set; }
    public int Page { get; set; }
    public int PageCount { get; set; }
    public string? Search { get; set; }
    public string? Filter { get; set; }

    // "sold" shows the Items section's archive instead of the stockpile. Carried on this model only
    // so the page can hand it to the Items component; nothing about gil reads it.
    public string? ItemView { get; set; }
}

// The Record form. Replaces ManageRevenueViewModel, whose free-text "Source" list was the origin of the
// vocabulary split — and whose StringLength(64) sat over a varchar(16) column, so any value longer than
// sixteen characters passed validation and then threw at the database.
public class RecordTreasuryEntryViewModel
{
    public int Id { get; set; }
    public int LinkshellId { get; set; }
    public string? LinkshellName { get; set; }

    [Required(ErrorMessage = "Pick what happened.")]
    [Display(Name = "What happened?")]
    // Gil In, because it is the first button and the one the form opens on. It must be a PICKABLE
    // kind: an unpickable default makes PickableWith append it, and the form quietly offers a reason
    // the picker was meant to have retired.
    public string TransactionKind { get; set; } = TreasuryTransactionKinds.OtherMoneyIn;

    [Range(1, long.MaxValue, ErrorMessage = "Enter how much gil moved.")]
    [Display(Name = "Amount")]
    public long Amount { get; set; }

    [Display(Name = "Date")]
    public DateTime TransactionDate { get; set; } = DateTime.UtcNow;

    [StringLength(1024)]
    [Display(Name = "Reason")]
    public string? Memo { get; set; }

    [StringLength(256)]
    [Display(Name = "Member")]
    public string? Member { get; set; }

    // Whose mule the gil lands on, or comes off. Required whenever the chosen option moves gil on
    // hand — ValidateKind enforces it, rather than a [Required] attribute, because whether it is
    // needed depends on the option picked and an attribute cannot see that.
    //
    // The Display name is a placeholder: the label rendered is the option's own HolderLabel, which
    // flips between "who's holding this gil" and "whose gil is this coming out of" with the
    // direction.
    [StringLength(256)]
    [Display(Name = "Who's holding this gil")]
    public string? Holder { get; set; }

    // Who the amount is for, when the chosen option splits it. Membership rows, not names: a name is
    // not unique and is not something the server can check against a roster.
    //
    // "Who this is for" rather than "Who gets a share": one member is a legitimate answer here now,
    // and a share of one is not something anyone would call a share.
    [Display(Name = "Who this is for")]
    public List<int> RecipientMembershipIds { get; set; } = new();

    // What the officer chooses between: keep it editable, or put it on the books now.
    public bool Confirm { get; set; } = true;

    // Populated for the form's picker, grouped so the everyday options come first.
    public IReadOnlyList<TreasuryTransactionKind> Options { get; set; } = Array.Empty<TreasuryTransactionKind>();

    // Everyone who could be given a share. Must be re-populated on every render, including the one
    // after a failed validation, or the form comes back with an empty roster.
    public IReadOnlyList<TreasuryRosterOption> Roster { get; set; } = Array.Empty<TreasuryRosterOption>();

    // Names on the entry being fixed whose members have since left. Blocks the submit rather than
    // quietly replacing a ten-way split with a smaller one.
    public List<string> UnresolvedRecipients { get; set; } = new();

    public TreasuryTransactionKind? SelectedOption => TreasuryTransactionKinds.Find(TransactionKind);
}

// Owed is what this member is still due, so "We paid a member what we owed" can fill the amount in
// rather than asking the officer to remember it. Zero for everyone else.
public sealed record TreasuryRosterOption(
    int MembershipId, string CharacterName, string? Rank, long Owed = 0);

// The Fix form: an entry is being replaced, and it has to say why.
public class FixTreasuryEntryViewModel : RecordTreasuryEntryViewModel
{
    public string EntryNumber { get; set; } = string.Empty;

    [Required(ErrorMessage = "Say what was wrong with the original entry.")]
    [StringLength(512)]
    [Display(Name = "What was wrong?")]
    public string Reason { get; set; } = string.Empty;
}
