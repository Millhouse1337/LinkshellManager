using System.ComponentModel.DataAnnotations;
using LinkshellManagerDiscordApp.Models;

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
    // Everyone who got a share, when this was split. One name means the ordinary single-member case.
    public List<string> Recipients { get; set; } = new();
    public string? EnteredBy { get; set; }
    public bool IsReversed { get; set; }
    public string? ReversesEntryNumber { get; set; }
    public string? CorrectionReason { get; set; }

    // Only rendered inside the collapsed "show the bookkeeping details" panel.
    public List<TreasuryEntryHalfViewModel> Halves { get; set; } = new();

    public bool IsDraft => JournalEntryStatuses.IsDraft(Status);
}

// One member the linkshell still owes.
public class TreasuryMemberObligationViewModel
{
    public string CharacterName { get; set; } = string.Empty;
    public long Amount { get; set; }
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
    public bool Balances { get; set; }
    public int UncategorizedCount { get; set; }
    public DateTime? LockedThrough { get; set; }

    public List<TreasuryEntryRowViewModel> Entries { get; set; } = new();
    public int TotalEntries { get; set; }
    public int Page { get; set; }
    public int PageCount { get; set; }
    public string? Search { get; set; }
    public string? Filter { get; set; }
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
    public string TransactionKind { get; set; } = TreasuryTransactionKinds.SoldAnItem;

    [Range(1, long.MaxValue, ErrorMessage = "Enter how much gil moved.")]
    [Display(Name = "Amount")]
    public long Amount { get; set; }

    [Display(Name = "Date")]
    public DateTime TransactionDate { get; set; } = DateTime.UtcNow;

    [StringLength(1024)]
    [Display(Name = "Note")]
    public string? Memo { get; set; }

    [StringLength(256)]
    [Display(Name = "Member")]
    public string? Member { get; set; }

    // Who shares the amount, when the chosen option splits it. Membership rows, not names: a name is
    // not unique and is not something the server can check against a roster.
    [Display(Name = "Who gets a share")]
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
