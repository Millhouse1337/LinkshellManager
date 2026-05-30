namespace LinkshellManagerDiscordApp.Services;

/// <summary>
/// Single source of truth for the "Hybrid" DKP loot formula, where a winner
/// spends a <em>percentage of their current balance</em> rather than a fixed
/// amount. Previously this arithmetic was copy-pasted across the ToD, event and
/// loot-edit flows; centralizing it keeps the money math consistent and tested.
///
/// All amounts are rounded to 2 decimal places to match the stored ledger.
/// Callers remain responsible for clamping the percent to [0, 100] and flooring
/// the balance at 0 before calling (every call site already does), and for the
/// sign convention (a debit is applied as a negative ledger amount).
/// </summary>
public static class LootDkpCalculator
{
    /// <summary>
    /// The DKP spent when winning Hybrid loot: <c>percent</c>% of the winner's
    /// <c>currentBalance</c>, as a positive amount.
    /// </summary>
    public static double ComputeHybridDebit(double currentBalance, double percent)
        => Math.Round(currentBalance * percent / 100d, 2);

    /// <summary>
    /// The DKP to refund when previously-won Hybrid loot is removed. Because the
    /// original debit took <c>percent</c>% of the balance, the post-debit balance
    /// is <c>(100 - percent)%</c> of the original; this reconstructs the spent
    /// amount from the <em>current</em> balance so adding it back restores the
    /// pre-spend total. Callers must guard <c>percent &gt;= 100</c> (which has no
    /// finite inverse) and refund the exact recorded amount in that case.
    /// </summary>
    public static double ComputeHybridRefund(double currentBalance, double percent)
        => Math.Round(currentBalance * percent / (100d - percent), 2);
}
