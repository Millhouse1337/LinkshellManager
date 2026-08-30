using LinkshellManagerDiscordApp.Services;

namespace LinkshellManagerDiscordApp.Models;

// The handful of things that can happen to a linkshell's gil, plus setup.
//
// This is the first half of the picker: an officer chooses an ACTION, and only then — where there
// is genuinely a choice left — a reason. Most of the time there is not.
//
// NO CATEGORIES. Gil In and Gil Out each offer exactly one thing, so picking one of them asks no
// further question: you type what happened in your own words and that sentence is the record. The
// income and expense buckets that used to sit behind those two buttons (item sales, donations, paid
// work, supplies) are gone, because a linkshell of a dozen people does not read a breakdown by
// category — it reads a list of sentences. What the app records for ITSELF is still labelled
// precisely, which is why "Gil In — Stockpile Item Sold" exists and is not something anyone picks.
//
// Gil In and Gil Out are named for what they do to GIL ON HAND, which is exactly how the
// transactions chips filter (the sign of the gil-on-hand line, never a string match). Everything
// that does NOT move gil on hand therefore gets its own button rather than hiding under one of
// them: What We Owe and Owed to us each move a different balance, and filing either under "Gil Out"
// would put it outside the Gil out chip and make the words disagree with the filter.
//
// A static class of string consts plus an ordered table, matching JournalEntryKinds and
// LedgerAccountClasses — the repo's idiom for a controlled vocabulary.
public static class TreasuryTransactionActions
{
    public const string GilIn = "GilIn";
    public const string GilOut = "GilOut";
    // Gil promised to members and not handed over yet. ONE button for one member or a dozen: who is
    // owed is a list, and a list of one is still a list. It was two buttons — "Split Gil" for
    // several and "We Owe a Member" for one — which asked the officer to classify the payout before
    // they had picked anyone, and put the same movement in two places in the transactions list.
    public const string WhatWeOwe = "WhatWeOwe";
    public const string OwedToUs = "OwedToUs";
    // History only, all three of them: nothing pickable is filed under any of these any more, both
    // front-ends drop an action with no pickable reason from the button row, and they survive so the
    // entries recorded under them still read as what they were. An action key is never stored on a
    // row — it groups the menu — so keeping one costs nothing but a line here.
    public const string SplitGil = "SplitGil";
    public const string OweAMember = "OweAMember";
    public const string Setup = "Setup";

    // Display order. Setup is last and is rendered outside the record form entirely — its members
    // are one-time or app-driven, not things an officer picks on a Tuesday.
    public static readonly IReadOnlyList<(string Key, string Label)> All = new[]
    {
        (GilIn, TreasuryLabels.ActionGilIn),
        (GilOut, TreasuryLabels.ActionGilOut),
        (WhatWeOwe, TreasuryLabels.ActionWhatWeOwe),
        (OwedToUs, TreasuryLabels.OwedToUs),
        (SplitGil, TreasuryLabels.ActionSplitGil),
        (OweAMember, TreasuryLabels.ActionOweAMember),
        (Setup, TreasuryLabels.ActionSetup),
    };

    private static readonly Dictionary<string, string> LabelsByKey =
        All.ToDictionary(action => action.Key, action => action.Label, StringComparer.OrdinalIgnoreCase);

    public static string? LabelFor(string? key) =>
        key is not null && LabelsByKey.TryGetValue(key, out var label) ? label : null;

    public static bool IsKnown(string? key) => LabelFor(key) is not null;
}

// One thing that can happen to a linkshell's gil, in the officer's words.
//
// AddTo/TakeFrom are the two halves of the movement. They are FIXED per kind, which is the whole
// safety property: an officer picks "Sold an item" and the pair is correct by construction. They
// are never asked which category to add to and which to take from, and no endpoint accepts a
// free-form pair. This is also why the picker was simplified by REGROUPING these rather than by
// introducing a "Gil In" kind that takes a category as a parameter — that would hand the choice of
// accounts to the client and there would be nothing left holding the pair together.
public sealed record TreasuryTransactionKind(
    string Key,
    // What the TRANSACTIONS LIST calls this, for every row ever recorded under it — past entries
    // included, because LabelFor reads the catalog at render time rather than storing the words on
    // the row. Editing one therefore re-words history, which is a deliberate choice here: these are
    // written as "Action — Reason" so the list speaks the same language as the picker, and an entry
    // recorded under the old wording reads as the thing you would pick today to record it again.
    //
    // Nothing computes on this. The figures, the filters and the balances all key on the account
    // numbers, so the wording is free to change without moving a single gil.
    string Label,
    // What the PICKER calls this, under its action. Free to be short and repetitive ("Something
    // else" appears under both Gil In and Gil Out) because the action beside it supplies the
    // context that Label has to carry on its own.
    string ReasonLabel,
    string Help,
    int AddTo,
    int TakeFrom,
    string Action,
    bool ShowsMember,
    // "{0}" is the formatted amount.
    string PreviewTemplate,
    string EntryKind = JournalEntryKinds.Standard,
    // Offered to an officer in the picker. False covers two different situations — see IsRetired.
    bool IsPickable = true,
    // Superseded: nothing may record this again, on any surface, ever. Distinct from a merely
    // unpickable kind, which the APP still records (the gil-count adjustments, and the settle-up
    // the tick-and-pay panel writes) and which a write endpoint must therefore keep accepting.
    //
    // Without this distinction "not in the picker" would be worth nothing: neither surface checks
    // pickability on the write path, so a browser tab left open on the old form would still record
    // a superseded kind long after it vanished from the menu.
    bool IsRetired = false,
    // Which of this kind's two categories gets one line PER MEMBER instead of one line for the
    // whole amount. Must be AddTo or TakeFrom; null means this kind is never split.
    //
    // The other category stays whole and carries no member, because an aggregate is not
    // attributable to one person. Which side splits is per-kind rather than a rule, because the
    // right side genuinely differs: paying members out splits the gil-paid category, whereas
    // owing them splits what we owe — the side the tick-and-pay panel later draws down.
    int? SplitAccount = null,
    // What the single name box is CALLED, when ShowsMember is on. "Member" is wrong for the
    // owed-to-us pair: the party who owes the linkshell gil is usually not a member at all —
    // another linkshell, a buyer, someone's mule — and calling the box "Member" sent officers
    // hunting the roster for a name that was never on it. Per-kind, because the same box means
    // "who owes us" on one option and "who paid us" on the next.
    string CounterpartyLabel = TreasuryLabels.Member)
{
    public bool IsSplittable => SplitAccount is not null;

    // A member is REQUIRED, not merely offered, when the entry creates or settles gil owed to one.
    // The balance sheet lists who is still owed, and an obligation with nobody attached cannot
    // appear on that list — it would sit in an anonymous bucket forever. Splits need no rule here:
    // they already refuse to record without recipients.
    public bool RequiresMember =>
        !IsSplittable && (AddTo == TreasuryAccounts.WeOwe || TakeFrom == TreasuryAccounts.WeOwe);

    // Hands over gil that was already promised, so the app knows what the amount SHOULD be: it is
    // whatever that member is still owed. Adding to what-we-owe is what draws the obligation down.
    //
    // No PICKABLE kind has this any more — the tick-and-pay panel is the only way to settle up.
    // The flag stays because Fix can still reach the kind, and that is exactly the moment the
    // amount hint and the departed-but-owed roster entries matter most.
    public bool SettlesMemberDebt => !IsSplittable && AddTo == TreasuryAccounts.WeOwe;

    // Whether gil actually changes hands, rather than a promise being recorded. Derived from the
    // pair rather than declared, for the same reason RequiresMember is: a kind whose accounts and
    // whose flags disagree is not a state this table can reach.
    public bool MovesCashOnHand =>
        AddTo == TreasuryAccounts.GilOnHand || TakeFrom == TreasuryAccounts.GilOnHand;

    // Gil is coming IN on this kind. Gil out is the other half of MovesCashOnHand.
    public bool BringsCashIn => AddTo == TreasuryAccounts.GilOnHand;

    // A HOLDER is required exactly when gil moves, because "gil on hand" is not a bank balance — it
    // is the sum of what sits on people's mules, and gil that arrives on nobody's mule cannot be
    // found again. This is the same argument RequiresMember makes about the who-we-owe list, applied
    // to the one figure that had no names behind it at all.
    //
    // Owed-to-us and what-we-owe are deliberately NOT covered: nothing has changed hands yet, so
    // there is no mule to name. They pick one up when they are settled.
    public bool RequiresHolder => MovesCashOnHand;

    // What the holder box is CALLED, which has to flip with the direction: naming who received gil
    // is a different question from naming whose stack it came out of, and one label for both reads
    // as wrong half the time.
    public string HolderLabel =>
        BringsCashIn ? TreasuryLabels.WhoIsHoldingIt : TreasuryLabels.WhosePocketItLeft;
}

// THE catalog of what can happen. The Angular picker and the Razor Record form both render this
// list, so the two surfaces cannot offer different options.
//
// Nothing is ever REMOVED from here, only retired. Every row in the treasury stores the key it was
// recorded under, and LabelFor resolves it at render time — so deleting an entry from this table
// would silently relabel years of history with whatever category the entry happens to have landed
// in. The one-time conversion from the old revenue table also names six of these keys, and the
// shipped migration generates its SQL from them.
//
// Deliberately absent: "listed an item on the auction house". A listing is not a sale — there is
// no buyer, no agreed price and nothing owed to anyone — so it produces no treasury entry. An
// unsold item stays what it already is: an Inventory row with IsSold = false.
public static class TreasuryTransactionKinds
{
    // Keys, for the call sites that record a specific kind.
    public const string SoldAnItem = "SoldAnItem";
    public const string GotADonation = "GotADonation";
    public const string GotPaidForWork = "GotPaidForWork";
    public const string PaidGilToMember = "PaidGilToMember";
    public const string SplitGilAmongMembers = "SplitGilAmongMembers";
    public const string BoughtSomething = "BoughtSomething";
    public const string OtherMoneyIn = "OtherMoneyIn";
    public const string OtherMoneyOut = "OtherMoneyOut";
    public const string SomeoneOwesUsForWork = "SomeoneOwesUsForWork";
    public const string TheyPaidWhatTheyOwed = "TheyPaidWhatTheyOwed";
    public const string WeOweAMember = "WeOweAMember";
    public const string WeOweSeveralMembers = "WeOweSeveralMembers";
    public const string WePaidWhatWeOwed = "WePaidWhatWeOwed";
    public const string StartingGil = "StartingGil";
    public const string FoundExtraGil = "FoundExtraGil";
    public const string MissingGil = "MissingGil";

    // In picker order, action by action. Nothing reads this positionally — both front-ends group by
    // Action and the fuzz test draws at random — but keeping it in the order a user sees it is what
    // makes the table readable as the menu it describes.
    public static readonly IReadOnlyList<TreasuryTransactionKind> All = new[]
    {
        // ---- Gil In: gil on hand goes UP, and that is the whole of it -------------------------
        // ONE reason, so the picker asks nothing further and the form shows a box to type in. The
        // key is still OtherMoneyIn and the category is still "other gil in" — every hand-recorded
        // arrival lands there now, which is the point rather than a shortcoming. The sentence the
        // officer types IS the categorisation.
        new TreasuryTransactionKind(
            OtherMoneyIn, "Gil In", "Gil in",
            "Gil arrived. Say where it came from in your own words — a sale, a donation, a payout, "
                + "whatever it was.",
            TreasuryAccounts.GilOnHand, TreasuryAccounts.OtherMoneyIn,
            TreasuryTransactionActions.GilIn, ShowsMember: false,
            "This adds {0} gil to gil on hand."),

        // ---- Gil Out: gil on hand goes DOWN ---------------------------------------------------
        new TreasuryTransactionKind(
            OtherMoneyOut, "Gil Out", "Gil out",
            "Gil left the treasury. Say what it went on in your own words.",
            TreasuryAccounts.OtherMoneyOut, TreasuryAccounts.GilOnHand,
            TreasuryTransactionActions.GilOut, ShowsMember: false,
            "This takes {0} gil out of gil on hand."),

        // ---- What we owe: one action, one reason, one member or a dozen ------------------------
        // Splits what we owe rather than the gil-paid side, so each member's share is its own
        // obligation the tick-and-pay panel can settle one at a time, whenever that person is next
        // online. That is the whole reason this is the only surviving split.
        //
        // It is also now the ONLY way to record gil owed to a member, single or shared. A split of
        // one is not a special case — Allocate hands the whole amount to the one name picked, and
        // the ledger lines that come out are identical to the ones the retired WeOweAMember wrote.
        // Keeping a separate single-member kind bought nothing but a second button and a second
        // label for the same movement.
        new TreasuryTransactionKind(
            WeOweSeveralMembers, "What We Owe", "What we owe",
            "Gil the linkshell owes but has not handed over. Pick one member and the whole amount "
                + "is theirs; pick several and it is split evenly between them. Nobody is paid "
                + "until you tick them off the who-we-owe panel.",
            TreasuryAccounts.GilToMembers, TreasuryAccounts.WeOwe,
            TreasuryTransactionActions.WhatWeOwe, ShowsMember: true,
            "This records {0} gil we owe. Gil on hand does not change until you tick them off as paid.",
            SplitAccount: TreasuryAccounts.WeOwe),

        // ---- Owed to us: the balance nothing else can move -------------------------------------
        // ONE pickable reason, because clearing the debt is not something anyone types. The
        // balance sheet's owed-to-us list is tick-and-record, exactly like the who-we-owe list on
        // the other side, and ticking a name there writes the settle entry below.
        //
        // That symmetry is the whole design: both halves of the sheet are derived lists of names,
        // and both are settled by ticking one. Neither direction is a Gil In or Gil Out reason —
        // the first moves no gil at all, and typing the arrival as ordinary Gil In would count it
        // as new income and leave the debt standing forever.
        //
        // The party is named in free text rather than off the roster: whoever owes a linkshell gil
        // is usually not in it.
        new TreasuryTransactionKind(
            SomeoneOwesUsForWork, "Owed to us", "Someone owes us",
            "The work is finished but they have not paid yet. Gil on hand does not change. Tick "
                + "them off the balance sheet when the gil arrives.",
            TreasuryAccounts.OwedToUs, TreasuryAccounts.PaidWork,
            TreasuryTransactionActions.OwedToUs, ShowsMember: true,
            "This records {0} gil owed to us. Gil on hand does not change until they pay.",
            CounterpartyLabel: TreasuryLabels.WhoOwesUs),

        // Written by ticking a name off the owed-to-us list on the balance sheet, the only way that
        // debt is ever cleared. Unpickable but NOT retired: the app records it, and Fix has to be
        // able to reproduce one. The exact mirror of WePaidWhatWeOwed below.
        new TreasuryTransactionKind(
            TheyPaidWhatTheyOwed, "Owed to us — They paid up", "They paid what they owed",
            "Gil we were already owed has now arrived. Recorded for you when you tick them off the "
                + "balance sheet.",
            TreasuryAccounts.GilOnHand, TreasuryAccounts.OwedToUs,
            TreasuryTransactionActions.OwedToUs, ShowsMember: true,
            "This adds {0} gil to gil on hand and clears {0} gil that was owed to us.",
            IsPickable: false,
            CounterpartyLabel: TreasuryLabels.WhoPaidUs),

        // ---- We owe a member: superseded by What We Owe -------------------------------------------
        // Same two accounts, same direction, same panel it lands on — the only difference was that
        // this one took a typed name and What We Owe takes a picked list. Asking the officer which
        // of the two they wanted, before they had picked anybody, was the whole problem.
        //
        // Retired rather than deleted: entries recorded under it keep reading as what they were, and
        // Fix can still reach the kind so one of them can be corrected without being re-filed.
        new TreasuryTransactionKind(
            WeOweAMember, "We Owe a Member", "We owe a member",
            "They have earned it — a gil auction win, an agreed split — but nobody has handed it "
                + "over yet. They go on the who-we-owe panel until you tick them off as paid.",
            TreasuryAccounts.GilToMembers, TreasuryAccounts.WeOwe,
            TreasuryTransactionActions.OweAMember, ShowsMember: true,
            "This records {0} gil we owe one member. Gil on hand does not change until you tick "
                + "them off as paid.",
            IsPickable: false, IsRetired: true),

        // ---- Not in the picker: THE APP records these --------------------------------------------
        // Unpickable but NOT retired — every write endpoint must keep accepting them, because the
        // app itself still records them and a Fix has to be able to reproduce the movement.
        //
        // These are also why dropping the categories cost so little: the two arrivals worth naming
        // precisely are the two nobody was typing by hand anyway.

        // Written by ItemSaleRecorder when an item is marked sold out of the stockpile. It keeps a
        // category of its own and a label that says where it came from, because this one IS reliably
        // categorised: the app knows it was a stockpile item, so it can say so without asking.
        new TreasuryTransactionKind(
            SoldAnItem, "Gil In — Stockpile Item Sold", "Stockpile item sold",
            "Something from the linkshell's stockpile sold. Recorded for you when you mark the item "
                + "sold, so there is nothing to type here.",
            TreasuryAccounts.GilOnHand, TreasuryAccounts.ItemSales,
            TreasuryTransactionActions.GilIn, ShowsMember: false,
            "This adds {0} gil to gil on hand and records it as a stockpile item sale.",
            IsPickable: false),

        // Written when a gil auction closes and the winner is paid.
        new TreasuryTransactionKind(
            PaidGilToMember, "Gil Out — Paid a member", "Paid a member",
            "Gil handed to a member — recorded for you when a gil auction closes.",
            TreasuryAccounts.GilToMembers, TreasuryAccounts.GilOnHand,
            TreasuryTransactionActions.GilOut, ShowsMember: true,
            "This takes {0} gil out of gil on hand and records it as gil paid to a member.",
            IsPickable: false),

        // The "Gil on Hand" flow, after someone logs in and counts the mule.
        // Written by the tick-and-pay panel on the balance sheet, the only way to settle up.
        new TreasuryTransactionKind(
            WePaidWhatWeOwed, "Gil Out — Paid what we owed", "Paid what we owed",
            "Gil we had already promised has now been handed over.",
            TreasuryAccounts.WeOwe, TreasuryAccounts.GilOnHand,
            TreasuryTransactionActions.GilOut, ShowsMember: true,
            "This takes {0} gil out of gil on hand and clears {0} gil we owed.",
            IsPickable: false),

        // ---- Retired: superseded, and refused on the write path ----------------------------------
        // Still resolvable so their history reads, and still reachable from Fix so an entry recorded
        // under one can be corrected without being re-filed onto entirely different categories.

        // The three income and expense buckets an officer used to pick between. Superseded by a
        // sentence: "Gil In" plus "sold the Osode to Skid" says everything "Gil In — Sold an item"
        // said and more, and it says it in the transactions list rather than in a menu nobody reads.
        new TreasuryTransactionKind(
            GotADonation, "Gil In — A donation", "A donation",
            "A member handed gil to the linkshell and is not expecting it back.",
            TreasuryAccounts.GilOnHand, TreasuryAccounts.MemberDonations,
            TreasuryTransactionActions.GilIn, ShowsMember: true,
            "This adds {0} gil to gil on hand and records it as a member donation.",
            IsPickable: false, IsRetired: true),

        new TreasuryTransactionKind(
            GotPaidForWork, "Gil In — Paid for work we did", "Paid for work we did",
            "The linkshell was paid for a run, an escort, a craft — anything done for someone else.",
            TreasuryAccounts.GilOnHand, TreasuryAccounts.PaidWork,
            TreasuryTransactionActions.GilIn, ShowsMember: false,
            "This adds {0} gil to gil on hand and records it as paid work.",
            IsPickable: false, IsRetired: true),

        new TreasuryTransactionKind(
            BoughtSomething, "Gil Out — Bought something", "Bought something",
            "Food, medicine, entry items, gear the linkshell paid for out of the treasury.",
            TreasuryAccounts.Supplies, TreasuryAccounts.GilOnHand,
            TreasuryTransactionActions.GilOut, ShowsMember: false,
            "This takes {0} gil out of gil on hand and records it as supplies.",
            IsPickable: false, IsRetired: true),

        // Superseded by What We Owe, which records the same payout as obligations instead. The
        // difference was one nobody thought about at the time of recording, and getting it wrong
        // meant gil leaving the treasury that had not actually been handed over.
        //
        // It keeps the Split Gil action, which is now history-only. Re-filing it under What We Owe
        // would have been a lie in both directions: this one DOES move gil on hand, and its label
        // would have had to be re-worded to match an action it never belonged to.
        new TreasuryTransactionKind(
            SplitGilAmongMembers, "Split Gil — Paid now", "Split gil (paid now)",
            "One lump sum shared out evenly. Pick everyone getting a share and the gil is divided "
                + "between them.",
            TreasuryAccounts.GilToMembers, TreasuryAccounts.GilOnHand,
            TreasuryTransactionActions.SplitGil, ShowsMember: true,
            "This takes {0} gil out of gil on hand and shares it evenly between the members you picked.",
            IsPickable: false, IsRetired: true,
            SplitAccount: TreasuryAccounts.GilToMembers),

        // ---- Setup: REMOVED, and kept only so its history reads ---------------------------------
        // "Starting Gil" and "Gil on Hand" are gone. Both were one-off ceremonies with their own
        // buttons, their own endpoints and their own arithmetic, and both do exactly what a Gil In
        // or Gil Out with a sentence in the reason box does: move gil on hand and say why. A
        // linkshell adopting the app records "Gil In — what we already had", and a linkshell that
        // counted the mule and found a gap records the gap the same way.
        //
        // Retired rather than deleted, because entries already recorded under them must keep
        // reading as what they were, and Fix must still be able to reproduce one.
        new TreasuryTransactionKind(
            StartingGil, "Setup — Starting gil", "Set our starting gil",
            "The gil the linkshell already had before it started tracking any of this.",
            TreasuryAccounts.GilOnHand, TreasuryAccounts.StartingBalance,
            TreasuryTransactionActions.Setup, ShowsMember: false,
            "This sets gil on hand to include {0} gil the linkshell already had.",
            EntryKind: JournalEntryKinds.Opening, IsPickable: false, IsRetired: true),

        new TreasuryTransactionKind(
            FoundExtraGil, "Setup — Counted more than the books", "Counted more than the books",
            "The difference found when checking gil on hand.",
            TreasuryAccounts.GilOnHand, TreasuryAccounts.BalanceAdjustment,
            TreasuryTransactionActions.Setup, ShowsMember: false,
            "This adds the {0} gil the books were missing.",
            EntryKind: JournalEntryKinds.Adjustment, IsPickable: false, IsRetired: true),

        new TreasuryTransactionKind(
            MissingGil, "Setup — Counted less than the books", "Counted less than the books",
            "The difference found when checking gil on hand.",
            TreasuryAccounts.BalanceAdjustment, TreasuryAccounts.GilOnHand,
            TreasuryTransactionActions.Setup, ShowsMember: false,
            "This removes {0} gil the books were counting but the linkshell does not have.",
            EntryKind: JournalEntryKinds.Adjustment, IsPickable: false, IsRetired: true),
    };

    private static readonly Dictionary<string, TreasuryTransactionKind> ByKey =
        All.ToDictionary(kind => kind.Key, StringComparer.OrdinalIgnoreCase);

    public static TreasuryTransactionKind? Find(string? key) =>
        key is not null && ByKey.TryGetValue(key, out var kind) ? kind : null;

    // What the officer may choose. Excludes the ones the app picks for them and the retired ones.
    public static IEnumerable<TreasuryTransactionKind> Pickable() => All.Where(kind => kind.IsPickable);

    // Pickable(), PLUS the kind an entry is already recorded under.
    //
    // This is what Fix and Edit-draft must offer. Given only Pickable(), a form for an entry whose
    // kind is not on the menu renders a select with no matching option, the browser quietly selects
    // the FIRST one, and saving re-files the entry onto two entirely different categories — a
    // gil-count adjustment becomes an item sale. The officer is never told; the amount is right, so
    // nothing looks wrong.
    //
    // The current kind is appended rather than inserted so the everyday reasons keep their order.
    public static IEnumerable<TreasuryTransactionKind> PickableWith(string? currentKey)
    {
        var current = Find(currentKey);
        if (current is null || current.IsPickable)
        {
            return Pickable();
        }
        return Pickable().Append(current);
    }

    // The label for an entry, falling back to the category name for converted legacy rows that
    // predate the picker.
    public static string LabelFor(string? key, string? fallback) =>
        Find(key)?.Label ?? (string.IsNullOrWhiteSpace(fallback) ? "Recorded" : fallback);

    // The reasons on offer under one action, in catalog order.
    public static IEnumerable<TreasuryTransactionKind> ReasonsFor(string? action) =>
        Pickable().Where(kind => string.Equals(kind.Action, action, StringComparison.OrdinalIgnoreCase));
}
