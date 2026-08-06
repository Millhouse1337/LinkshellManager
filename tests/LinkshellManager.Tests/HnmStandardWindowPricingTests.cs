using LinkshellManagerDiscordApp.Services;
using Xunit;

namespace LinkshellManager.Tests;

// Pricing ONE window by hand — HnmStandardCampFinalizer.WindowValue and the per-window
// ComputeMemberDkp that sums it, isolated from the DB.
//
// The feature exists because the four linkshell bonuses price the SHAPE of a camp (an open, a
// close, a claim, a kill) and have no way to say "this particular window was worth something".
// An officer sets EventAttendanceWindow.DkpAmount from the Activity's card or the addon's
// "Dkp this window" box, and it REPLACES that window's default contribution.
//
// HnmStandardMemberGatingTests is the companion: it pins the override-free camp, which is still
// the default and still what almost every camp pays.
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
        int sequence, int closeWindow, double? explicitAmount = null, double windowBonus = 0d) =>
        HnmStandardCampFinalizer.WindowValue(
            sequence, closeWindow, explicitAmount, windowBonus, Open, Close);

    // ----- WindowValue: the camp's own default, nobody has priced anything -----

    [Fact]
    public void WindowValue_NoExplicitAmount_OpenWindowPaysOpenBonus()
    {
        // Window 1 of a camp that has posted through window 3.
        Assert.Equal(Open, Value(sequence: 1, closeWindow: 3), precision: 3);
    }

    [Fact]
    public void WindowValue_NoExplicitAmount_CloseWindowPaysCloseBonus()
    {
        Assert.Equal(Close, Value(sequence: 3, closeWindow: 3), precision: 3);
    }

    // A camp with exactly one posted window is its own open AND its own close, so the two add
    // rather than branch. This is what a fresh camp's window 1 looks like the moment it is posted.
    [Fact]
    public void WindowValue_NoExplicitAmount_SingleWindowIsBothOpenAndClose()
    {
        Assert.Equal(Open + Close, Value(sequence: 1, closeWindow: 1), precision: 3);
    }

    [Fact]
    public void WindowValue_NoExplicitAmount_MiddleWindowPaysNothing()
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

    // Zero is what a camp with no snapshots at all resolves to; nothing is open and nothing closes.
    [Fact]
    public void WindowValue_NoCloseResolved_PaysOnlyTheOpen()
    {
        Assert.Equal(Open, Value(sequence: 1, closeWindow: 0), precision: 3);
    }

    // ----- WindowValue: an officer priced it -----

    // THE precedence rule, and the one a future reader is most likely to get backwards. The
    // control is labelled "DKP this window"; a box showing 5 on a window that paid 5.5 would be
    // lying about its own name.
    [Fact]
    public void WindowValue_ExplicitAmount_ReplacesTheBonuses_DoesNotAddToThem()
    {
        // Window 1 IS the close here, so its default would be Open + Close = 2.0.
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

    // ----- ComputeMemberDkp: summing the windows a member was actually scanned in -----

    [Fact]
    public void ComputeMemberDkp_SumsEveryWindowTheMemberWasScannedIn()
    {
        // Scanned in three windows priced 1, 2 and 0.5. No claim, no kill.
        var dkp = HnmStandardCampFinalizer.ComputeMemberDkp(
            new[] { 1.0, 2.0, 0.5 }, atClose: false, claimBonus: Claim, killBonus: Kill, step: Quarter);
        Assert.Equal(3.5, dkp, precision: 3);
    }

    // THE feature. Someone who showed up for one middle window and left used to earn nothing; if
    // an officer prices that window, they earn it.
    [Fact]
    public void ComputeMemberDkp_PricedMiddleWindow_PaysTheMemberWhoWasOnlyThere()
    {
        var priced = Value(sequence: 2, closeWindow: 3, explicitAmount: 3.0);
        var dkp = HnmStandardCampFinalizer.ComputeMemberDkp(
            new[] { priced }, atClose: false, claimBonus: Claim, killBonus: Kill, step: Quarter);
        Assert.Equal(3.0, dkp, precision: 3);
    }

    // The regression guard. Pricing a window says what IT pays; it says nothing about whether the
    // camp was claimed or killed, and it must never turn a middle-window-only member into someone
    // who collects the outcome bonuses.
    [Fact]
    public void ComputeMemberDkp_ClaimAndKill_StayGatedOnTheCloseWindow_NotOnAPricedMiddleWindow()
    {
        var priced = Value(sequence: 2, closeWindow: 3, explicitAmount: 3.0);

        var midOnly = HnmStandardCampFinalizer.ComputeMemberDkp(
            new[] { priced }, atClose: false, claimBonus: Claim, killBonus: Kill, step: Quarter);
        var throughTheClose = HnmStandardCampFinalizer.ComputeMemberDkp(
            new[] { priced, Value(sequence: 3, closeWindow: 3) },
            atClose: true, claimBonus: Claim, killBonus: Kill, step: Quarter);

        Assert.Equal(3.0, midOnly, precision: 3);                       // priced window only
        Assert.Equal(3.0 + Close + Claim + Kill, throughTheClose, precision: 3);
    }

    // Pins that expressing the boolean form through the per-window sum moved no existing payout.
    // If this drifts, every camp nobody has priced by hand just changed what it pays.
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void ComputeMemberDkp_NoExplicitAmounts_MatchesTheBooleanOverload(bool atOpen, bool atClose)
    {
        var viaBooleans = HnmStandardCampFinalizer.ComputeMemberDkp(
            atOpen, atClose, Open, Close, Claim, Kill, Quarter);

        // The same member expressed as the windows they were scanned in, on a camp that closes on
        // window 3 — which is how BuildRosterAsync now computes it.
        var earned = new List<double>();
        if (atOpen) earned.Add(Value(sequence: 1, closeWindow: 3));
        if (atClose) earned.Add(Value(sequence: 3, closeWindow: 3));
        var viaWindows = HnmStandardCampFinalizer.ComputeMemberDkp(
            earned, atClose, Claim, Kill, Quarter);

        Assert.Equal(viaBooleans, viaWindows, precision: 3);
    }

    // Rounds the TOTAL, not each window. Snapping per window would let the grid multiply the error
    // by the window count — three 0.1 windows would round to 0 each and pay nothing at all.
    [Fact]
    public void ComputeMemberDkp_RoundsTheTotal_NotEachWindow()
    {
        var dkp = HnmStandardCampFinalizer.ComputeMemberDkp(
            new[] { 0.1, 0.1, 0.1 }, atClose: false, claimBonus: 0, killBonus: 0, step: Quarter);
        Assert.Equal(0.25, dkp, precision: 3);
    }

    // ----- The regular-window rate: what the windows IN BETWEEN pay -----
    //
    // Before this, a middle window was worth nothing unless an officer priced it one at a time, so
    // a wyrm camp sat for eight windows paid for the two ends of it and nothing else.

    private const double Regular = 0.25;

    [Fact]
    public void RegularWindowRate_MiddleWindow_NowPaysTheRate()
    {
        Assert.Equal(Regular, Value(sequence: 2, closeWindow: 3, windowBonus: Regular), precision: 3);
    }

    // The open and close ADD to the rate rather than replacing it — that is what makes them read as
    // bonuses. A camp paying 0.25 a window with a 0.5 open pays 0.75 for window 1, not 0.5.
    [Fact]
    public void RegularWindowRate_OpenAndCloseRideOnTopOfIt()
    {
        Assert.Equal(Regular + Open, Value(sequence: 1, closeWindow: 3, windowBonus: Regular), precision: 3);
        Assert.Equal(Regular + Close, Value(sequence: 3, closeWindow: 3, windowBonus: Regular), precision: 3);
        Assert.Equal(Regular + Open + Close,
            Value(sequence: 1, closeWindow: 1, windowBonus: Regular), precision: 3);
    }

    // An officer's explicit amount still REPLACES the whole thing, rate included. The box is
    // labelled "DKP this window" and has to mean it.
    [Fact]
    public void RegularWindowRate_ExplicitAmount_StillReplacesEverything()
    {
        Assert.Equal(5.0,
            Value(sequence: 1, closeWindow: 1, explicitAmount: 5.0, windowBonus: Regular), precision: 3);
    }

    // The migration guarantee: leaving the rate at 0 — which every existing linkshell does, since
    // the column defaults to 0 — pays exactly what the camp paid before the rate existed.
    [Theory]
    [InlineData(1, 3)]
    [InlineData(2, 3)]
    [InlineData(3, 3)]
    public void RegularWindowRate_AtZero_ChangesNoExistingPayout(int sequence, int closeWindow)
    {
        Assert.Equal(
            Value(sequence, closeWindow),
            Value(sequence, closeWindow, windowBonus: 0d),
            precision: 3);
    }

    // Where the rate actually shows up in a payout: someone scanned across a camp earns it for
    // EVERY window they were in, not once. Eight windows of a wyrm camp at 0.25 = 2.0, and the ends
    // add their bonuses on top.
    [Fact]
    public void RegularWindowRate_PaysPerWindowScanned()
    {
        var earned = new List<double>();
        for (var seq = 1; seq <= 8; seq++)
        {
            earned.Add(Value(seq, closeWindow: 8, windowBonus: Regular));
        }

        var dkp = HnmStandardCampFinalizer.ComputeMemberDkp(
            earned, atClose: true, claimBonus: 0, killBonus: 0, step: Quarter);

        Assert.Equal(8 * Regular + Open + Close, dkp, precision: 3);
    }
}
