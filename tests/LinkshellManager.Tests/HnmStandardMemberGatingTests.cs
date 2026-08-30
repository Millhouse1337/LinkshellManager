using LinkshellManagerDiscordApp.Services;
using Xunit;

namespace LinkshellManager.Tests;

// Which bonuses ONE member actually earns — HnmStandardCampFinalizer's gating rule, isolated from
// the DB.
//
// Four bonuses, four different pieces of evidence:
//   open  — scanned in window 1
//   close — scanned in the window an officer TICKED as the camp's close, and that window is not
//           window 1 (a window pays one amount, and window 1's is the open — see WindowValue)
//   claim — their name is on this camp's Claim Shield (they landed an action on the mob)
//   kill  — scanned in the Post Kill roster
//
// Claim and kill used to share the close window as their gate, because there was nothing better to
// gate them on. There is now, and splitting them is what lets a tagger who logged out before the
// pop keep their claim and a late arrival who only helped fight earn their kill.
//
// Every case here goes through the BOOLEAN ComputeMemberDkp overload, which is a thin delegate
// onto the per-window sum. So this file pins two things at once: the gating rule itself, and that
// the two forms agree. Both matter — do not "modernize" these onto the per-window overload, or the
// second guarantee is lost. See HnmStandardWindowPricingTests for the priced-window behaviour.
public class HnmStandardMemberGatingTests
{
    private const double Quarter = DkpRounding.QuarterStep; // 0.25

    private const double Open = 0.5;
    private const double Close = 0.5;
    private const double Claim = 1.0;
    private const double Kill = 2.0;

    private static double Member(bool atOpen, bool atClose, bool tagged = false, bool inKill = false) =>
        HnmStandardCampFinalizer.ComputeMemberDkp(
            atOpen, atClose, tagged, inKill, Open, Close, Claim, Kill, Quarter);

    // THE gap this gating closes. Someone scanned into one middle window — not the open, not the
    // close, never tagged the mob, not on the kill roster — earns nothing.
    [Fact]
    public void MidCampOnly_PaysNothing_EvenWhenClaimedAndKilled()
    {
        Assert.Equal(0.0, Member(atOpen: false, atClose: false), precision: 3);
    }

    // Presence at the close no longer buys the outcome bonuses. This is the behaviour CHANGE: it
    // used to pay close + claim + kill for exactly this input.
    [Fact]
    public void AtCloseOnly_PaysTheCloseAlone_NotClaimOrKill()
    {
        Assert.Equal(Close, Member(atOpen: false, atClose: true), precision: 3);
    }

    [Fact]
    public void AtOpenOnly_PaysOpen_ButNotClaimOrKill()
    {
        Assert.Equal(Open, Member(atOpen: true, atClose: false), precision: 3);
    }

    // Two windows attended is two payments — the open and the close are different windows.
    [Fact]
    public void AtOpenAndClose_PaysBothWindows()
    {
        Assert.Equal(Open + Close, Member(atOpen: true, atClose: true), precision: 3);
    }

    // ----- The outcome bonuses, now on their own evidence -----

    // Tagged the mob and left. Under the old rule this person earned nothing at all unless they
    // happened to be in the close scan.
    [Fact]
    public void TaggedButNeverScanned_StillEarnsTheClaim()
    {
        Assert.Equal(Claim, Member(atOpen: false, atClose: false, tagged: true), precision: 3);
    }

    // Turned up for the fight, never sat a window. The whole reason Post Kill files its own roster.
    [Fact]
    public void OnTheKillRosterOnly_EarnsTheKill()
    {
        Assert.Equal(Kill, Member(atOpen: false, atClose: false, inKill: true), precision: 3);
    }

    // The two are independent — one member can be either, both, or neither.
    [Fact]
    public void ClaimAndKill_AreIndependentOfEachOther()
    {
        Assert.Equal(Claim + Kill,
            Member(atOpen: false, atClose: false, tagged: true, inKill: true), precision: 3);
    }

    // Camped the whole thing, tagged it, and was there for the kill.
    [Fact]
    public void FullCamp_PaysEverything()
    {
        Assert.Equal(Open + Close + Claim + Kill,
            Member(atOpen: true, atClose: true, tagged: true, inKill: true), precision: 3);
    }

    // A camp that was neither claimed nor killed passes 0 for both bonuses, so even a tagger on the
    // kill roster earns only their window credit. The officer's End Camp outcome still decides IF;
    // the Claim Shield and the kill roster only decide WHO. (The finalizer zeroes them before
    // calling; this pins that contract.)
    [Fact]
    public void UnclaimedUnkilledCamp_PaysOnlyPresenceBonuses()
    {
        var dkp = HnmStandardCampFinalizer.ComputeMemberDkp(
            atOpen: true, atClose: true, tagged: true, inKillWindow: true,
            openBonus: Open, closeBonus: Close, claimBonus: 0, killBonus: 0, step: Quarter);

        Assert.Equal(Open + Close, dkp, precision: 3);
    }

    // Nothing configured is the default for every linkshell — presence must never invent a payout.
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void NoBonusesConfigured_PaysNothing(bool atOpen, bool atClose)
    {
        var dkp = HnmStandardCampFinalizer.ComputeMemberDkp(
            atOpen, atClose, tagged: true, inKillWindow: true, 0, 0, 0, 0, Quarter);
        Assert.Equal(0.0, dkp, precision: 3);
    }

    // ----- Where the close window comes from -----

    // A camp that popped in its opener. Window 1 resolves as the close as well as the open — and it
    // still pays the OPEN alone, because a window never pays two amounts. The outcome bonuses are
    // unaffected: they answer to the Claim Shield and the kill roster, not to the window count.
    //
    // Goes through WindowValue rather than the boolean Member helper, because the boolean form
    // models two DIFFERENT windows and this camp only ever had one — see the overload's own note.
    // The close, if the officer decides the camp earned it, is theirs to add from the review page.
    [Fact]
    public void PoppedOnTheOpener_TheOneWindowPaysTheOpen_NotOpenPlusClose()
    {
        var close = HnmStandardCampFinalizer.ResolveCloseWindow(new[] { 1 }, popWindow: 1);
        Assert.Equal(1, close);

        var dkp = HnmStandardCampFinalizer.ComputeMemberDkp(
            new[] { HnmStandardCampFinalizer.WindowValue(1, close, null, 0d, Open, Close) },
            tagged: true, inKillWindow: true, claimBonus: Claim, killBonus: Kill, step: Quarter);

        Assert.Equal(Open + Claim + Kill, dkp, precision: 3);
    }

    // The complaint that produced the rule, in the smallest form that reproduces it: a linkshell
    // running 1 open / 1 close, an officer posting the opening window and nothing else. That is one
    // window, so it pays one amount — the open — not the 2 the old both-ends rule handed out on
    // every camp's first post, before anyone had ticked a closing window.
    [Fact]
    public void OpenWindowPostedAlone_PaysTheOpenOnce_NotOpenPlusClose()
    {
        const double One = 1.0;
        var close = HnmStandardCampFinalizer.ResolveCloseWindow(new[] { 1 }, popWindow: 4);

        var dkp = HnmStandardCampFinalizer.ComputeMemberDkp(
            new[] { HnmStandardCampFinalizer.WindowValue(1, close, null, 0.5d, One, One) },
            tagged: false, inKillWindow: false, claimBonus: One, killBonus: One, step: Quarter);

        Assert.Equal(One, dkp, precision: 3);
    }

    // An officer who reached camp only for the kill posts a Close and no Open. The lone window is
    // the close (nothing else was posted), so the camp still pays out — but there was no open
    // roster, so nobody earns the open bonus. Open is gated on window 1 SPECIFICALLY rather than
    // on "the earliest window posted", which is what makes this work.
    [Fact]
    public void CloseOnlyCamp_LateOfficerPostedOnlyTheClose_PaysNoOpenBonus()
    {
        var close = HnmStandardCampFinalizer.ResolveCloseWindow(new[] { 2 }, popWindow: 2);
        Assert.Equal(2, close);

        var windows = new HashSet<int> { 2 };
        var dkp = Member(atOpen: windows.Contains(1), atClose: windows.Contains(close),
            tagged: true, inKill: true);

        // Everything the full camp pays, minus the open bonus that was never earned.
        Assert.Equal(Open + Close + Claim + Kill - Open, dkp, precision: 3);
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
    // one posted roster and never tagged earns nothing, exactly as on a camp with both windows.
    [Fact]
    public void CloseOnlyCamp_MemberNotInTheCloseScan_PaysNothing()
    {
        var close = HnmStandardCampFinalizer.ResolveCloseWindow(new[] { 2 }, popWindow: 2);
        var windows = new HashSet<int>();   // scanned nowhere

        Assert.Equal(0.0,
            Member(atOpen: windows.Contains(1), atClose: windows.Contains(close)), precision: 3);
    }
}
