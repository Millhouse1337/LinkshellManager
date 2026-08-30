using LinkshellManagerDiscordApp.Services;
using Xunit;

namespace LinkshellManager.Tests;

// Pricing ONE window — HnmStandardCampFinalizer.WindowValue and the per-window ComputeMemberDkp
// that sums it, isolated from the DB.
//
// A window pays ONE amount: the open, the close, or the regular window rate. They do not stack, and
// there is no exception — a window that is both ends of the camp pays the open, and the close on it
// is the officer's to award by hand. That is a reversal — the bonuses used to ADD to the rate, so
// window 1 paid rate + open — and it went with making the close an explicit officer mark instead of
// a derived "newest window posted". Together those were the bug: the derived close was QUOTED to
// the addon before every post, the addon wrote the quote back as an explicit price, and so every
// window on a camp ended up frozen holding a close bonus, with window 1 holding open + close + rate.
//
// The both-ends case was the last of that stacking to go: because the close FALLS BACK to the
// latest window posted, the opening post of every camp was also its close until a second one
// landed, so a 1 open / 1 close linkshell was quoted 1 and shown 2 for posting the open.
//
// An officer can still price a window by hand (EventAttendanceWindow.DkpAmount) and it REPLACES
// whichever of the three would otherwise apply.
//
// HnmStandardMemberGatingTests is the companion: it pins the gating of the four bonuses.
public class HnmStandardWindowPricingTests
{
    private const double Quarter = DkpRounding.QuarterStep; // 0.25

    private const double Open = 0.5;
    private const double Close = 1.5;
    private const double Claim = 1.0;
    private const double Kill = 2.0;

    // Every case below runs at windowBonus = 0 unless it says otherwise — the default, and the
    // shape every camp had before a regular-window rate existed. The RegularWindowRate_* block at
    // the bottom is where a non-zero one is exercised.
    private static double Value(
        int sequence, int closeWindow, double? explicitAmount = null, double windowBonus = 0d,
        bool isKillWindow = false) =>
        HnmStandardCampFinalizer.WindowValue(
            sequence, closeWindow, explicitAmount, windowBonus, Open, Close, isKillWindow);

    // ----- WindowValue: the camp's own default, nobody has priced anything -----

    [Fact]
    public void WindowValue_NoExplicitAmount_OpenWindowPaysOpenBonus()
    {
        // Window 1 of a camp whose close is window 3.
        Assert.Equal(Open, Value(sequence: 1, closeWindow: 3), precision: 3);
    }

    [Fact]
    public void WindowValue_NoExplicitAmount_CloseWindowPaysCloseBonus()
    {
        Assert.Equal(Close, Value(sequence: 3, closeWindow: 3), precision: 3);
    }

    // THE regression this whole change exists for. The open and the close are alternatives, not
    // addends: window 1 pays the open and nothing else. It used to pay open + close on any camp
    // whose close resolved to window 1 — which, under the old "newest window posted" derivation,
    // was every camp for as long as it had one window posted.
    [Fact]
    public void WindowValue_WindowOneOnly_PaysTheOpenAlone()
    {
        Assert.Equal(Open, Value(sequence: 1, closeWindow: 0), precision: 3);
        Assert.Equal(Open, Value(sequence: 1, closeWindow: 4), precision: 3);
    }

    // The window that is BOTH ends of the camp — a single-post NM claimed and dead in one roster
    // read, or a camp that popped in its opener. It pays the OPEN, alone. There is no case in which
    // one window pays two amounts.
    //
    // This used to pay open + close, on the reasoning that the camp genuinely earned both. What
    // that missed is ResolveCloseWindow's fallback: with nothing ticked, the close is "the latest
    // window posted", so the opening post of EVERY camp is also its close for as long as it is the
    // only window. A linkshell running 1 open / 1 close saw 2 DKP for posting the open. The camp
    // that really did open and close at once is now the officer's to settle by hand.
    [Fact]
    public void WindowValue_WindowOneIsAlsoTheClose_PaysTheOpenAlone()
    {
        Assert.Equal(Open, Value(sequence: 1, closeWindow: 1), precision: 3);
    }

    // ...and the regular-window rate does not join in either. Window 1 is the open, and the open
    // replaces the rate rather than riding on top of it.
    [Fact]
    public void WindowValue_WindowOneIsAlsoTheClose_DoesNotAlsoPayTheRegularRate()
    {
        Assert.Equal(Open, Value(sequence: 1, closeWindow: 1, windowBonus: 0.25), precision: 3);
    }

    [Fact]
    public void WindowValue_NoExplicitAmount_MiddleWindowPaysNothingWithoutARate()
    {
        Assert.Equal(0.0, Value(sequence: 2, closeWindow: 3), precision: 3);
    }

    // The open is gated on window 1 SPECIFICALLY, not on "the earliest window posted". A camp can
    // hold a Close with no Open (an officer who only reached camp for the kill posts one window,
    // which the addon files as 2), and treating the earliest posted window as the open would hand
    // that roster an open bonus for a roster nobody ever observed at the open.
    [Fact]
    public void WindowValue_CloseOnlyCamp_PaysCloseWithNoOpen()
    {
        Assert.Equal(Close, Value(sequence: 2, closeWindow: 2), precision: 3);
    }

    // closeWindow 0 is what a camp with no snapshots — or none marked and none posted — resolves
    // to. Nothing closes, so nothing pays the close.
    [Fact]
    public void WindowValue_NoCloseResolved_NoWindowPaysTheCloseBonus()
    {
        Assert.Equal(Open, Value(sequence: 1, closeWindow: 0), precision: 3);
        Assert.Equal(0.0, Value(sequence: 5, closeWindow: 0), precision: 3);
    }

    // ----- WindowValue: the Post Kill roster -----

    // Worth 0 AS A WINDOW at every sequence, rate or no rate. It is not a roster read of the camp;
    // it is who was standing there when the mob died, and the kill bonus is what pays for that.
    // Pricing it as a window on top would pay the late arrivals twice for one appearance.
    [Fact]
    public void WindowValue_KillWindow_PaysNothingAsAWindow()
    {
        Assert.Equal(0.0, Value(sequence: 3, closeWindow: 2, isKillWindow: true), precision: 3);
        Assert.Equal(0.0,
            Value(sequence: 3, closeWindow: 2, windowBonus: 0.25, isKillWindow: true), precision: 3);
    }

    // Even at sequence 1, and even if something managed to mark it as the close — the flags cannot
    // be set together through any endpoint, and this pins that the pricing agrees.
    [Fact]
    public void WindowValue_KillWindow_BeatsBothPositionalBonuses()
    {
        // Sequence 1 marked as the close would otherwise pay the Open.
        Assert.Equal(0.0, Value(sequence: 1, closeWindow: 1, isKillWindow: true), precision: 3);
    }

    // ----- WindowValue: an officer priced it -----

    // THE precedence rule, and the one a future reader is most likely to get backwards. The
    // control is labelled "DKP this window"; a box showing 5 on a window that paid 5.5 would be
    // lying about its own name.
    [Fact]
    public void WindowValue_ExplicitAmount_ReplacesTheBonuses_DoesNotAddToThem()
    {
        // Window 1 IS the close here, so its default would be the Open.
        Assert.Equal(5.0, Value(sequence: 1, closeWindow: 1, explicitAmount: 5.0), precision: 3);
    }

    // An officer who deliberately zeroes the Open must be able to make it stick. Treating 0 as
    // "unset" is exactly the bug the addon's migrations.lua carried, where a saved 0 was silently
    // rewritten to 1 on every reload.
    [Fact]
    public void WindowValue_ExplicitZero_IsARealZero_NotUnset()
    {
        Assert.Equal(0.0, Value(sequence: 1, closeWindow: 1, explicitAmount: 0.0), precision: 3);
    }

    // The endpoints reject negatives, so this only guards a hand-edited row — but the finalizer
    // clamping rather than subtracting is what keeps one bad row from eating someone's balance.
    [Fact]
    public void WindowValue_NegativeExplicitAmount_IsClampedToZero()
    {
        Assert.Equal(0.0, Value(sequence: 2, closeWindow: 3, explicitAmount: -4.0), precision: 3);
    }

    // ----- ResolveCloseWindow: the officer's tick beats the derivation -----

    [Fact]
    public void ResolveCloseWindow_MarkedWindow_WinsOverTheDerivation()
    {
        // Windows 1..4 posted, the pop landed in 4, but the officer ticked window 2. The tick wins:
        // it is a statement about the camp, not a guess to be second-guessed.
        Assert.Equal(2, HnmStandardCampFinalizer.ResolveCloseWindow(
            new[] { 1, 2, 3, 4 }, popWindow: 4, markedCloseWindow: 2));
    }

    [Fact]
    public void ResolveCloseWindow_NothingMarked_FallsBackToTheOldDerivation()
    {
        // Kept so camps that predate the checkbox — and camps where nobody ticked it — still pay a
        // close instead of silently paying none.
        Assert.Equal(4, HnmStandardCampFinalizer.ResolveCloseWindow(
            new[] { 1, 2, 3, 4 }, popWindow: 4));
        Assert.Equal(2, HnmStandardCampFinalizer.ResolveCloseWindow(
            new[] { 2 }, popWindow: 7));
        Assert.Equal(0, HnmStandardCampFinalizer.ResolveCloseWindow(
            System.Array.Empty<int>(), popWindow: 3));
    }

    // The row overload is what every caller outside the finalizer uses, and it owns the two filters
    // that make the answer right: find the tick, drop the kill rosters.
    [Fact]
    public void ResolveCloseWindow_OverRows_IgnoresKillWindows()
    {
        var windows = new[]
        {
            new LinkshellManagerDiscordApp.Models.EventAttendanceWindow { SequenceNumber = 1 },
            new LinkshellManagerDiscordApp.Models.EventAttendanceWindow { SequenceNumber = 2 },
            // Filed after the close. Left in the derivation it would steal the close bonus off
            // window 2 and hand it to the people who only turned up for the fight.
            new LinkshellManagerDiscordApp.Models.EventAttendanceWindow
            {
                SequenceNumber = 3, IsKillWindow = true
            },
        };

        Assert.Equal(2, HnmStandardCampFinalizer.ResolveCloseWindow(windows, popWindow: 3));
    }

    [Fact]
    public void ResolveCloseWindow_OverRows_TakesTheTick()
    {
        var windows = new[]
        {
            new LinkshellManagerDiscordApp.Models.EventAttendanceWindow
            {
                SequenceNumber = 1, IsClosingWindow = true
            },
            new LinkshellManagerDiscordApp.Models.EventAttendanceWindow { SequenceNumber = 2 },
            new LinkshellManagerDiscordApp.Models.EventAttendanceWindow { SequenceNumber = 3 },
        };

        Assert.Equal(1, HnmStandardCampFinalizer.ResolveCloseWindow(windows, popWindow: 3));
    }

    // ----- ComputeMemberDkp: summing the windows a member was actually scanned in -----

    [Fact]
    public void ComputeMemberDkp_SumsEveryWindowTheMemberWasScannedIn()
    {
        // Scanned in three windows priced 1, 2 and 0.5. No claim, no kill.
        var dkp = HnmStandardCampFinalizer.ComputeMemberDkp(
            new[] { 1.0, 2.0, 0.5 }, tagged: false, inKillWindow: false,
            claimBonus: Claim, killBonus: Kill, step: Quarter);
        Assert.Equal(3.5, dkp, precision: 3);
    }

    // Attending two windows is two payments. That is not the stacking WindowValue forbids —
    // WindowValue is about one window never paying two amounts.
    [Fact]
    public void ComputeMemberDkp_OpenAndCloseAreSeparateWindows_SoTheyBothPay()
    {
        var dkp = HnmStandardCampFinalizer.ComputeMemberDkp(
            new[] { Value(sequence: 1, closeWindow: 3), Value(sequence: 3, closeWindow: 3) },
            tagged: false, inKillWindow: false, claimBonus: 0, killBonus: 0, step: Quarter);
        Assert.Equal(Open + Close, dkp, precision: 3);
    }

    // THE per-window feature. Someone who showed up for one middle window and left used to earn
    // nothing; if an officer prices that window, they earn it.
    [Fact]
    public void ComputeMemberDkp_PricedMiddleWindow_PaysTheMemberWhoWasOnlyThere()
    {
        var priced = Value(sequence: 2, closeWindow: 3, explicitAmount: 3.0);
        var dkp = HnmStandardCampFinalizer.ComputeMemberDkp(
            new[] { priced }, tagged: false, inKillWindow: false,
            claimBonus: Claim, killBonus: Kill, step: Quarter);
        Assert.Equal(3.0, dkp, precision: 3);
    }

    // The regression guard. Pricing a window says what IT pays; it says nothing about whether the
    // member tagged the mob or was there for the kill, and it must never turn a
    // middle-window-only member into someone who collects the outcome bonuses.
    [Fact]
    public void ComputeMemberDkp_ClaimAndKill_AreNotEarnedByPricingAMiddleWindow()
    {
        var priced = Value(sequence: 2, closeWindow: 3, explicitAmount: 3.0);

        var midOnly = HnmStandardCampFinalizer.ComputeMemberDkp(
            new[] { priced }, tagged: false, inKillWindow: false,
            claimBonus: Claim, killBonus: Kill, step: Quarter);
        var taggedAndKilled = HnmStandardCampFinalizer.ComputeMemberDkp(
            new[] { priced, Value(sequence: 3, closeWindow: 3) },
            tagged: true, inKillWindow: true, claimBonus: Claim, killBonus: Kill, step: Quarter);

        Assert.Equal(3.0, midOnly, precision: 3);                       // priced window only
        Assert.Equal(3.0 + Close + Claim + Kill, taggedAndKilled, precision: 3);
    }

    // The two outcome gates are INDEPENDENT, which is the point of splitting them. A tagger who
    // logged out before the pop earns the claim and not the kill; a late arrival who only helped
    // fight earns the kill and not the claim. Under the old rule both were "scanned in the close
    // window", so each of these people got both or neither.
    [Fact]
    public void ComputeMemberDkp_ClaimAndKill_AreGatedSeparately()
    {
        var taggerOnly = HnmStandardCampFinalizer.ComputeMemberDkp(
            System.Array.Empty<double>(), tagged: true, inKillWindow: false,
            claimBonus: Claim, killBonus: Kill, step: Quarter);
        var killerOnly = HnmStandardCampFinalizer.ComputeMemberDkp(
            System.Array.Empty<double>(), tagged: false, inKillWindow: true,
            claimBonus: Claim, killBonus: Kill, step: Quarter);

        Assert.Equal(Claim, taggerOnly, precision: 3);
        Assert.Equal(Kill, killerOnly, precision: 3);
    }

    // Pins that the boolean form and the per-window sum agree. If this drifts, every camp nobody
    // has priced by hand just changed what it pays.
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void ComputeMemberDkp_NoExplicitAmounts_MatchesTheBooleanOverload(bool atOpen, bool atClose)
    {
        var viaBooleans = HnmStandardCampFinalizer.ComputeMemberDkp(
            atOpen, atClose, tagged: false, inKillWindow: false,
            openBonus: Open, closeBonus: Close, claimBonus: Claim, killBonus: Kill, step: Quarter);

        // The same member expressed as the windows they were scanned in, on a camp that closes on
        // window 3 — which is how BuildRosterAsync computes it.
        var earned = new List<double>();
        if (atOpen) earned.Add(Value(sequence: 1, closeWindow: 3));
        if (atClose) earned.Add(Value(sequence: 3, closeWindow: 3));
        var viaWindows = HnmStandardCampFinalizer.ComputeMemberDkp(
            earned, tagged: false, inKillWindow: false,
            claimBonus: Claim, killBonus: Kill, step: Quarter);

        Assert.Equal(viaBooleans, viaWindows, precision: 3);
    }

    // Rounds the TOTAL, not each window. Snapping per window would let the grid multiply the error
    // by the window count — three 0.1 windows would round to 0 each and pay nothing at all.
    [Fact]
    public void ComputeMemberDkp_RoundsTheTotal_NotEachWindow()
    {
        var dkp = HnmStandardCampFinalizer.ComputeMemberDkp(
            new[] { 0.1, 0.1, 0.1 }, tagged: false, inKillWindow: false,
            claimBonus: 0, killBonus: 0, step: Quarter);
        Assert.Equal(0.25, dkp, precision: 3);
    }

    // ----- The regular-window rate: what the windows IN BETWEEN pay -----
    //
    // Before this, a middle window was worth nothing unless an officer priced it one at a time, so
    // a wyrm camp sat for eight windows paid for the two ends of it and nothing else.

    private const double Regular = 0.25;

    [Fact]
    public void RegularWindowRate_MiddleWindow_PaysTheRate()
    {
        Assert.Equal(Regular, Value(sequence: 2, closeWindow: 3, windowBonus: Regular), precision: 3);
    }

    // The rate is what a window pays when it is NEITHER end. The open and the close replace it
    // rather than riding on top — one amount per window.
    [Fact]
    public void RegularWindowRate_OpenAndCloseReplaceIt_TheyDoNotRideOnTopOfIt()
    {
        Assert.Equal(Open, Value(sequence: 1, closeWindow: 3, windowBonus: Regular), precision: 3);
        Assert.Equal(Close, Value(sequence: 3, closeWindow: 3, windowBonus: Regular), precision: 3);
        // Both ends at once still excludes the rate — it is what a window pays when it is NEITHER.
        Assert.Equal(Open, Value(sequence: 1, closeWindow: 1, windowBonus: Regular), precision: 3);
    }

    // An officer's explicit amount still REPLACES the whole thing, rate included. The box is
    // labelled "DKP this window" and has to mean it.
    [Fact]
    public void RegularWindowRate_ExplicitAmount_StillReplacesEverything()
    {
        Assert.Equal(5.0,
            Value(sequence: 1, closeWindow: 1, explicitAmount: 5.0, windowBonus: Regular), precision: 3);
    }

    // Where the rate actually shows up in a payout: someone scanned across a camp earns it for
    // every MIDDLE window they were in. Eight windows of a wyrm camp = the open, six at the rate,
    // and the close.
    [Fact]
    public void RegularWindowRate_PaysPerMiddleWindowScanned()
    {
        var earned = new List<double>();
        for (var seq = 1; seq <= 8; seq++)
        {
            earned.Add(Value(seq, closeWindow: 8, windowBonus: Regular));
        }

        var dkp = HnmStandardCampFinalizer.ComputeMemberDkp(
            earned, tagged: false, inKillWindow: false, claimBonus: 0, killBonus: 0, step: Quarter);

        Assert.Equal(Open + 6 * Regular + Close, dkp, precision: 3);
    }
}
