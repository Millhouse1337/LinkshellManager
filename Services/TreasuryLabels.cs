using LinkshellManagerDiscordApp.Models;

namespace LinkshellManagerDiscordApp.Services;

// THE one home for every word the treasury shows a user.
//
// Both front-ends read from here — the Razor views directly, the Activity through the DTO mapper
// — so a label cannot drift between the web and Discord. That was a real failure mode: the same
// column was labelled "Source" on the web, "Type" in the Activity, and "EntryType" in the API.
//
// Everything here is plain English on purpose. This is a treasury for a group of people playing a
// video game, not a set of books anyone will audit, so the words on screen are the words an
// officer would actually say. The structure underneath is rigorous — two balanced halves per
// entry, nothing deleted, one derived balance — and none of that vocabulary is shown.
//
// TreasuryLabelsTests pins that no label here contains a term from Forbidden below.
public static class TreasuryLabels
{
    // --- the feature ---
    // Treasury is the nav section holding both halves of what a linkshell owns: Gil and Items. This
    // file covers the Gil half; the Items half predates it and keeps its own wording.
    public const string Section = "Treasury";
    public const string Title = "Gil";
    public const string ItemsTitle = "Items";
    public const string TransactionsCard = "Transactions";
    public const string EntryNoun = "Entry";
    public const string CategoryNoun = "Category";

    // --- the numbers ---
    public const string CashOnHand = "Gil on hand";
    public const string MoneyIn = "Gil in";
    public const string MoneyOut = "Gil out";
    public const string NetChange = "Net change";
    public const string OwedToUs = "Owed to us";
    public const string WeOwe = "We owe";
    public const string NetWorth = "What we're worth";
    public const string BooksBalance = "Adds up";
    public const string DoesNotBalance = "Does not add up";
    public const string Uncategorized = "Uncategorized";

    // --- the balance sheet ---
    // The card title is its own string rather than CashOnHand: a card whose body is a list of
    // figures cannot be titled with one of them, and the dashboard tile still reads CashOnHand.
    public const string BalanceCard = "Balance Sheet";
    public const string WhatWeHave = "What we have";
    public const string WhatWeOweSection = "What we owe";
    // Account 2000 holds nothing but member obligations — every kind that touches it names a
    // member, which EveryKindThatTouchesWhatWeOweNamesAMember pins.
    public const string OwedToMembers = "Owed to members";
    // An entry recorded before a member was required, or with the box left blank. It IS recorded;
    // it just has nobody attached, so say that rather than "not recorded".
    public const string UnnamedMember = "No member named";
    public const string ShowWhoWeOwe = "Who";
    public const string SectionTotal = "Total";

    // --- one entry's fields ---
    public const string Date = "Date";
    public const string Recorded = "Recorded";
    public const string EnteredBy = "Entered by";
    public const string Member = "Member";
    public const string Note = "Note";
    public const string Amount = "Amount";
    public const string WhatHappened = "What happened?";
    public const string EntryNumberField = "Entry #";
    public const string Status = "Status";

    // --- actions ---
    public const string RecordAction = "Record Transaction";
    public const string SaveDraftAction = "Save as draft";
    public const string ConfirmAction = "Confirm";
    public const string EditDraftAction = "Edit";
    public const string DiscardDraftAction = "Discard";
    public const string FixAction = "Fix";
    public const string ReverseAction = "Reverse";
    public const string CheckGilAction = "Gil on Hand";
    public const string LockAction = "Lock older entries";
    public const string UnlockAction = "Unlock";
    public const string ShowDetails = "Show the bookkeeping details";
    // A split writes one line per member on the same category, so this panel is a who-got-what list
    // rather than two halves. Say so, or nobody thinks to open it.
    public const string ShowSplitDetails = "Show who got what";

    // --- statuses ---
    public const string DraftChip = "Draft";
    public const string ConfirmedChip = "Confirmed";
    public const string ReversedChip = "Reversed";

    // --- copy ---
    public const string EmptyState =
        "Nothing recorded yet. Track gil coming in and going out to keep the treasury accurate.";
    public const string EmptySearch = "No entries match your search.";
    public const string ReverseConfirm =
        "This adds an opposite entry that cancels this one out. The original stays in the list.";
    public const string DiscardConfirm = "Discard this draft? Nothing has been recorded yet.";
    public const string BasisNote = "Recorded when gil actually moves · amounts in gil";
    public const string LockHelp = "Locks every entry dated on or before this date.";

    // The five kinds of category, in the officer's words. Returns the consts above rather than
    // its own literals, so the balance sheet's section headings and a line's class label are
    // guaranteed to be the same words.
    public static string ClassLabel(string? accountClass) => accountClass switch
    {
        LedgerAccountClasses.Holds => WhatWeHave,
        LedgerAccountClasses.Owes => WhatWeOweSection,
        LedgerAccountClasses.Worth => NetWorth,
        LedgerAccountClasses.MoneyIn => MoneyIn,
        LedgerAccountClasses.MoneyOut => MoneyOut,
        _ => Uncategorized,
    };

    public static string ClassLabelForNumber(int accountNumber) =>
        ClassLabel(LedgerAccountClasses.FromNumber(accountNumber));

    public static string StatusLabel(string? status) =>
        JournalEntryStatuses.IsDraft(status) ? DraftChip : ConfirmedChip;

    // "000142" -> "#142". Stored zero-padded so it sorts as text; shown without the padding because
    // six digits is a formality nobody in a linkshell says out loud. Every place an entry is quoted
    // — a row's own number, a "Reverses #142" tag, the fix page title — goes through here, so the
    // number on the row and the number in the reference can never drift apart.
    public static string EntryReference(string? entryNumber)
    {
        if (string.IsNullOrWhiteSpace(entryNumber))
        {
            return "#";
        }

        var trimmed = entryNumber.Trim().TrimStart('0');
        return trimmed.Length == 0 ? "#0" : $"#{trimmed}";
    }

    // Words that are correct bookkeeping and wrong for this audience. Every one of them describes
    // something this feature genuinely does — it just does not say so out loud.
    //
    // "credit" earns a special mention: it already means DKP credit throughout this app, so
    // showing accounting credits beside DKP credits would be actively misleading rather than
    // merely obscure.
    public static readonly IReadOnlyList<string> Forbidden = new[]
    {
        "journal", "ledger", "debit", "credit", "post to", "void", "revenue", "expense",
        "net asset", "change in net assets", "statement of activities", "trial balance",
        "accounts receivable", "accounts payable", "accrual", "accrued", "fiscal",
        "chart of accounts", "reconcil", "general ledger", "double entry", "contra",
    };
}
