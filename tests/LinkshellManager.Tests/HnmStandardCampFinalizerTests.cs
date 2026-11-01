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

// Which window closes the camp: the one an officer ticked, or on a named Open/Close camp its
// latest post. A numbered camp nobody ticked has no close.
public class HnmStandardCloseWindowTests
{
    [Fact]
    public void ResolveCloseWindow_TickedWindow_Wins()
    {
        var posted = new[] { 1, 2, 3, 4 };
        Assert.Equal(3, HnmStandardCampFinalizer.ResolveCloseWindow(posted, markedCloseWindow: 3));
    }

    [Fact]
    public void ResolveCloseWindow_NumberedCampNothingTicked_HasNoClose()
    {
        // It used to fall back to the pop window, else the latest window posted. Now the officer
        // ticks the close, and the addon will not end the camp until they do.
        var posted = new[] { 1, 2, 3, 4 };
        Assert.Equal(0, HnmStandardCampFinalizer.ResolveCloseWindow(posted));
    }

    [Fact]
    public void ResolveCloseWindow_TwoPostCamp_IsTheLatestPost()
    {
        // A named Open/Close camp: pressing Post Close already said which post closes it.
        var posted = new[] { 1, 2 };
        Assert.Equal(2, HnmStandardCampFinalizer.ResolveCloseWindow(
            posted, markedCloseWindow: null, closeIsNamedPost: true));
    }

    [Fact]
    public void ResolveCloseWindow_NamedCampScansOutOfOrder_UsesHighest()
    {
        // Posts can arrive in any order (a corrected re-post of an earlier window).
        var posted = new[] { 2, 1 };
        Assert.Equal(2, HnmStandardCampFinalizer.ResolveCloseWindow(posted, closeIsNamedPost: true));
    }

    [Fact]
    public void ResolveCloseWindow_NoScans_IsZero()
    {
        // Nobody was ever scanned — there is no open and no close to pay.
        Assert.Equal(0, HnmStandardCampFinalizer.ResolveCloseWindow(
            Array.Empty<int>(), closeIsNamedPost: true));
    }

    [Fact]
    public void ResolveCloseWindow_NamedCampOpenOnly_LandsOnWindowOne()
    {
        // Only the Open posted so far. That answer is inert at payout: CloseWindowValue adds the
        // close to window 1 only when an officer TICKED it there.
        var posted = new[] { 1 };
        Assert.Equal(1, HnmStandardCampFinalizer.ResolveCloseWindow(posted, closeIsNamedPost: true));
    }
}
