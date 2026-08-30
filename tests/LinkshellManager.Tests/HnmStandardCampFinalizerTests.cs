using LinkshellManagerDiscordApp.Services;
using Xunit;

namespace LinkshellManager.Tests;

// The Standard-mode HNM earn formula (the pure core of HnmStandardCampFinalizer). Unlike Manual Check In there
// is no per-window rate: presence comes from the addon's window SCANS and the payout is exactly the
// four configurable bonuses — open (scanned in window 1), close (scanned in the camp's closing
// window), plus claim/kill for the camp's outcome — snapped to the linkshell's rounding grid.
public class HnmStandardCampFinalizerTests
{
    private const double Quarter = DkpRounding.QuarterStep; // 0.25

    [Fact]
    public void ComputeDkp_ScannedOpenAndClose_PaysBoth()
    {
        // There from the first scan through the closing one: 0.5 + 0.5 = 1.0.
        var dkp = HnmStandardCampFinalizer.ComputeDkp(
            openBonus: 0.5, closeBonus: 0.5, claimBonus: 0, killBonus: 0, step: Quarter);

        Assert.Equal(1.0, dkp, precision: 3);
    }

    [Fact]
    public void ComputeDkp_LateArrival_PaysCloseOnly()
    {
        // Missed the window-1 scan, caught the close — the caller passes 0 for the open bonus.
        var dkp = HnmStandardCampFinalizer.ComputeDkp(
            openBonus: 0, closeBonus: 0.5, claimBonus: 0, killBonus: 0, step: Quarter);

        Assert.Equal(0.5, dkp, precision: 3);
    }

    [Fact]
    public void ComputeDkp_AddsClaimAndKillBonuses()
    {
        // Open 0.5 + close 0.5 + claim 0.25 + kill 0.75 = 2.0.
        var dkp = HnmStandardCampFinalizer.ComputeDkp(
            openBonus: 0.5, closeBonus: 0.5, claimBonus: 0.25, killBonus: 0.75, step: Quarter);

        Assert.Equal(2.0, dkp, precision: 3);
    }

    [Fact]
    public void ComputeDkp_NothingQualified_IsZero()
    {
        // Scanned mid-camp only, camp neither claimed nor killed — nothing to pay. The finalizer
        // skips these members entirely rather than writing a 0-DKP history row.
        var dkp = HnmStandardCampFinalizer.ComputeDkp(
            openBonus: 0, closeBonus: 0, claimBonus: 0, killBonus: 0, step: Quarter);

        Assert.Equal(0, dkp, precision: 3);
    }

    [Fact]
    public void ComputeDkp_SnapsToTheRoundingGrid()
    {
        // 0.1 + 0.1 = 0.2 → snapped to the nearest 0.25 = 0.25.
        var dkp = HnmStandardCampFinalizer.ComputeDkp(
            openBonus: 0.1, closeBonus: 0.1, claimBonus: 0, killBonus: 0, step: Quarter);

        Assert.Equal(0.25, dkp, precision: 3);
    }

    [Fact]
    public void ComputeDkp_HalfStepLinkshell_SnapsToHalves()
    {
        // 0.5 + 0.5 + 0.4 = 1.4 → snapped to the nearest 0.5 = 1.5.
        var dkp = HnmStandardCampFinalizer.ComputeDkp(
            openBonus: 0.5, closeBonus: 0.5, claimBonus: 0.4, killBonus: 0, step: DkpRounding.HalfStep);

        Assert.Equal(1.5, dkp, precision: 3);
    }
}

// Which window closes the camp. The pop window when the officer actually scanned it; otherwise the
// last window that HAS a scan, so an officer who stopped scanning early still closes out the people
// in that final scan.
public class HnmStandardCloseWindowTests
{
    [Fact]
    public void ResolveCloseWindow_PopWindowScanned_UsesPopWindow()
    {
        var posted = new[] { 1, 2, 3, 4 };
        Assert.Equal(4, HnmStandardCampFinalizer.ResolveCloseWindow(posted, popWindow: 4));
    }

    [Fact]
    public void ResolveCloseWindow_PopWindowNotScanned_FallsBackToLastScan()
    {
        // Scanning stopped at window 3 but the monster popped on 7 — window 3 is the close.
        var posted = new[] { 1, 2, 3 };
        Assert.Equal(3, HnmStandardCampFinalizer.ResolveCloseWindow(posted, popWindow: 7));
    }

    [Fact]
    public void ResolveCloseWindow_ScansOutOfOrder_UsesHighest()
    {
        // Posts can arrive in any order (a corrected re-post of an earlier window).
        var posted = new[] { 5, 1, 3 };
        Assert.Equal(5, HnmStandardCampFinalizer.ResolveCloseWindow(posted, popWindow: 9));
    }

    [Fact]
    public void ResolveCloseWindow_NoScans_IsZero()
    {
        // Nobody was ever scanned — there is no open and no close to pay.
        Assert.Equal(0, HnmStandardCampFinalizer.ResolveCloseWindow(Array.Empty<int>(), popWindow: 7));
    }

    [Fact]
    public void ResolveCloseWindow_SingleScan_IsBothOpenAndClose()
    {
        // One scan, taken at window 1: that window is simultaneously the open and the close.
        //
        // Which is now inert at payout — WindowValue prices sequence 1 as the OPEN whether or not
        // it also resolves as the close, so this answer costs nobody anything. It used to pay both,
        // which is how posting the opening window on a 1 open / 1 close linkshell read as 2 DKP.
        var posted = new[] { 1 };
        Assert.Equal(1, HnmStandardCampFinalizer.ResolveCloseWindow(posted, popWindow: 1));
    }
}
