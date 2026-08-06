namespace LinkshellManagerDiscordApp.Models;

// One thing that can happen to a linkshell's gil, in the officer's words.
//
// AddTo/TakeFrom are the two halves of the movement. They are FIXED per kind, which is the whole
// safety property: an officer picks "Sold an item" and the pair is correct by construction. They
// are never asked which category to add to and which to take from, and no endpoint accepts a
// free-form pair.
public sealed record TreasuryTransactionKind(
    string Key,
    string Label,
    string Help,
    int AddTo,
    int TakeFrom,
    string Group,
    bool ShowsMember,
    // "{0}" is the formatted amount.
    string PreviewTemplate,
    string EntryKind = JournalEntryKinds.Standard,
    bool IsPickable = true,
    // Which of this kind's two categories gets one line PER MEMBER instead of one line for the
    // whole amount. Must be AddTo or TakeFrom; null means this kind is never split.
    //
    // The other category stays whole and carries no member, because an aggregate is not
    // attributable to one person. Which side splits is per-kind rather than a rule, because the
    // right side genuinely differs: paying members out splits the gil-paid category, whereas
    // owing them splits what we owe — the side "We paid a member what we owed" later draws down.
    int? SplitAccount = null)
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
    public bool SettlesMemberDebt => !IsSplittable && AddTo == TreasuryAccounts.WeOwe;
}

// THE catalog of what can happen. The Angular "What happened?" select and the Razor Record form
// both render this list, so the two surfaces cannot offer different options.
//
// Deliberately absent: "listed an item on the auction house". A listing is not a sale — there is
// no buyer, no agreed price and nothing owed to anyone — so it produces no treasury entry. An
// unsold item stays what it already is: an Inventory row with IsSold = false.
public static class TreasuryTransactionKinds
{
    public const string GroupCommon = "Common";
    public const string GroupOwed = "Owed";
    public const string GroupSetup = "Setup";

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

    public static readonly IReadOnlyList<TreasuryTransactionKind> All = new[]
    {
        // --- the everyday ones ---
        new TreasuryTransactionKind(
            SoldAnItem, "Sold an item",
            "Something from the linkshell's stash sold on the auction house or to a player.",
            TreasuryAccounts.GilOnHand, TreasuryAccounts.ItemSales,
            GroupCommon, ShowsMember: false,
            "This adds {0} gil to gil on hand and records it as an item sale."),

        new TreasuryTransactionKind(
            GotADonation, "Got a donation",
            "A member handed gil to the linkshell and is not expecting it back.",
            TreasuryAccounts.GilOnHand, TreasuryAccounts.MemberDonations,
            GroupCommon, ShowsMember: true,
            "This adds {0} gil to gil on hand and records it as a member donation."),

        new TreasuryTransactionKind(
            GotPaidForWork, "Got paid for work",
            "The linkshell was paid for a run, an escort, a craft — anything done for someone else.",
            TreasuryAccounts.GilOnHand, TreasuryAccounts.PaidWork,
            GroupCommon, ShowsMember: false,
            "This adds {0} gil to gil on hand and records it as paid work."),

        new TreasuryTransactionKind(
            PaidGilToMember, "Paid gil to a member",
            "Gil handed out to a member — a gil auction win, a split, a reimbursement.",
            TreasuryAccounts.GilToMembers, TreasuryAccounts.GilOnHand,
            GroupCommon, ShowsMember: true,
            "This takes {0} gil out of gil on hand and records it as gil paid to a member."),

        new TreasuryTransactionKind(
            SplitGilAmongMembers, "Split gil among several members",
            "One lump sum shared out evenly. Pick everyone getting a share and the gil is divided "
                + "between them.",
            TreasuryAccounts.GilToMembers, TreasuryAccounts.GilOnHand,
            GroupCommon, ShowsMember: true,
            "This takes {0} gil out of gil on hand and shares it evenly between the members you picked.",
            SplitAccount: TreasuryAccounts.GilToMembers),

        new TreasuryTransactionKind(
            BoughtSomething, "Bought something for the linkshell",
            "Food, medicine, entry items, gear the linkshell paid for out of the treasury.",
            TreasuryAccounts.Supplies, TreasuryAccounts.GilOnHand,
            GroupCommon, ShowsMember: false,
            "This takes {0} gil out of gil on hand and records it as supplies."),

        new TreasuryTransactionKind(
            OtherMoneyIn, "Gil Received — Other",
            "Use this when nothing above fits. You can always change the category later.",
            TreasuryAccounts.GilOnHand, TreasuryAccounts.OtherMoneyIn,
            GroupCommon, ShowsMember: false,
            "This adds {0} gil to gil on hand and files it under other gil in."),

        new TreasuryTransactionKind(
            OtherMoneyOut, "Gil Paid — Other",
            "Use this when nothing above fits. You can always change the category later.",
            TreasuryAccounts.OtherMoneyOut, TreasuryAccounts.GilOnHand,
            GroupCommon, ShowsMember: false,
            "This takes {0} gil out of gil on hand and files it under other gil out."),

        // --- gil promised but not yet moved ---
        // These record a claim or an obligation WITHOUT touching gil on hand, then a second
        // entry moves the gil when it actually changes hands. Nothing here is required: a
        // linkshell that only records gil when it moves never touches this group.
        new TreasuryTransactionKind(
            SomeoneOwesUsForWork, "Someone owes us for work we did",
            "The work is finished but they have not paid yet. Gil on hand does not change.",
            TreasuryAccounts.OwedToUs, TreasuryAccounts.PaidWork,
            GroupOwed, ShowsMember: true,
            "This records {0} gil owed to us. Gil on hand does not change until they pay."),

        new TreasuryTransactionKind(
            TheyPaidWhatTheyOwed, "They paid what they owed",
            "Gil we were already owed has now arrived.",
            TreasuryAccounts.GilOnHand, TreasuryAccounts.OwedToUs,
            GroupOwed, ShowsMember: true,
            "This adds {0} gil to gil on hand and clears {0} gil that was owed to us."),

        new TreasuryTransactionKind(
            WeOweAMember, "Gil Owed to Member",
            "They have earned it — a gil auction win, an agreed split — but nobody has handed it over yet.",
            TreasuryAccounts.GilToMembers, TreasuryAccounts.WeOwe,
            GroupOwed, ShowsMember: true,
            "This records {0} gil we owe. Gil on hand does not change until it is handed over."),

        // Splits what we owe rather than the gil-paid side, so each member's share is its own
        // obligation that "We paid a member what we owed" can settle one at a time.
        new TreasuryTransactionKind(
            WeOweSeveralMembers, "We owe several members a split",
            "They have earned a share of one lump sum, but nobody has handed the gil over yet.",
            TreasuryAccounts.GilToMembers, TreasuryAccounts.WeOwe,
            GroupOwed, ShowsMember: true,
            "This records {0} gil we owe, shared evenly between the members you picked. "
                + "Gil on hand does not change until it is handed over.",
            SplitAccount: TreasuryAccounts.WeOwe),

        new TreasuryTransactionKind(
            WePaidWhatWeOwed, "We paid a member what we owed",
            "Gil we had already promised has now been handed over.",
            TreasuryAccounts.WeOwe, TreasuryAccounts.GilOnHand,
            GroupOwed, ShowsMember: true,
            "This takes {0} gil out of gil on hand and clears {0} gil we owed."),

        // --- one-time setup ---
        new TreasuryTransactionKind(
            StartingGil, "Set our starting gil",
            "The gil the linkshell already had before it started tracking any of this. "
                + "Record it once, so it does not look like gil the linkshell earned.",
            TreasuryAccounts.GilOnHand, TreasuryAccounts.StartingBalance,
            GroupSetup, ShowsMember: false,
            "This sets gil on hand to include {0} gil the linkshell already had.",
            EntryKind: JournalEntryKinds.Opening),

        // --- not offered in the picker: the app decides the direction ---
        // Recorded by the "Check gil on hand" flow after someone logs in and counts the mule.
        new TreasuryTransactionKind(
            FoundExtraGil, "Counted more gil than the books showed",
            "The difference found when checking gil on hand.",
            TreasuryAccounts.GilOnHand, TreasuryAccounts.BalanceAdjustment,
            GroupSetup, ShowsMember: false,
            "This adds the {0} gil the books were missing.",
            EntryKind: JournalEntryKinds.Adjustment, IsPickable: false),

        new TreasuryTransactionKind(
            MissingGil, "Counted less gil than the books showed",
            "The difference found when checking gil on hand.",
            TreasuryAccounts.BalanceAdjustment, TreasuryAccounts.GilOnHand,
            GroupSetup, ShowsMember: false,
            "This removes {0} gil the books were counting but the linkshell does not have.",
            EntryKind: JournalEntryKinds.Adjustment, IsPickable: false),
    };

    private static readonly Dictionary<string, TreasuryTransactionKind> ByKey =
        All.ToDictionary(kind => kind.Key, StringComparer.OrdinalIgnoreCase);

    public static TreasuryTransactionKind? Find(string? key) =>
        key is not null && ByKey.TryGetValue(key, out var kind) ? kind : null;

    // What the officer may choose. Excludes the two the app picks for them.
    public static IEnumerable<TreasuryTransactionKind> Pickable() => All.Where(kind => kind.IsPickable);

    // The label for an entry, falling back to the category name for converted legacy rows that
    // predate the picker.
    public static string LabelFor(string? key, string? fallback) =>
        Find(key)?.Label ?? (string.IsNullOrWhiteSpace(fallback) ? "Recorded" : fallback);
}
