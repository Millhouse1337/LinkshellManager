using LinkshellManagerDiscordApp.Services;
using Xunit;

namespace LinkshellManager.Tests;

// Pricing ONE window — HnmStandardCampFinalizer.WindowValue and the per-window ComputeMemberDkp
// that sums it, isolated from the DB.
//
// A window pays its BASE amount -- the open on window 1, the regular rate elsewhere, or an officer's
// hand-set price (EventAttendanceWindow.DkpAmount) -- plus the close bonus when it is the close. The
// Activity shows those as its DKP and Closing window columns. (Until 2026-11-01 the close bonus
// REPLACED the window's amount, and ticking the close wiped a hand-set price.)
//
// The close is only the window an officer ticked, or a named camp's Close post; a numbered camp
// nobody ticked has none. A close that only fell back onto window 1 (a named camp with just its Open
// posted) adds nothing -- window 1 pays open + close only when TICKED.
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

    // Window 1 pays the open and nothing else when the close is elsewhere, or there is none.
    [Fact]
    public void WindowValue_WindowOneOnly_PaysTheOpenAlone()
    {
        Assert.Equal(Open, Value(sequence: 1, closeWindow: 0), precision: 3);
        Assert.Equal(Open, Value(sequence: 1, closeWindow: 4), precision: 3);
    }

    // A close that only FELL BACK onto window 1 -- a named camp with just its Open posted -- adds
    // nothing. Otherwise posting the Open on a 1 open / 1 close linkshell would read as 2 DKP.
    [Fact]
    public void WindowValue_WindowOneIsAlsoTheClose_PaysTheOpenAlone()
    {
        Assert.Equal(Open, Value(sequence: 1, closeWindow: 1), precision: 3);
    }

    // Window 1 is a window like any other: it pays the regular rate, and the open bonus rides on
    // top of it. (It used to REPLACE the rate, so the open paid less than a plain window whenever
    // the rate was higher than the bonus.)
    [Fact]
    public void WindowValue_WindowOne_PaysTheRegularRatePlusTheOpen()
    {
        Assert.Equal(0.25 + Open, Value(sequence: 1, closeWindow: 1, windowBonus: 0.25), precision: 3);
    }

    // ...and when an officer TICKED window 1 as the closing window -- the camp that popped in its
    // opener -- it earns the close too: regular + open + close. Only the tick does this: the
    // fallback above is what every camp's first post looks like.
    [Fact]
    public void WindowValue_WindowOneTickedAsTheClose_PaysRegularPlusOpenPlusClose()
    {
        Assert.Equal(0.25 + Open + Close, HnmStandardCampFinalizer.WindowValue(
            sequence: 1, closeWindow: 1, explicitAmount: null, windowBonus: 0.25,
            openBonus: Open, closeBonus: Close, closeIsMarked: true), precision: 3);
        // An officer's price replaces the regular rate; the open and the ticked close still add.
        Assert.Equal(5.0 + Open + Close, HnmStandardCampFinalizer.WindowValue(
            sequence: 1, closeWindow: 1, explicitAmount: 5.0, windowBonus: 0d,
            openBonus: Open, closeBonus: Close, closeIsMarked: true), precision: 3);
    }

    // The tick names WHICH window closed the camp. A tick on window 3 says nothing about window 1.
    [Fact]
    public void WindowValue_CloseTickedOnALaterWindow_WindowOneStillPaysTheOpenAlone()
    {
        Assert.Equal(Open, HnmStandardCampFinalizer.WindowValue(
            sequence: 1, closeWindow: 3, explicitAmount: null, windowBonus: 0d,
            openBonus: Open, closeBonus: Close, closeIsMarked: true), precision: 3);
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

    // An officer's price replaces the window's OWN amount -- the regular rate -- and the open bonus
    // still rides on top of it on window 1. The close only fell back onto window 1 (nothing
    // ticked), so it adds nothing.
    [Fact]
    public void WindowValue_ExplicitAmountOnWindowOne_ReplacesTheRate_TheOpenStillAdds()
    {
        Assert.Equal(5.0 + Open, Value(sequence: 1, closeWindow: 1, explicitAmount: 5.0, windowBonus: 0.25), precision: 3);
    }

    // THE rule a future reader is most likely to get backwards: on the close, a hand-set price and
    // the close bonus ADD. The Activity shows them as two columns, and the window pays both.
    [Fact]
    public void WindowValue_ExplicitAmountOnTheClose_StillAddsTheCloseBonus()
    {
        Assert.Equal(2.0 + Close, Value(sequence: 3, closeWindow: 3, explicitAmount: 2.0), precision: 3);
    }

    // The three parts the Activity shows as its DKP, Open bonus and Closing window columns.
    [Fact]
    public void BaseOpenAndCloseValues_SumToTheWindowValue()
    {
        Assert.Equal(2.0, HnmStandardCampFinalizer.BaseWindowValue(2.0, 0.25), precision: 3);
        Assert.Equal(0.25, HnmStandardCampFinalizer.BaseWindowValue(null, 0.25), precision: 3);
        Assert.Equal(Open, HnmStandardCampFinalizer.OpenWindowValue(1, Open), precision: 3);
        Assert.Equal(0.0, HnmStandardCampFinalizer.OpenWindowValue(2, Open), precision: 3);
        Assert.Equal(0.0, HnmStandardCampFinalizer.OpenWindowValue(1, Open, isKillWindow: true), precision: 3);
        Assert.Equal(Close, HnmStandardCampFinalizer.CloseWindowValue(3, 3, Close), precision: 3);
        Assert.Equal(0.0, HnmStandardCampFinalizer.CloseWindowValue(2, 3, Close), precision: 3);
    }

    // An officer who deliberately zeroes a window must be able to make it stick. Treating 0 as
    // "unset" is exactly the bug the addon's migrations.lua carried, where a saved 0 was silently
    // rewritten to 1 on every reload. (On window 1 the open bonus still adds: zeroing the open is
    // the camp's open bonus, the Open bonus column, not the window's price.)
    [Fact]
    public void WindowValue_ExplicitZero_IsARealZero_NotUnset()
    {
        Assert.Equal(0.0, Value(sequence: 2, closeWindow: 3, explicitAmount: 0.0, windowBonus: 0.25), precision: 3);
        Assert.Equal(Open, Value(sequence: 1, closeWindow: 3, explicitAmount: 0.0, windowBonus: 0.25), precision: 3);
    }

    // The endpoints reject negatives, so this only guards a hand-edited row — but the finalizer
    // clamping rather than subtracting is what keeps one bad row from eating someone's balance.
    [Fact]
    public void WindowValue_NegativeExplicitAmount_IsClampedToZero()
    {
        Assert.Equal(0.0, Value(sequence: 2, closeWindow: 3, explicitAmount: -4.0), precision: 3);
    }

    // ----- ResolveCloseWindow: only the officer's tick, or a named camp's Close post -----

    [Fact]
    public void ResolveCloseWindow_MarkedWindow_Wins()
    {
        // Windows 1..4 posted and the officer ticked window 2. The tick wins, on a named camp too.
        Assert.Equal(2, HnmStandardCampFinalizer.ResolveCloseWindow(
            new[] { 1, 2, 3, 4 }, markedCloseWindow: 2));
        Assert.Equal(1, HnmStandardCampFinalizer.ResolveCloseWindow(
            new[] { 1, 2 }, markedCloseWindow: 1, closeIsNamedPost: true));
    }

    [Fact]
    public void ResolveCloseWindow_NothingMarked_OnlyANamedCampHasAClose()
    {
        // A numbered camp nobody ticked pays no close. It used to fall back to the pop window, else
        // the latest window posted.
        Assert.Equal(0, HnmStandardCampFinalizer.ResolveCloseWindow(new[] { 1, 2, 3, 4 }));
        // A named Open/Close camp's close is its latest post.
        Assert.Equal(2, HnmStandardCampFinalizer.ResolveCloseWindow(
            new[] { 1, 2 }, closeIsNamedPost: true));
        Assert.Equal(0, HnmStandardCampFinalizer.ResolveCloseWindow(
            System.Array.Empty<int>(), closeIsNamedPost: true));
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
            // Filed after the close. Left in a named camp's "latest post" it would steal the close
            // bonus off window 2 and hand it to the people who only turned up for the fight.
            new LinkshellManagerDiscordApp.Models.EventAttendanceWindow
            {
                SequenceNumber = 3, IsKillWindow = true
            },
        };

        Assert.Equal(2, HnmStandardCampFinalizer.ResolveCloseWindow(windows, closeIsNamedPost: true));
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

        Assert.Equal(1, HnmStandardCampFinalizer.ResolveCloseWindow(windows));
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

    // ----- PriceMembers: the whole camp, per account -----
    //
    // The pricing End Camp pays from AND the Activity card's "Event Total" projects with, so the
    // number a member is shown is the number they are paid.

    // One account across windows (in any casing) is one person; a tagger never scanned is owed the
    // claim alone.
    [Fact]
    public void PriceMembers_SumsPerAccount_AndPaysATaggerWithNoScan()
    {
        var scans = new[]
        {
            new HnmStandardCampFinalizer.MemberWindowScan("acct-1", 1, null, false, false),
            new HnmStandardCampFinalizer.MemberWindowScan("ACCT-1", 3, null, true, false),  // ticked close
            new HnmStandardCampFinalizer.MemberWindowScan("acct-1", 4, null, false, true),  // kill roster
            new HnmStandardCampFinalizer.MemberWindowScan("acct-2", 2, null, false, false), // middle window
        };
        var tagged = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "acct-1", "acct-3" };

        var pricing = HnmStandardCampFinalizer.PriceMembers(
            scans, tagged, closeIsNamedPost: false,
            windowBonus: 0d, openBonus: Open, closeBonus: Close,
            claimBonus: Claim, killBonus: Kill, step: Quarter);

        Assert.Equal(3, pricing.CloseWindow);
        Assert.Equal(Open + Close + Claim + Kill, pricing.DkpByAppUserId["acct-1"], precision: 3);
        Assert.Equal(0d, pricing.DkpByAppUserId["acct-2"], precision: 3);
        Assert.Equal(Claim, pricing.DkpByAppUserId["acct-3"], precision: 3);
    }

    // The camp that popped in its opener, ticked there: open + close through the camp path too.
    [Fact]
    public void PriceMembers_WindowOneTickedAsTheClose_PaysOpenPlusClose()
    {
        var scans = new[] { new HnmStandardCampFinalizer.MemberWindowScan("acct-1", 1, null, true, false) };

        var pricing = HnmStandardCampFinalizer.PriceMembers(
            scans, new HashSet<string>(), closeIsNamedPost: false,
            windowBonus: 0d, openBonus: Open, closeBonus: Close,
            claimBonus: Claim, killBonus: Kill, step: Quarter);

        Assert.Equal(Open + Close, pricing.DkpByAppUserId["acct-1"], precision: 3);
    }

    // A numbered camp nobody ticked: nobody earns the close, wherever the pop landed.
    [Fact]
    public void PriceMembers_NothingTicked_PaysNoCloseBonus()
    {
        var scans = new[]
        {
            new HnmStandardCampFinalizer.MemberWindowScan("acct-1", 1, null, false, false),
            new HnmStandardCampFinalizer.MemberWindowScan("acct-1", 3, null, false, false),
        };

        var pricing = HnmStandardCampFinalizer.PriceMembers(
            scans, new HashSet<string>(), closeIsNamedPost: false,
            windowBonus: 0d, openBonus: Open, closeBonus: Close,
            claimBonus: Claim, killBonus: Kill, step: Quarter);

        Assert.Equal(0, pricing.CloseWindow);
        Assert.Equal(Open, pricing.DkpByAppUserId["acct-1"], precision: 3);
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

    // Both bonuses ride on top of the rate: window 1 is still a window and adds the open, the close
    // window is still a window and adds the close.
    [Fact]
    public void RegularWindowRate_OpenAndCloseBothAddToIt()
    {
        Assert.Equal(Regular + Open, Value(sequence: 1, closeWindow: 3, windowBonus: Regular), precision: 3);
        Assert.Equal(Regular + Close, Value(sequence: 3, closeWindow: 3, windowBonus: Regular), precision: 3);
        // A close that only fell back onto window 1 adds nothing; the open still adds.
        Assert.Equal(Regular + Open, Value(sequence: 1, closeWindow: 1, windowBonus: Regular), precision: 3);
    }

    // An officer's explicit amount replaces the rate too.
    [Fact]
    public void RegularWindowRate_ExplicitAmount_ReplacesTheRate()
    {
        Assert.Equal(5.0,
            Value(sequence: 2, closeWindow: 3, explicitAmount: 5.0, windowBonus: Regular), precision: 3);
    }

    // Where the rate actually shows up in a payout: someone scanned across a camp earns it for
    // every window, and the open and close add their bonuses. Eight windows of a wyrm camp = eight
    // at the rate, the open bonus and the close bonus.
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

        Assert.Equal(8 * Regular + Open + Close, dkp, precision: 3);
    }
}
