using LinkshellManagerDiscordApp.Services;
using Xunit;

namespace LinkshellManager.Tests;

// The one invariant the whole DKP-pool feature rests on:
//
//     SUM(pool balances) == AppUserLinkshell.LinkshellDkp
//
// Pool balances are DERIVED from the ledger, with the default pool absorbing whatever the other
// pools don't claim (the "residual"). That makes the sum exact by construction — for ANY mapping,
// which is precisely why remapping event types can move DKP between pools but can never change a
// member's total.
public class DkpPoolBalanceServiceTests
{
    private const int Main = 1;      // the default pool
    private const int Endgame = 2;
    private const int Raids = 3;

    private static IReadOnlyList<int> AllPools => new[] { Main, Endgame, Raids };

    [Fact]
    public void Project_SinglePool_PutsEverythingInTheDefault()
    {
        // The state every linkshell is in until an officer creates a second pool: no row carries a
        // pool id, so the default has to hold the whole balance or the app would silently change
        // behaviour on the day pools shipped.
        var rows = new[]
        {
            new LedgerPoolRow(1, null, 15),
            new LedgerPoolRow(2, null, 20),
            new LedgerPoolRow(3, null, -5),
        };

        var byPool = DkpPoolBalanceService.Project(30, rows, epochLedgerId: 0, defaultPoolId: Main, new[] { Main });

        Assert.Equal(30, byPool[Main]);
    }

    [Fact]
    public void Project_PooledEventTypes_SumToTheMemberTotal()
    {
        // The user's acceptance test: Sky 15 + Sea 20 + Dynamis 5, all mapped into "Endgame".
        var rows = new[]
        {
            new LedgerPoolRow(1, Endgame, 15),
            new LedgerPoolRow(2, Endgame, 20),
            new LedgerPoolRow(3, Endgame, 5),
        };

        var byPool = DkpPoolBalanceService.Project(40, rows, epochLedgerId: 0, defaultPoolId: Main, AllPools);

        Assert.Equal(40, byPool[Endgame]);   // 40 spendable on any Sky/Sea/Dynamis event
        Assert.Equal(0, byPool[Main]);
        Assert.Equal(40, byPool.Values.Sum());
    }

    [Fact]
    public void Project_Remap_MovesDkpBetweenPools_ButNeverChangesTheTotal()
    {
        var before = new[]
        {
            new LedgerPoolRow(1, Endgame, 15),   // Sky
            new LedgerPoolRow(2, Endgame, 20),   // Sea
            new LedgerPoolRow(3, Endgame, 5),    // Dynamis
        };
        // The officer moves Sea into its own pool. A remap only re-stamps the SAME rows, so this is
        // exactly what the re-stamped ledger looks like.
        var after = new[]
        {
            new LedgerPoolRow(1, Endgame, 15),
            new LedgerPoolRow(2, Raids, 20),
            new LedgerPoolRow(3, Endgame, 5),
        };

        var poolsBefore = DkpPoolBalanceService.Project(40, before, 0, Main, AllPools);
        var poolsAfter = DkpPoolBalanceService.Project(40, after, 0, Main, AllPools);

        Assert.Equal(40, poolsBefore[Endgame]);
        Assert.Equal(20, poolsAfter[Endgame]);
        Assert.Equal(20, poolsAfter[Raids]);
        // The whole point: the split changed, the total did not.
        Assert.Equal(poolsBefore.Values.Sum(), poolsAfter.Values.Sum());
    }

    [Fact]
    public void Project_DriftedBalance_IsAbsorbedByTheDefaultPool()
    {
        // LinkshellDkp is a cache that predates pools and can disagree with the ledger for
        // historical reasons (imports, merges, old bugs). The residual has to swallow the
        // difference, or the sum would stop matching the balance and every downstream guard would
        // be computing against a number the member doesn't actually have.
        var rows = new[] { new LedgerPoolRow(1, Endgame, 10) };

        var byPool = DkpPoolBalanceService.Project(100, rows, epochLedgerId: 0, defaultPoolId: Main, AllPools);

        Assert.Equal(10, byPool[Endgame]);
        Assert.Equal(90, byPool[Main]);        // the unexplained 90 lands in the default
        Assert.Equal(100, byPool.Values.Sum());
    }

    [Fact]
    public void Project_RowsAtOrBelowTheEpoch_AreNotAttributed()
    {
        // A kicked member's ledger rows survive the membership delete. Re-inviting them stamps a new
        // epoch, so their pre-kick earns must NOT resurrect as spendable pool DKP — their balance is
        // back to 0 and the pools have to agree.
        var rows = new[]
        {
            new LedgerPoolRow(1, Endgame, 500),   // earned before the kick
            new LedgerPoolRow(2, Endgame, 500),   // ditto
        };

        var byPool = DkpPoolBalanceService.Project(0, rows, epochLedgerId: 2, defaultPoolId: Main, AllPools);

        Assert.Equal(0, byPool[Endgame]);
        Assert.Equal(0, byPool[Main]);
        Assert.Equal(0, byPool.Values.Sum());
    }

    [Fact]
    public void Project_SpendingFromAPool_LeavesTheRestIntact()
    {
        var rows = new[]
        {
            new LedgerPoolRow(1, Endgame, 40),    // earned
            new LedgerPoolRow(2, Endgame, -30),   // a 30-DKP item won on a Sky event
            new LedgerPoolRow(3, Raids, 25),
        };

        var byPool = DkpPoolBalanceService.Project(35, rows, epochLedgerId: 0, defaultPoolId: Main, AllPools);

        Assert.Equal(10, byPool[Endgame]);
        Assert.Equal(25, byPool[Raids]);
        Assert.Equal(35, byPool.Values.Sum());
    }
}
