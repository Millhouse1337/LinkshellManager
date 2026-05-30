using LinkshellManagerDiscordApp.Services;
using Xunit;

namespace LinkshellManager.Tests;

// Pins the Hybrid-DKP loot formula. Results now snap to the linkshell's
// configured rounding increment (0.25 / 0.5) — the same grid every other DKP
// value uses — instead of 2 decimal places. The debit/refund round-trip is the
// key invariant: refunding removed loot must restore the pre-spend balance.
public class LootDkpCalculatorTests
{
    [Theory]
    [InlineData(100, 50, 0.25, 50)]      // exactly on grid
    [InlineData(100, 0, 0.25, 0)]        // 0% spends nothing
    [InlineData(0, 50, 0.25, 0)]         // nothing to spend
    [InlineData(33, 50, 0.25, 16.5)]     // 16.5 already on the 0.25 grid
    [InlineData(33.33, 10, 0.25, 3.25)]  // 3.333 -> nearest 0.25
    [InlineData(33.33, 10, 0.5, 3.5)]    // 3.333 -> nearest 0.5
    public void ComputeHybridDebit_SnapsToIncrement(double balance, double percent, double step, double expected)
    {
        Assert.Equal(expected, LootDkpCalculator.ComputeHybridDebit(balance, percent, step), precision: 5);
    }

    [Theory]
    [InlineData(50, 50, 0.25, 50)]   // remaining 50 after a 50% spend -> spent was 50
    [InlineData(75, 25, 0.5, 25)]    // remaining 75 after a 25% spend -> spent was 25
    [InlineData(0, 40, 0.25, 0)]
    public void ComputeHybridRefund_SnapsToIncrement(double remaining, double percent, double step, double expected)
    {
        Assert.Equal(expected, LootDkpCalculator.ComputeHybridRefund(remaining, percent, step), precision: 5);
    }

    [Theory]
    [InlineData(200, 40, 0.25)]
    [InlineData(100, 50, 0.5)]
    [InlineData(1000, 10, 0.25)]
    public void DebitThenRefund_RestoresOriginalBalance(double startingBalance, double percent, double step)
    {
        var debit = LootDkpCalculator.ComputeHybridDebit(startingBalance, percent, step);
        var remaining = startingBalance - debit;

        var refund = LootDkpCalculator.ComputeHybridRefund(remaining, percent, step);

        // Refunding the removed loot returns the exact DKP spent, so
        // remaining + refund reconstructs the starting balance.
        Assert.Equal(startingBalance, remaining + refund, precision: 2);
    }
}
