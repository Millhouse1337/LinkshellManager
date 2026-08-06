namespace LinkshellManagerDiscordApp.Models;

// What kind of thing a treasury category is, DERIVED from its number so the two can never
// disagree. Storing both and reconciling later is how RevenueEntry.EntryType ended up
// holding seven values from two incompatible vocabularies.
//
//   1000-1999  things the linkshell HAS      (gil on hand, gil owed to us)
//   2000-2999  things the linkshell OWES
//   3000-3999  what it is worth              (the residual, plus the adoption balance)
//   4000-4999  gil coming IN
//   5000-5999  gil going OUT
//
// String consts rather than an enum: the repo has no finance enums and its idiom for a
// controlled vocabulary is a static class of consts plus a normalizer — see LinkshellRanks
// and DkpPool. It also keeps the data migration a plain text comparison.
public static class LedgerAccountClasses
{
    public const string Holds = "Holds";        // asset
    public const string Owes = "Owes";          // liability
    public const string Worth = "Worth";        // net assets / equity
    public const string MoneyIn = "MoneyIn";    // revenue
    public const string MoneyOut = "MoneyOut";  // expense

    public const int MinAccountNumber = 1000;
    public const int MaxAccountNumber = 5999;

    public static string? FromNumber(int accountNumber) => accountNumber switch
    {
        >= 1000 and <= 1999 => Holds,
        >= 2000 and <= 2999 => Owes,
        >= 3000 and <= 3999 => Worth,
        >= 4000 and <= 4999 => MoneyIn,
        >= 5000 and <= 5999 => MoneyOut,
        _ => null,
    };

    // +1 = the category grows when gil is added to it (a debit), -1 = it grows when gil is
    // taken away (a credit). Multiply a stored signed amount by this to PRESENT it, so a
    // money-in category holding -4,000,000 presents as 4,000,000 of gil received.
    //
    // This is what gross totals must key on — NOT the sign of the amount. Reversing a sale
    // puts a positive amount on a money-in category, and that has to REDUCE gil received
    // rather than register as gil paid out.
    public static int NormalSign(string? accountClass) => accountClass switch
    {
        Holds or MoneyOut => 1,
        _ => -1,
    };

    public static int NormalSignForNumber(int accountNumber) => NormalSign(FromNumber(accountNumber));

    // Does this category belong on the "what we have / owe" view rather than the
    // "gil in and out over a period" view? Holdings carry forward; flows do not.
    public static bool IsHolding(string? accountClass) =>
        accountClass is Holds or Owes or Worth;

    public static bool IsFlow(string? accountClass) =>
        accountClass is MoneyIn or MoneyOut;
}
