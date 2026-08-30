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
    // Two words for one idea, read at different sizes. NetWorth titles the whole bottom band and is
    // the plain-English framing the rest of this file follows; NetWorthFigure labels the figure
    // itself, beside gil on hand, where "what we're worth" would be a sentence where its twin has a
    // noun. Both are on screen at once, so they must not be the same string.
    public const string NetWorth = "What we're worth";
    public const string NetWorthFigure = "Net worth";
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
    // Each side of the sheet totals what that side LISTS, so the bottom line visibly comes from the
    // rows above it. Two different words rather than a bare "Total" on both sides: two panels
    // reading the same word invite adding them together.
    //
    // Not "Total assets": gil on hand moved down to the bottom line, where the arithmetic that
    // produces it lives, so this panel now lists only what the linkshell is owed and says so. A
    // total naming everything the linkshell holds, over a list that no longer shows the largest
    // part of it, is the "subtotal over hidden components" problem in its clearest form.
    public const string TotalOwedToUs = "Total owed to us";
    public const string TotalLiabilities = "Total liabilities";

    // --- one entry's fields ---
    public const string Date = "Date";
    public const string Recorded = "Recorded";
    public const string EnteredBy = "Entered by";
    public const string Member = "Member";
    // What the single name box is called on the two owed-to-us options. Not "Member": whoever owes a
    // linkshell gil is usually not in it — another linkshell, a buyer, someone's mule — so the box
    // takes a typed name and says out loud whose name it wants.
    public const string WhoOwesUs = "Who owes us";
    public const string WhoPaidUs = "Who paid us";
    // --- who is physically carrying the gil ---
    // A linkshell has no bank: its gil sits on members' mules. Gil on hand was one number with
    // nothing behind it, so "who actually has it" was answered from memory or from Discord
    // scrollback. Every movement of gil now names a mule, and the figure gets the same treatment
    // the other two figures on the sheet already had — a list of names that adds up to it.
    //
    // Two labels because the question flips with the direction. Asking "who's holding this gil" as
    // gil leaves reads as though the payer keeps it.
    public const string WhoIsHoldingIt = "Who's holding this gil";
    public const string WhosePocketItLeft = "Whose gil is this coming out of";
    // The stockpile-sale version of the same question. "Who sold it" is what an officer would say,
    // and the seller is the one left holding the gil, so one box answers both.
    public const string WhoSoldIt = "Who sold it";
    public const string GilHoldersTitle = "Who's holding the gil";
    // The disclosure on the gil-on-hand chip. It has to say there is something behind the number,
    // because a figure that is also a button looks exactly like a figure that is not.
    public const string GilHoldersHint = "Click to see who's holding the gil";
    // Gil recorded before anyone was asked, plus the gil-auction payouts, which move gil without
    // anyone saying off whose mule. Same idiom as UnnamedMember: it IS recorded, it just has nobody
    // attached, so say that rather than pretending the gil is missing.
    public const string UnnamedHolder = "Nobody named";
    public const string GilHoldersEmpty = "No gil recorded yet.";
    public const string Note = "Note";
    // The one free-text box on the record form. It replaced BOTH the reason dropdown and the old
    // separate Note field: with no categories left to pick between, what an officer types IS the
    // record of what happened, so it is asked for once and shown in the list under this heading.
    public const string ReasonField = "Reason";
    public const string ReasonHelp =
        "In your own words — this is what the transactions list will show.";
    public const string Amount = "Amount";
    public const string WhatHappened = "What happened?";
    public const string EntryNumberField = "Entry #";
    public const string Status = "Status";

    // --- the things that can happen, in the officer's words ---
    // The picker is a row of BUTTONS and nothing else. Every one of them asks exactly one thing now,
    // so the "Which one?" dropdown behind them is gone — nobody reads fourteen options to write down
    // a Kirin's Osode sale, and nobody reads two either. Gil In and Gil Out are named for what they
    // do to gil on hand, which is also how the transactions chips filter — so the words and the
    // filter finally agree. "Owed to us" moves no gil at all, which is why it is not a Gil In reason.
    public const string ActionGilIn = "Gil In";
    public const string ActionGilOut = "Gil Out";
    // Gil promised to members and not handed over yet — one of them or a dozen. It was called
    // "Split Gil", which described the arithmetic rather than the balance it moves, and left a
    // second button ("We Owe a Member") doing the same thing for a single name. One button now:
    // pick one member and the whole amount is theirs, pick several and it splits evenly.
    public const string ActionWhatWeOwe = "What We Owe";
    // History only, like Setup below: nothing is recorded under it any more, but the one retired
    // kind still filed here has to keep reading as what it was, and Fix has to be able to group it.
    public const string ActionSplitGil = "Split Gil";
    // Setup no longer has any buttons — "Starting Gil" and "Gil on Hand" were removed, and a Gil In
    // or Gil Out with a sentence in the reason box does what both of them did. The action label
    // stays so the entries recorded under it before that still read as what they were.
    public const string ActionSetup = "Setup";
    // Folded into What We Owe, which now takes one member or several. The label stays because Fix
    // still has to group an entry recorded under it, and it has to read as what it was.
    public const string ActionOweAMember = "We Owe a Member";
    // The fifth action reuses OwedToUs below rather than adding a second const that says the same
    // thing: the balance-sheet row and the button are the same idea and must not drift.

    // --- actions ---
    public const string RecordAction = "Record Transaction";
    public const string ConfirmAction = "Confirm";
    public const string EditDraftAction = "Edit";
    public const string DiscardDraftAction = "Discard";
    public const string FixAction = "Fix";
    public const string ReverseAction = "Reverse";
    // Paying members straight off the "who we owe" list. The wording says "record" rather than
    // "pay", because the gil moves in the game and this only writes down that it did.
    public const string SettleAction = "Record payment";
    public const string SettleHint = "Tick who has been paid. Each one is settled in full.";
    public const string SettleConfirm =
        "Record these as paid? This takes the gil out of gil on hand, so only do it once the gil "
        + "has actually been handed over.";
    // Not a block — a linkshell is allowed to know it is short. The Record form has never refused a
    // payout for want of gil on hand either, and refusing here would be a rule one surface enforced.
    // Only the paying-OUT side has a shortfall to warn about; gil arriving cannot overdraw anything.
    public const string SettleShortfall = "That is more than the linkshell has on hand.";

    // The mirror panel, on the "owed to us" half of the sheet. Ticking a name there records the gil
    // ARRIVING, which is the only way an owed-to-us debt is ever cleared — there is no option for it
    // on the Record form, because ticking a name off a list the app already knows beats typing the
    // name and the figure back in.
    // What the mule box on each settle panel is called. Paying out takes gil OFF a character; the
    // panel opposite puts it on one. Short, because both sit inline in a row of controls rather than
    // over a labelled field — the record form has room for the full question, this does not.
    public const string PayingOutOf = "Paying out of";
    public const string ReceivedOnto = "Received onto";
    public const string CollectAction = "Record payment received";
    public const string CollectHint = "Tick who has paid us. Each one is settled in full.";
    public const string CollectConfirm =
        "Record these as paid? This adds the gil to gil on hand, so only do it once the gil has "
        + "actually arrived.";
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
    // Fixed and Reversed are different things and now say so: a fix records the right numbers and
    // cancels the wrong ones in one action, so its entries read as "Fixed" and sit under their own
    // chip. Before, every corrected typo landed in Reversed and buried the entries someone had
    // actually cancelled outright.
    public const string FixedChip = "Fixed";

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
