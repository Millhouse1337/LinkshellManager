using LinkshellManagerDiscordApp.Services;
using Xunit;

namespace LinkshellManager.Tests;

// Pins the Hybrid-DKP money formula that was previously copy-pasted across the
// ToD, event and loot-edit flows. The debit/refund round-trip is the key
// invariant: refunding removed loot must restore the pre-spend balance.
public class LootDkpCalculatorTests
{
    [Theory]
    [InlineData(100, 50, 50)]    // 50% of 100
    [InlineData(100, 0, 0)]      // 0% spends nothing
    [InlineData(0, 50, 0)]       // nothing to spend
    [InlineData(200, 25, 50)]    // 25% of 200
    [InlineData(33.333, 10, 3.33)] // rounds to 2dp
    public void ComputeHybridDebit_SpendsPercentOfBalance(double balance, double percent, double expected)
    {
        Assert.Equal(expected, LootDkpCalculator.ComputeHybridDebit(balance, percent), precision: 2);
    }

    [Theory]
    [InlineData(50, 50, 50)]     // remaining 50 after a 50% spend -> spent was 50
    [InlineData(75, 25, 25)]     // remaining 75 after a 25% spend -> spent was 25
    [InlineData(0, 40, 0)]
    public void ComputeHybridRefund_ReconstructsSpentAmount(double remainingBalance, double percent, double expected)
    {
        Assert.Equal(expected, LootDkpCalculator.ComputeHybridRefund(remainingBalance, percent), precision: 2);
    }

    [Theory]
    [InlineData(200, 40)]
    [InlineData(100, 50)]
    [InlineData(1000, 10)]
    [InlineData(500, 33)]
    public void DebitThenRefund_RestoresOriginalBalance(double startingBalance, double percent)
    {
        var debit = LootDkpCalculator.ComputeHybridDebit(startingBalance, percent);
        var remaining = startingBalance - debit;

        var refund = LootDkpCalculator.ComputeHybridRefund(remaining, percent);

        // Refunding the removed loot returns (within rounding) the exact DKP spent,
        // so remaining + refund reconstructs the starting balance.
        Assert.Equal(startingBalance, remaining + refund, precision: 2);
    }
}
