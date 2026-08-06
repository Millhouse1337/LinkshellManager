namespace LinkshellManagerDiscordApp.Models;

// The seeded category numbers, referenced symbolically everywhere so no call site hardcodes a
// bare 4100. Numbers are internal identity only — LedgerAccountClasses derives behaviour from
// them, and users never see one.
public static class TreasuryAccounts
{
    // --- 1000s: what the linkshell has ---
    public const int GilOnHand = 1000;
    public const int OwedToUs = 1200;

    // --- 2000s: what it owes ---
    public const int WeOwe = 2000;

    // --- 3000s: what it is worth ---
    // Never posted to: worth is the residual of holdings minus obligations. IsPostable = false.
    public const int NetWorth = 3000;
    // The gil a linkshell already had when it adopted LSManager. Postable exactly once, and the
    // reason an adopting linkshell does not have to book its existing treasury as gil received.
    public const int StartingBalance = 3900;

    // --- 4000s: gil coming in ---
    public const int MemberDonations = 4000;
    public const int ItemSales = 4100;
    public const int PaidWork = 4200;
    public const int OtherMoneyIn = 4900;

    // --- 5000s: gil going out ---
    public const int GilToMembers = 5000;
    public const int Supplies = 5100;
    public const int OtherMoneyOut = 5900;
    // Where the gap goes when someone counts the mule and it disagrees with the books.
    public const int BalanceAdjustment = 5950;
}
