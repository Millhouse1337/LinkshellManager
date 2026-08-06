using LinkshellManagerDiscordApp.Services;
using Xunit;

namespace LinkshellManager.Tests;

// Which bonuses ONE member actually earns from their scan presence — HnmStandardCampFinalizer's
// gating rule, isolated from the DB.
//
// Open needs the open window. Close, claim AND kill all need the close window: claim/kill reward
// the outcome, and the close window is where the pop happened. Before this gating existed, claim
// and kill were paid to anyone with a single scan anywhere in the camp.
//
// Every case here goes through the BOOLEAN ComputeMemberDkp overload, which since per-window
// pricing landed is a thin delegate onto the per-window sum. So this file now pins two things at
// once: the gating rule itself, and that the refactor to per-window values moved no existing
// payout. Both matter — do not "modernize" these onto the per-window overload, or the second
// guarantee is lost. See HnmStandardWindowPricingTests for the priced-window behaviour.
public class HnmStandardMemberGatingTests
{
    private const double Quarter = DkpRounding.QuarterStep; // 0.25

    private const double Open = 0.5;
    private const double Close = 0.5;
    private const double Claim = 1.0;
    private const double Kill = 2.0;

    private static double Member(bool atOpen, bool atClose) =>
        HnmStandardCampFinalizer.ComputeMemberDkp(atOpen, atClose, Open, Close, Claim, Kill, Quarter);

    // THE gap this gating closes. Someone scanned into one middle window — not the open, not the
    // close — used to collect the full claim + kill bonus, identical to the people who camped
    // every window through the kill.
    [Fact]
    public void MidCampOnly_PaysNothing_EvenWhenClaimedAndKilled()
    {
        Assert.Equal(0.0, Member(atOpen: false, atClose: false), precision: 3);
    }

    [Fact]
    public void AtCloseOnly_PaysCloseClaimAndKill()
    {
        // 0.5 close + 1.0 claim + 2.0 kill — they were there for the pop.
        Assert.Equal(3.5, Member(atOpen: false, atClose: true), precision: 3);
    }

    // Being at the open is NOT enough for the outcome bonuses — they left before the kill.
    [Fact]
    public void AtOpenOnly_PaysOpen_ButNotClaimOrKill()
    {
        Assert.Equal(0.5, Member(atOpen: true, atClose: false), precision: 3);
    }

    [Fact]
    public void AtOpenAndClose_PaysEverything()
    {
        Assert.Equal(4.0, Member(atOpen: true, atClose: true), precision: 3);
    }

    // A camp that was neither claimed nor killed passes 0 for both, so presence at the close only
    // earns the close bonus. (The finalizer zeroes them before calling; this pins that contract.)
    [Fact]
    public void UnclaimedUnkilledCamp_PaysOnlyPresenceBonuses()
    {
        var dkp = HnmStandardCampFinalizer.ComputeMemberDkp(
            atOpen: true, atClose: true, Open, Close, claimBonus: 0, killBonus: 0, step: Quarter);

        Assert.Equal(1.0, dkp, precision: 3);
    }

    // Nothing configured is the default for every linkshell — presence must never invent a payout.
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void NoBonusesConfigured_PaysNothing(bool atOpen, bool atClose)
    {
        var dkp = HnmStandardCampFinalizer.ComputeMemberDkp(atOpen, atClose, 0, 0, 0, 0, Quarter);
        Assert.Equal(0.0, dkp, precision: 3);
    }

    // The close window is resolved from the posted snapshots, so window 1 IS a legitimate close on
    // a camp that popped in its opener — one snapshot then earns open + close + claim + kill.
    [Fact]
    public void PoppedOnTheOpener_OneWindowEarnsEverything()
    {
        var close = HnmStandardCampFinalizer.ResolveCloseWindow(new[] { 1 }, popWindow: 1);
        Assert.Equal(1, close);

        var windows = new HashSet<int> { 1 };
        var dkp = HnmStandardCampFinalizer.ComputeMemberDkp(
            atOpen: windows.Contains(1), atClose: windows.Contains(close),
            Open, Close, Claim, Kill, Quarter);

        Assert.Equal(4.0, dkp, precision: 3);
    }

    // The mirror image of the case above, and the one the two-post camps actually needed: an
    // officer who reached camp only for the kill posts a Close and no Open. The lone window is
    // the close (nothing else was posted), so the camp still pays out — but there was no open
    // roster, so nobody earns the open bonus. Open is gated on window 1 SPECIFICALLY rather
    // than on "the earliest window posted", which is what makes this work.
    [Fact]
    public void CloseOnlyCamp_LateOfficerPostedOnlyTheClose_PaysNoOpenBonus()
    {
        var close = HnmStandardCampFinalizer.ResolveCloseWindow(new[] { 2 }, popWindow: 2);
        Assert.Equal(2, close);

        var windows = new HashSet<int> { 2 };
        var dkp = HnmStandardCampFinalizer.ComputeMemberDkp(
            atOpen: windows.Contains(1), atClose: windows.Contains(close),
            Open, Close, Claim, Kill, Quarter);

        // Everything the full camp pays, minus the open bonus that was never earned.
        Assert.Equal(4.0 - Open, dkp, precision: 3);
    }

    // A Close-only camp whose officer-stated pop window was never scanned still closes on the one
    // window that WAS posted (ResolveCloseWindow falls back to the highest). Without this the
    // close window would resolve to 0 and the lone roster would earn nothing at all.
    [Fact]
    public void CloseOnlyCamp_PopWindowNotScanned_StillClosesOnTheLoneWindow()
    {
        Assert.Equal(2, HnmStandardCampFinalizer.ResolveCloseWindow(new[] { 2 }, popWindow: 7));
    }

    // A Close-only camp is not a blanket payout: someone who was around but is absent from the
    // one posted roster earns nothing, exactly as on a camp with both windows.
    [Fact]
    public void CloseOnlyCamp_MemberNotInTheCloseScan_PaysNothing()
    {
        var close = HnmStandardCampFinalizer.ResolveCloseWindow(new[] { 2 }, popWindow: 2);
        var windows = new HashSet<int>();   // scanned nowhere

        Assert.Equal(0.0, HnmStandardCampFinalizer.ComputeMemberDkp(
            atOpen: windows.Contains(1), atClose: windows.Contains(close),
            Open, Close, Claim, Kill, Quarter), precision: 3);
    }
}
